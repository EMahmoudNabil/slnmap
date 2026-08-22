import ts from 'typescript';
import type { ResolutionTier, UnresolvedCategory } from './types.js';

/**
 * Constant folding (investigation §Q3.2) and the declared-unresolvable classifier (§Q3.3).
 * Every failure returns a specific category — nothing falls through uncategorized. Uses checker
 * symbol/declaration facts throughout; never AST-text guessing.
 */

/** Mirrors EndpointFacts.MaxResolutionDepth = 8 (src/Slnmap.Analysis/EndpointFacts.cs) — a
 * self-imposed bound against pathological/adversarial nesting, not a guess. */
const MAX_RESOLUTION_DEPTH = 8;

export interface FoldFailure {
  category: UnresolvedCategory;
  detail: string;
}

export type FoldResult = { ok: true; value: string } | { ok: false; failure: FoldFailure };

export type UrlFoldResult =
  | { ok: true; value: string; resolutionTier: ResolutionTier }
  | { ok: false; failure: FoldFailure };

function truncate(text: string, max = 60): string {
  const collapsed = text.replace(/\s+/g, ' ').trim();
  return collapsed.length <= max ? collapsed : `${collapsed.slice(0, max)}…`;
}

function unwrap(expr: ts.Expression): ts.Expression {
  for (;;) {
    if (ts.isParenthesizedExpression(expr)) {
      expr = expr.expression;
      continue;
    }
    if (ts.isAsExpression(expr) || ts.isSatisfiesExpression(expr)) {
      expr = expr.expression;
      continue;
    }
    if (ts.isNonNullExpression(expr)) {
      expr = expr.expression;
      continue;
    }
    return expr;
  }
}

/** True for a dynamic `import(...)` call or a `require(...)` call, optionally behind `await`. */
function isDynamicImportOrRequire(expr: ts.Expression): boolean {
  const e = unwrap(expr);
  if (ts.isAwaitExpression(e)) {
    return isDynamicImportOrRequire(e.expression);
  }
  if (ts.isCallExpression(e)) {
    if (e.expression.kind === ts.SyntaxKind.ImportKeyword) {
      return true; // dynamic import()
    }
    if (ts.isIdentifier(e.expression) && e.expression.text === 'require') {
      return true;
    }
  }
  return false;
}

/**
 * Whether `expr`'s resolution chain (property-access base, or one hop through a const
 * identifier's initializer) touches a dynamic `import()`/`require()` — used both for the URL
 * argument and for an HTTP-verb-call receiver (e.g. `(await import('./client')).default.get(...)`).
 */
export function tracesThroughDynamicImportOrRequire(
  expr: ts.Expression,
  checker: ts.TypeChecker,
  depth = MAX_RESOLUTION_DEPTH,
): boolean {
  if (depth <= 0) {
    return false;
  }
  const e = unwrap(expr);
  if (isDynamicImportOrRequire(e)) {
    return true;
  }
  if (ts.isPropertyAccessExpression(e)) {
    return tracesThroughDynamicImportOrRequire(e.expression, checker, depth - 1);
  }
  if (ts.isIdentifier(e)) {
    const symbol = checker.getSymbolAtLocation(e);
    const decl = symbol?.valueDeclaration ?? symbol?.declarations?.[0];
    if (!decl) {
      return false;
    }
    if (ts.isVariableDeclaration(decl) && decl.initializer) {
      return isDynamicImportOrRequire(decl.initializer);
    }
    if (ts.isBindingElement(decl)) {
      // `const { default: apiClient } = await import('./apiClient')` — a very common real-world
      // shape (verified against OSSUS_Frontend's own AuthContext.tsx). The binding element
      // itself has no initializer; the relevant expression is the ENCLOSING variable
      // declaration's initializer, since that is what the whole destructuring pulls from.
      let ancestor: ts.Node = decl.parent;
      while (!ts.isVariableDeclaration(ancestor) && ancestor.parent) {
        ancestor = ancestor.parent;
      }
      if (ts.isVariableDeclaration(ancestor) && ancestor.initializer) {
        return isDynamicImportOrRequire(ancestor.initializer);
      }
    }
  }
  return false;
}

