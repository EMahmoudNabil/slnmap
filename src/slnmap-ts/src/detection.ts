import ts from 'typescript';

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

function importedModuleSpecifierText(symbol: ts.Symbol): string | undefined {
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
