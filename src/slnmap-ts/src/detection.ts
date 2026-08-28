import ts from 'typescript';
import { isConstRootedOrNotApplicable, unwrap } from './resolve.js';

/**
 * The verb-shaped method names the detection set recognizes, mapped to the verb they report.
 * `del` is superagent's own official method name for DELETE (not an app-specific quirk — verified
 * against a real codebase during the pre-publish field trial, reports/analyze-ts-field-trial.md: a
 * superagent-wrapped API layer's four `.del(...)` call sites were silently invisible to the tool —
 * not even counted as unresolved — before this was recognized as an alias). Lives here (not
 * walk.ts, which is the only other place it's used) so `resolveHttpWrapper` below can recognize
 * the same verb shapes inside a wrapper function's own body without an import cycle.
 */
export const HTTP_VERB_METHODS = new Map([
  ['get', 'GET'],
  ['post', 'POST'],
  ['put', 'PUT'],
  ['delete', 'DELETE'],
  ['del', 'DELETE'],
  ['patch', 'PATCH'],
]);

/**
 * Call-site detection set — investigation §Q3.1. Detection resolves through the TYPE CHECKER,
 * never AST-text/name guessing: the spike's "5 distinct import spellings resolving to one
 * client" case (frontend-feasibility-spike.md §1) must work regardless of which of N import
 * spellings a call site uses, because `getAliasedSymbol` follows re-export/rename chains back to
 * the one real declaration.
 */

/** Follows a symbol through any number of alias hops (imports, re-exports, barrel renames) to
 * its original declaring symbol. */
function resolveAlias(symbol: ts.Symbol, checker: ts.TypeChecker): ts.Symbol {
  let current = symbol;
  for (let hops = 0; hops < 32 && (current.flags & ts.SymbolFlags.Alias) !== 0; hops++) {
    let next: ts.Symbol;
    try {
      next = checker.getAliasedSymbol(current);
    } catch {
      break; // unresolved external (e.g. an untyped module) — stop where we are
    }
    if (next === current) {
      break;
    }
    current = next;
  }
  return current;
}

/** True for `<expr>.create(...)` where `<expr>` is bound to the default/namespace import of the
 * `axios` module specifier — checked via the import declaration's module-specifier text, which
 * is available even when axios's own type declarations cannot be resolved (the spike ran with no
 * node_modules installed at all; this detection must not depend on resolving axios's real types). */
function isAxiosCreateCall(expr: ts.Expression, checker: ts.TypeChecker): boolean {
  if (!ts.isCallExpression(expr)) {
    return false;
  }
  const callee = expr.expression;
  if (!ts.isPropertyAccessExpression(callee) || callee.name.text !== 'create') {
    return false;
  }
  const receiverSymbol = checker.getSymbolAtLocation(callee.expression);
  if (!receiverSymbol) {
    return false;
  }
  return (
    importedModuleSpecifierText(receiverSymbol) === 'axios' ||
    importedModuleSpecifierText(resolveAlias(receiverSymbol, checker)) === 'axios'
  );
}

export function importedModuleSpecifierText(symbol: ts.Symbol): string | undefined {
  for (const decl of symbol.declarations ?? []) {
    if (
      ts.isImportClause(decl) &&
      ts.isImportDeclaration(decl.parent) &&
      ts.isStringLiteral(decl.parent.moduleSpecifier)
    ) {
      return decl.parent.moduleSpecifier.text; // `import axios from 'axios'`
    }
    if (ts.isNamespaceImport(decl)) {
      const importDecl = decl.parent.parent;
      if (ts.isImportDeclaration(importDecl) && ts.isStringLiteral(importDecl.moduleSpecifier)) {
        return importDecl.moduleSpecifier.text; // `import * as axios from 'axios'`
      }
    }
    if (ts.isImportSpecifier(decl)) {
      const importDecl = decl.parent.parent.parent;
      if (ts.isImportDeclaration(importDecl) && ts.isStringLiteral(importDecl.moduleSpecifier)) {
        return importDecl.moduleSpecifier.text; // `import { default as axios } from 'axios'`
      }
    }
  }
  return undefined;
}

/**
 * Whether `expr` (an HTTP-verb-method-call receiver, e.g. the `apiClient` in `apiClient.get(...)`)
 * resolves — through however many barrel/rename hops — back to an `axios.create(...)` instance.
 * On-demand, not a pre-built registry: resolves the ORIGINAL declaration behind the alias chain
 * and inspects it directly, so a barrel's default->named rename (investigation §Q3.1) and a
 * direct `export default axios.create(...)` both resolve without separate handling.
 */