/**
 * True for `process.env.X` / `process.env['X']`. Deliberately a NAME check on `process`, not a
 * symbol-resolved one (contrast `isAmbientGlobalFetch` in detection.ts, which resolves through
 * the checker): verifying `process` resolves to `@types/node`'s ambient declaration turned out to
 * depend on `ts.sys.getCurrentDirectory()` (Node's own type-roots acquisition walks up from the
 * process's CWD, not just the analyzed project's directory) — a real, empirically-found
 * non-determinism risk for a tool invoked via `npx` from an arbitrary working directory (it
 * could pick up `slnmap-ts`'s OWN `node_modules/@types/node` depending on where the caller's
 * shell happens to be). `process` is, in practice, essentially never locally shadowed the way
 * `fetch` commonly is (as an injected/mockable HTTP client parameter) — a name check is an
 * acceptable, deterministic trade-off for this one narrow, well-known global.
 */
function isProcessEnvAccess(expr: ts.Expression): boolean {
  if (!ts.isPropertyAccessExpression(expr) && !ts.isElementAccessExpression(expr)) {
    return false;
  }
  const base = expr.expression;
  if (!ts.isPropertyAccessExpression(base) || base.name.text !== 'env') {
    return false;
  }
  const root = base.expression;
  return ts.isIdentifier(root) && root.text === 'process';
}

/** Whether the (simple-identifier) root of a property-access chain is itself `const`-declared —
 * an object's literal property is only trustworthy if the binding holding the object can't be
 * reassigned wholesale. Non-identifier roots (a nested call, etc.) are not blocked by this check;
 * they fail resolution on their own terms further down the recursion. */
function isConstRootedOrNotApplicable(expr: ts.Expression, checker: ts.TypeChecker): boolean {
  const e = unwrap(expr);
  if (!ts.isIdentifier(e)) {
    return true;
  }
  const symbol = checker.getSymbolAtLocation(e);
  const decl = symbol?.valueDeclaration ?? symbol?.declarations?.[0];
  if (!decl || !ts.isVariableDeclaration(decl)) {
    return true;
  }
  const declList = decl.parent;
  return ts.isVariableDeclarationList(declList) && (declList.flags & ts.NodeFlags.Const) !== 0;
}

/**
 * Resolves an expression to a compile-time string constant, or a specific declared-unresolvable
 * category. Handles: string/numeric literals, `+` concatenation, const identifiers (through
 * their initializer), and object-literal property access (API_ROUTES-style, §Q3.2 row 3), all
 * recursively up to MAX_RESOLUTION_DEPTH hops.
 */
