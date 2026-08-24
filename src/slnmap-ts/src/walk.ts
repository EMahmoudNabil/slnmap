import ts from 'typescript';
import type { LoadedProgram } from './program.js';
import { isAmbientGlobalFetch, isKnownHttpClient } from './detection.js';
import { foldUrlArgument, tracesThroughDynamicImportOrRequire, unwrap } from './resolve.js';
import type { CallSiteRecord, UnresolvedCallSiteRecord, UnresolvedCategory } from './types.js';

/**
 * The verb-shaped method names the detection set recognizes (investigation §Q3.1), mapped to
 * the verb they report. `del` is superagent's own official method name for DELETE (not an
 * app-specific quirk — verified against a real codebase during the pre-publish field trial,
 * reports/analyze-ts-field-trial.md: a superagent-wrapped API layer's four `.del(...)` call
 * sites were silently invisible to the tool — not even counted as unresolved — before this was
 * recognized as an alias).
 */
const HTTP_VERB_METHODS = new Map([
  ['get', 'GET'],
  ['post', 'POST'],
  ['put', 'PUT'],
  ['delete', 'DELETE'],
  ['del', 'DELETE'],
  ['patch', 'PATCH'],
]);

interface Position {
  line: number;
  column: number;
  spanStart: number;
  spanEnd: number;
}

/**
 * The position to anchor a call expression's report to. Ordinarily this is the call's own
 * start — but a FLUENT CHAIN (`x.a().b().c()`) nests each link's CallExpression inside the next,
 * and `getStart()` walks back through the receiver to the leftmost token of the WHOLE chain
 * (e.g. `x`), which is identical for every link. Two verb-named links in the same chain
 * (`app.use(...).get(A, ...).get(B, ...)`, the real shape from the foreign-patterns trial's
 * Turborepo target) then report byte-identical line/column/spanStart — and since downstream
 * node identity keys on exactly that position, the second link silently collapsed into the
 * first (v0.12.2, foreign-patterns-trial finding #3: 4 call sites reported, 3 persisted).
 *
 * When the receiver is itself a call expression, anchor to the callee's own property-name token
 * instead — every link in a chain has a distinct property-access name token, so this is always
 * unique per link. This leaves the ordinary (non-chained) case — the receiver is a plain
 * identifier or property access, not another call — completely unchanged, matching every
 * existing golden fixture.
 */
function anchorStart(node: ts.CallExpression, sourceFile: ts.SourceFile): number {
  const callee = node.expression;
  if (ts.isPropertyAccessExpression(callee) && ts.isCallExpression(callee.expression)) {
    return callee.name.getStart(sourceFile);
  }
  return node.getStart(sourceFile);
}

/** Line/column (1-based, human-facing) AND character-offset span (Roslyn `TextSpan` units,
 * schemaVersion 2) for the call expression — matching how the C# Endpoint node's span is the
 * whole `Map*` invocation, not just the callee identifier, except where a fluent chain requires
 * anchoring to the callee name instead (see `anchorStart`). `spanEnd` is always this call's own
 * end, which already differs per link in a chain regardless of the anchor fix. */
function positionOf(node: ts.CallExpression, sourceFile: ts.SourceFile): Position {
  const spanStart = anchorStart(node, sourceFile);
  const { line, character } = sourceFile.getLineAndCharacterOfPosition(spanStart);
  return { line: line + 1, column: character + 1, spanStart, spanEnd: node.getEnd() };
}

function truncate(text: string, max = 60): string {
  const collapsed = text.replace(/\s+/g, ' ').trim();
  return collapsed.length <= max ? collapsed : `${collapsed.slice(0, max)}…`;
}

/** Whether `expr` — the first argument of a verb-shaped call on an unverified receiver — looks
 * like a URL at all (resolves, fully or with `{*}` holes, to a string starting with '/'). Reuses
 * the same folder the real HTTP-client path uses, so "looks URL-shaped" and "is a route
 * template" are exactly the same notion, not two divergent heuristics.
 *
 * A top-level string-concatenation (`base + '/users'`) gets the same free pass a template
 * literal already does (v0.12.2, foreign-patterns-trial finding #3): using `+` to build the
 * first argument to a verb-named method is itself strong evidence of URL-building, regardless of
 * whether every operand happens to fold. Before this, a concatenation that didn't fully resolve
 * fell through to "not URL-shaped" and the whole call site was silently dropped — never even
 * counted unresolved — the same "proven not relevant" treatment reserved for things like
 * `response.headers.get(...)`, which this plainly isn't. The receiver is still unrecognized
 * either way, so the eventual disclosed category remains `unrecognized-callee` (below) — this
 * only fixes whether the call site is disclosed at all. */
function isUrlShaped(expr: ts.Expression, checker: ts.TypeChecker): boolean {
  const e = unwrap(expr);
  if (ts.isBinaryExpression(e) && e.operatorToken.kind === ts.SyntaxKind.PlusToken) {
    return true;
  }
  const result = foldUrlArgument(expr, checker);
  return result.ok && result.value.startsWith('/');
}

function unresolved(
  verb: string,
  category: UnresolvedCategory,
  reason: string,
  file: string,
  position: Position,
): UnresolvedCallSiteRecord {
  return {
    kind: 'UnresolvedCallSite',
    verb,
    category,
    reason,
    file,
    line: position.line,
    column: position.column,
    spanStart: position.spanStart,
    spanEnd: position.spanEnd,
  };
}