export function isKnownHttpClient(expr: ts.Expression, checker: ts.TypeChecker): boolean {
  const symbol = checker.getSymbolAtLocation(expr);
  if (!symbol) {
    return false;
  }
  const resolved = resolveAlias(symbol, checker);
  const decl = resolved.valueDeclaration ?? resolved.declarations?.[0];
  if (!decl) {
    return false;
  }
  if (ts.isVariableDeclaration(decl) && decl.initializer) {
    return isAxiosCreateCall(decl.initializer, checker);
  }
  if (ts.isExportAssignment(decl) && !decl.isExportEquals) {
    return isAxiosCreateCall(decl.expression, checker);
  }
  return false;
}

/**
 * True when `expr` resolves (through alias hops) to the default/namespace import binding of the
 * `axios` or `superagent` module ITSELF — a raw `axios.get(...)` / `superagent.post(...)` call
 * that never goes through `axios.create()`. Deliberately narrower than `isKnownHttpClient`: it
 * exists only for `resolveHttpWrapper` below, to recognize the low-level client call made INSIDE
 * a locally-declared wrapper function's own body (the real `agent.js` shape from
 * `gothinkster/react-redux-realworld-example-app`: `requests.get = url => superagent.get(...)`)
 * — not as a general top-level call-site receiver check, where an un-`.create()`'d client import
 * would be far too permissive (every module-level `.get`/`.post` etc. on the raw import would
 * qualify, reintroducing the `response.headers.get()`-style noise `isUrlShaped` already guards
 * against at the outer call-site level).
 */
function isKnownHttpClientModule(expr: ts.Expression, checker: ts.TypeChecker): boolean {
  const symbol = checker.getSymbolAtLocation(expr);
  if (!symbol) {
    return false;
  }
  const spec = importedModuleSpecifierText(symbol) ?? importedModuleSpecifierText(resolveAlias(symbol, checker));
  if (spec === 'axios' || spec === 'superagent') {
    return true;
  }
  return isKnownHttpClientWrapperCall(symbol, checker);
}

/**
 * True when `symbol` is a const variable initialized by a CALL that passes a known low-level
 * HTTP client module (axios/superagent) directly as one of ITS OWN arguments — the real,
 * verified-against-the-corpus shape in `gothinkster/react-redux-realworld-example-app`'s actual
 * `src/agent.js` (2026-08-28 field trial): `const superagent = superagentPromise(_superagent,
 * global.Promise)` — `superagent` there is not literally the raw import, it is promisified one
 * hop earlier by the `superagent-promise` package. Deliberately NOT a name-check on
 * `superagent-promise` specifically (never guess by name/library) — any thin call wrapper that
 * takes the real client module as a direct argument qualifies structurally, which is what that
 * package and any equivalent promisifying wrapper share.
 */
function isKnownHttpClientWrapperCall(symbol: ts.Symbol, checker: ts.TypeChecker): boolean {
  const decl = symbol.valueDeclaration ?? symbol.declarations?.[0];
  if (!decl || !ts.isVariableDeclaration(decl) || !decl.initializer) {
    return false;
  }
  const declList = decl.parent;
  if (!ts.isVariableDeclarationList(declList) || (declList.flags & ts.NodeFlags.Const) === 0) {
    return false; // same const-rootedness discipline as everywhere else in this file
  }
  const init = unwrap(decl.initializer);
  if (!ts.isCallExpression(init)) {
    return false;
  }
  return init.arguments.some((arg) => {
    const argSymbol = checker.getSymbolAtLocation(unwrap(arg));
    if (!argSymbol) {
      return false;
    }
    const spec = importedModuleSpecifierText(argSymbol) ?? importedModuleSpecifierText(resolveAlias(argSymbol, checker));
    return spec === 'axios' || spec === 'superagent';
  });
}

/**
 * True only when `identifier` (already known to be textually named `fetch`) resolves to the
 * ambient `lib.dom.d.ts` global declaration — never a local variable/parameter/destructured
 * callback that happens to share the name (the spike's `useExportPreview.ts` shadowed-`fetch`
 * false-positive, frontend-feasibility-spike.md §1). A shadowed local is proven NOT an HTTP call
 * (positive evidence), so it is silently excluded rather than reported unresolved.
 */
export function isAmbientGlobalFetch(identifier: ts.Identifier, checker: ts.TypeChecker): boolean {
  const symbol = checker.getSymbolAtLocation(identifier);
  if (!symbol) {
    return false;
  }
  return (symbol.declarations ?? []).some((decl) => {
    const fileName = decl.getSourceFile().fileName.replace(/\\/g, '/');
    return fileName.includes('/lib.dom');
  });
}