export function resolveConstantExpression(
  expr: ts.Expression,
  checker: ts.TypeChecker,
  depth = MAX_RESOLUTION_DEPTH,
): FoldResult {
  const e = unwrap(expr);

  if (depth <= 0) {
    return {
      ok: false,
      failure: {
        category: 'resolution-depth-exceeded',
        detail: `resolution exceeded ${MAX_RESOLUTION_DEPTH} hops at '${truncate(e.getText())}'`,
      },
    };
  }

  if (ts.isStringLiteralLike(e)) {
    return { ok: true, value: e.text };
  }
  if (ts.isNumericLiteral(e)) {
    return { ok: true, value: e.text };
  }

  if (isDynamicImportOrRequire(e)) {
    return {
      ok: false,
      failure: {
        category: 'dynamic-import-or-indirection',
        detail: `value flows through a dynamic import/require: '${truncate(e.getText())}'`,
      },
    };
  }

  if (isProcessEnvAccess(e)) {
    return {
      ok: false,
      failure: {
        category: 'dynamic-base-url',
        detail: `value is read from '${truncate(e.getText())}', an environment/runtime-configured value`,
      },
    };
  }

  if (ts.isBinaryExpression(e) && e.operatorToken.kind === ts.SyntaxKind.PlusToken) {
    const left = resolveConstantExpression(e.left, checker, depth - 1);
    if (!left.ok) {
      return left;
    }
    const right = resolveConstantExpression(e.right, checker, depth - 1);
    if (!right.ok) {
      return right;
    }
    return { ok: true, value: left.value + right.value };
  }

  if (ts.isIdentifier(e) || ts.isPropertyAccessExpression(e)) {
    const symbol = checker.getSymbolAtLocation(e);
    if (!symbol) {
      return {
        ok: false,
        failure: {
          category: 'runtime-computed-segment',
          detail: `'${truncate(e.getText())}' does not resolve to a symbol`,
        },
      };
    }

    const decl = symbol.valueDeclaration ?? symbol.declarations?.[0];

    if (decl && ts.isVariableDeclaration(decl) && decl.initializer) {
      const declList = decl.parent;
      const isConst = ts.isVariableDeclarationList(declList) && (declList.flags & ts.NodeFlags.Const) !== 0;
      if (!isConst) {
        return {
          ok: false,
          failure: {
            category: 'non-constant-identifier',
            detail: `'${symbol.getName()}' is not declared 'const' — a mutable binding is never treated as a compile-time constant`,
          },
        };
      }
      if (isDynamicImportOrRequire(unwrap(decl.initializer))) {
        return {
          ok: false,
          failure: {
            category: 'dynamic-import-or-indirection',
            detail: `'${symbol.getName()}' is initialized from a dynamic import/require`,
          },
        };
      }
      return resolveConstantExpression(decl.initializer, checker, depth - 1);
    }

    if (decl && ts.isPropertyAssignment(decl) && decl.initializer && ts.isPropertyAccessExpression(e)) {
      if (!isConstRootedOrNotApplicable(e.expression, checker)) {
        return {
          ok: false,
          failure: {
            category: 'non-constant-identifier',
            detail: `the object holding '${symbol.getName()}' is not const-rooted`,
          },
        };
      }
      return resolveConstantExpression(decl.initializer, checker, depth - 1);
    }

    return {
      ok: false,
      failure: {
        category: 'runtime-computed-segment',
        detail: `'${truncate(e.getText())}' does not resolve to a literal-holding declaration`,
      },
    };
  }

  return {
    ok: false,
    failure: {
      category: 'runtime-computed-segment',
      detail: `'${truncate(e.getText())}' is not a compile-time-resolvable expression`,
    },
  };
}

/**
 * Folds the URL argument of a recognized HTTP call to a route template. A template literal
 * ALWAYS succeeds (investigation §Q3.2 row 6): literal segments are kept verbatim and every
 * unresolvable hole becomes an anonymous `{*}` token — only a bare (non-template) argument that
 * fails to resolve at all pushes the call site to the declared-unresolvable bucket.
 */
export function foldUrlArgument(expr: ts.Expression, checker: ts.TypeChecker): UrlFoldResult {
  const e = unwrap(expr);

  if (ts.isNoSubstitutionTemplateLiteral(e)) {
    return { ok: true, value: e.text, resolutionTier: 'literal' };
  }

  if (ts.isTemplateExpression(e)) {
    let out = e.head.text;
    let hasHole = false;
    for (const span of e.templateSpans) {
      const hole = resolveConstantExpression(span.expression, checker);
      if (hole.ok) {
        out += hole.value;
      } else if (hole.failure.category === 'runtime-computed-segment') {
        // The generic "some runtime value flows in" bucket is exactly an ordinary template
        // hole (§Q3.2 row 6 / Case B's `${module}`/`${taskId}`) — becomes an anonymous {*}
        // token and the template still resolves as a whole.
        out += '{*}';
        hasHole = true;
      } else {
        // A MORE SPECIFIC failure (dynamic-base-url, non-constant-identifier,
        // dynamic-import-or-indirection, resolution-depth-exceeded) is a qualitatively
        // different, worth-surfacing situation — it propagates and marks the whole call site
        // unresolved under that specific category rather than being silently swallowed into
        // a {*} hole. First such specific failure wins, in left-to-right source order.
        return hole;
      }
      out += span.literal.text;
    }
    return { ok: true, value: out, resolutionTier: hasHole ? 'template-param-holes' : 'template-folded' };
  }

  const result = resolveConstantExpression(e, checker);
  if (!result.ok) {
    return result;
  }
  return { ok: true, value: result.value, resolutionTier: ts.isStringLiteralLike(e) ? 'literal' : 'const-resolved' };
}