function classifyUrlArgument(
  node: ts.CallExpression,
  verb: string,
  file: string,
  position: Position,
  checker: ts.TypeChecker,
): CallSiteRecord {
  const urlArg = node.arguments[0];
  if (!urlArg) {
    return unresolved(verb, 'runtime-computed-segment', 'call has no URL argument', file, position);
  }

  const result = foldUrlArgument(urlArg, checker);
  if (!result.ok) {
    return unresolved(verb, result.failure.category, result.failure.detail, file, position);
  }

  return {
    kind: 'FrontendCallSite',
    verb,
    template: result.value,
    resolutionTier: result.resolutionTier,
    file,
    line: position.line,
    column: position.column,
    spanStart: position.spanStart,
    spanEnd: position.spanEnd,
  };
}

/**
 * Classifies one call expression as a call site (resolved or unresolved) or `null` — not a
 * candidate at all. Two detection shapes (§Q3.1):
 *   1. A verb-named property-access call (`apiClient.get(...)`) — receiver identity is checked
 *      FIRST (known client / unresolved indirection / unrecognized), independently of whether
 *      the URL argument itself would resolve.
 *   2. The bare global `fetch(...)` — a shadowed local of the same name is proven NOT an HTTP
 *      call (positive evidence) and is silently excluded, never reported unresolved.
 */
function classifyCallExpression(
  node: ts.CallExpression,
  sourceFile: ts.SourceFile,
  checker: ts.TypeChecker,
  file: string,
): CallSiteRecord | null {
  const callee = node.expression;
  const position = positionOf(node, sourceFile);

  if (ts.isPropertyAccessExpression(callee) && HTTP_VERB_METHODS.has(callee.name.text)) {
    const verb = HTTP_VERB_METHODS.get(callee.name.text)!;
    const receiver = callee.expression;

    if (tracesThroughDynamicImportOrRequire(receiver, checker)) {
      return unresolved(
        verb,
        'dynamic-import-or-indirection',
        `receiver flows through a dynamic import/require: '${truncate(receiver.getText())}'`,
        file,
        position,
      );
    }

    if (!isKnownHttpClient(receiver, checker)) {
      // The verb-named-method shape alone is a WEAK signal: `.get`/`.post`/etc. are also real
      // methods on `Headers`, `Map`, `URLSearchParams`, and plenty of other everyday objects —
      // at OSSUS_Frontend scale, an unfiltered shape check produced 284 false candidates (mostly
      // `response.headers.get('Content-Type')`-style calls), each correctly failing identity
      // resolution but drowning the genuine unresolved signal in noise. The argument itself is
      // the disambiguator: only when it actually looks URL-shaped (resolves — fully or with
      // holes — to a string starting with '/') is this worth flagging as `unrecognized-callee`.
      // Otherwise it is proven NOT an HTTP call site (no URL-shaped argument at all) and is
      // silently excluded — the same "proven not relevant" treatment as a shadowed `fetch`.
      const urlArg = node.arguments[0];
      const looksUrlShaped = urlArg !== undefined && isUrlShaped(urlArg, checker);
      if (!looksUrlShaped) {
        return null;
      }
      return unresolved(
        verb,
        'unrecognized-callee',
        `receiver '${truncate(receiver.getText())}' does not resolve to a known HTTP client (an axios.create() instance)`,
        file,
        position,
      );
    }

    return classifyUrlArgument(node, verb, file, position, checker);
  }

  if (ts.isIdentifier(callee) && callee.text === 'fetch') {
    if (!isAmbientGlobalFetch(callee, checker)) {
      return null; // shadowed local — proven not an HTTP call, not merely unverified
    }
    return classifyUrlArgument(node, resolveFetchVerb(node), file, position, checker);
  }

  return null;
}

/**
 * The Fetch API defaults to GET absent an explicit `method`, but a real call site frequently
 * overrides it in the options object (`fetch(url, { method: 'POST', ... })`) — a spot-check
 * against OSSUS_Frontend caught this: a bare `fetch()` call was silently reported as GET when
 * its options literally said POST. Only a literal `method` value is trusted; a non-literal one
 * means the real verb is genuinely unknown and must be declared as such, never guessed.
 */
function resolveFetchVerb(node: ts.CallExpression): string {
  const options = node.arguments[1];
  if (!options || !ts.isObjectLiteralExpression(options)) {
    return 'GET';
  }
  for (const prop of options.properties) {
    if (ts.isPropertyAssignment(prop) && ts.isIdentifier(prop.name) && prop.name.text === 'method') {
      return ts.isStringLiteralLike(prop.initializer) ? prop.initializer.text.toUpperCase() : 'UNKNOWN';
    }
  }
  return 'GET';
}

function compareRecords(a: CallSiteRecord, b: CallSiteRecord): number {
  if (a.file !== b.file) {
    return a.file < b.file ? -1 : 1;
  }
  if (a.line !== b.line) {
    return a.line - b.line;
  }
  return a.column - b.column;
}

/** Walks every in-project source file's call expressions and returns call-site records sorted
 * by (file, line, column) — deterministic, byte-identical output across runs on unchanged input
 * (the investigation's determinism contract, §Q1/§Q2.2). */
export function extractCallSites(loaded: LoadedProgram): CallSiteRecord[] {
  const { program, checker, inProject, relativePath } = loaded;
  const records: CallSiteRecord[] = [];

  for (const sourceFile of program.getSourceFiles()) {
    if (!inProject(sourceFile.fileName)) {
      continue;
    }
    const file = relativePath(sourceFile.fileName).replace(/\\/g, '/');

    const visit = (node: ts.Node): void => {
      if (ts.isCallExpression(node)) {
        const record = classifyCallExpression(node, sourceFile, checker, file);
        if (record) {
          records.push(record);
        }
      }
      ts.forEachChild(node, visit);
    };
    ts.forEachChild(sourceFile, visit);
  }

  records.sort(compareRecords);
  return records;
}