/**
 * A function/arrow-function/method shape that could be a wrapper's implementation — the common
 * factor `resolveHttpWrapper` needs from any of the four declaration shapes a verb-named object
 * property can take.
 */
type WrapperFunctionLike = ts.ArrowFunction | ts.FunctionExpression | ts.MethodDeclaration | ts.FunctionDeclaration;

/** The confirmed result of tracing a call through one locally-declared wrapper: the wrapper's own
 * first-parameter symbol (what a call site's argument substitutes for), the URL-argument
 * expression of the recognized low-level HTTP-client call found inside the wrapper's body, and
 * that call itself (`forwardedCall`) — exposed so callers can suppress it from being
 * independently classified as its OWN call site (walk.ts's `collectWrapperInternalCalls`): it is
 * wrapper-internal plumbing whose meaning is already folded into whichever outer, application-
 * level call site(s) go through this wrapper, not a call site in its own right. */
export interface WrapperForward {
  paramSymbol: ts.Symbol;
  urlArg: ts.Expression;
  forwardedCall: ts.CallExpression;
}

function wrapperFunctionFromDeclaration(decl: ts.Declaration | undefined, checker: ts.TypeChecker, hops = 4): WrapperFunctionLike | undefined {
  if (!decl || hops <= 0) {
    return undefined;
  }
  // `const requests = { get: url => ... }` — a property, arrow/function-expression-valued.
  if (ts.isPropertyAssignment(decl) && (ts.isArrowFunction(decl.initializer) || ts.isFunctionExpression(decl.initializer))) {
    return decl.initializer;
  }
  // `const requests = { get: httpGet }` — a property referencing a separately-declared wrapper
  // function BY NAME (shorthand-by-reference, not inlined) — one more const-rooted hop to the
  // function it actually names, e.g. the real `agent.js`-adjacent `const wrapper = { get: httpGet }`
  // shape. Still exactly one level of receiver indirection for the CALL SITE — this only follows
  // how the wrapper's own single implementation happens to be spelled.
  if (ts.isPropertyAssignment(decl) && ts.isIdentifier(decl.initializer)) {
    if (!isConstRootedOrNotApplicable(decl.initializer, checker)) {
      return undefined;
    }
    const identSymbol = checker.getSymbolAtLocation(decl.initializer);
    if (!identSymbol) {
      return undefined;
    }
    const symbol = resolveAlias(identSymbol, checker);
    return wrapperFunctionFromDeclaration(symbol.valueDeclaration ?? symbol.declarations?.[0], checker, hops - 1);
  }
  // `const requests = { get(url) { ... } }` — object-literal shorthand method syntax.
  if (ts.isMethodDeclaration(decl) && decl.body) {
    return decl;
  }
  // `const httpGet = (url) => ...` — a standalone wrapper function, not behind an object literal.
  if (ts.isVariableDeclaration(decl) && decl.initializer && (ts.isArrowFunction(decl.initializer) || ts.isFunctionExpression(decl.initializer))) {
    return decl.initializer;
  }
  if (ts.isFunctionDeclaration(decl) && decl.body) {
    return decl;
  }
  return undefined;
}

/**
 * The wrapper function's single, clean forwarding expression — an arrow's concise body, or a
 * block with EXACTLY one `return <expr>;` statement. Anything else (multiple statements, an
 * `if`/`else`, a reassignment before the call, no return at all) means the wrapper doesn't
 * cleanly forward and `undefined` is returned — the caller falls back to today's disclosed
 * `unrecognized-callee`, never a guess.
 */
function wrapperReturnExpression(fn: WrapperFunctionLike): ts.Expression | undefined {
  const body = fn.body;
  if (!body) {
    return undefined;
  }
  if (!ts.isBlock(body)) {
    return body; // arrow concise body: `url => expr`
  }
  if (body.statements.length !== 1) {
    return undefined;
  }
  const statement = body.statements[0]!;
  return ts.isReturnStatement(statement) && statement.expression ? statement.expression : undefined;
}

/**
 * Walks down a FLUENT CHAIN (`superagent.get(url).use(tokenPlugin).then(responseBody)`) looking
 * for the one link that is itself a recognized low-level HTTP-client call — descending only
 * through each link's own receiver (never into unrelated argument expressions, which would risk
 * matching an unrelated client call buried elsewhere and turning "this wrapper forwards" into a
 * guess). Also unwraps a single leading `await`.
 */
function findChainedRecognizedCall(expr: ts.Expression, checker: ts.TypeChecker): ts.CallExpression | undefined {
  let e = unwrap(expr);
  if (ts.isAwaitExpression(e)) {
    e = unwrap(e.expression);
  }
  if (!ts.isCallExpression(e)) {
    return undefined;
  }
  const callee = e.expression;
  if (ts.isIdentifier(callee) && callee.text === 'fetch' && isAmbientGlobalFetch(callee, checker)) {
    return e;
  }
  if (ts.isPropertyAccessExpression(callee)) {
    if (
      HTTP_VERB_METHODS.has(callee.name.text) &&
      (isKnownHttpClient(callee.expression, checker) || isKnownHttpClientModule(callee.expression, checker))
    ) {
      return e;
    }
    return findChainedRecognizedCall(callee.expression, checker);
  }
  return undefined;
}

/** Whether `paramSymbol` is referenced anywhere within `expr` — the confirmation that a wrapper's
 * inner HTTP-client call actually forwards the wrapper's OWN parameter into its URL argument,
 * rather than happening to call a recognized client with an unrelated, unconnected URL. */
function referencesSymbol(expr: ts.Node, paramSymbol: ts.Symbol, checker: ts.TypeChecker): boolean {
  let found = false;
  const visit = (node: ts.Node): void => {
    if (found) {
      return;
    }
    if (ts.isIdentifier(node) && checker.getSymbolAtLocation(node) === paramSymbol) {
      found = true;
      return;
    }
    ts.forEachChild(node, visit);
  };
  visit(expr);
  return found;
}

/**
 * Traces a verb-named call's receiver ONE level through a locally-declared object/function
 * wrapper whose own implementation directly forwards its first parameter into a recognized
 * HTTP-client call — the shape confirmed against the real
 * `gothinkster/react-redux-realworld-example-app` field trial (2026-08-28): every one of its 22
 * frontend call sites goes through
 * `requests.get: url => superagent.get(\`${API_ROOT}${url}\`).use(tokenPlugin).then(responseBody)`,
 * which `isKnownHttpClient` alone cannot see through (`requests` is a plain object literal, not
 * an `axios.create()` instance) — so every one of those 22 call sites was previously reported
 * `unrecognized-callee` and none linked to a backend endpoint.
 *
 * Returns `undefined` — never a guess — unless ALL of the following hold:
 *   1. the receiver (`callee.expression`) is const-rooted;
 *   2. it resolves to a wrapper function of one of the four recognized shapes;
 *   3. the wrapper's first parameter is a plain identifier (no destructuring, no rest/spread);
 *   4. the wrapper's body is a single, clean forwarding expression (see `wrapperReturnExpression`);
 *   5. that expression's fluent chain contains a recognized low-level HTTP-client call
 *      (`fetch`, `axios.*`/an `axios.create()` instance, or `superagent.<verb>`);
 *   6. that inner call's URL argument actually references the wrapper's own first parameter.
 *
 * Only one level of indirection is supported (a wrapper calling a wrapper calling a wrapper is a
 * known, out-of-scope limitation) — `wrapperReturnExpression` and `findChainedRecognizedCall`
 * only look inside the ONE function this call's receiver resolves to.
 */
export function resolveHttpWrapper(callee: ts.PropertyAccessExpression, checker: ts.TypeChecker): WrapperForward | undefined {
  if (!isConstRootedOrNotApplicable(callee.expression, checker)) {
    return undefined;
  }
  const memberSymbol = checker.getSymbolAtLocation(callee.name);
  if (!memberSymbol) {
    return undefined;
  }
  const resolved = resolveAlias(memberSymbol, checker);
  const fn = wrapperFunctionFromDeclaration(resolved.valueDeclaration ?? resolved.declarations?.[0], checker);
  if (!fn) {
    return undefined;
  }

  const param = fn.parameters[0];
  if (!param || param.dotDotDotToken || !ts.isIdentifier(param.name)) {
    return undefined; // no parameter, or a rest/destructured one — cannot cleanly substitute
  }
  const paramSymbol = checker.getSymbolAtLocation(param.name);
  if (!paramSymbol) {
    return undefined;
  }

  const bodyExpr = wrapperReturnExpression(fn);
  if (!bodyExpr) {
    return undefined;
  }

  const forwardedCall = findChainedRecognizedCall(bodyExpr, checker);
  if (!forwardedCall) {
    return undefined;
  }

  const urlArg = forwardedCall.arguments[0];
  if (!urlArg || !referencesSymbol(urlArg, paramSymbol, checker)) {
    return undefined;
  }

  return { paramSymbol, urlArg, forwardedCall };
}
