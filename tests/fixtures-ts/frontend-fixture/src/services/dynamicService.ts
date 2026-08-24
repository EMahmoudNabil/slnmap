import apiClient from './apiClient';

// Every row below is DECLARED unresolvable -- never guessed. Each maps to one category
// code in the UnresolvedCallSite bucket (reports/ts-extractor-investigation.md §Q3).

// Category: dynamic-base-url -- the base comes from an environment/runtime config value,
// not a compile-time literal. process.env.X types as `string`, not a string-literal type.
const API_BASE = process.env.NEXT_PUBLIC_API_BASE as string;
export function fetchFromConfiguredBase(path: string) {
  return fetch(`${API_BASE}/${path}`);
}

// Category: string-concatenation (v0.12.2; was runtime-computed-segment before the
// foreign-patterns-trial fix) -- the URL is built via `+` concatenation and the right-hand
// operand is a runtime computation (function call, Date, etc.), never a literal. Concatenation
// is now its own disclosed category rather than being folded into the generic
// runtime-computed-segment bucket, since it's the more specific, more actionable fact.
export function fetchYearlyReport() {
  const basePath = '/Reports/' + new Date().getFullYear();
  return apiClient.get(basePath);
}

// Category: non-constant-identifier -- `let`, reassigned before use. The checker will not
// treat a mutable binding's value as a compile-time constant even though every assignment
// happens to be a literal.
let currentEndpoint = '/Vendors';
export function fetchVendors() {
  currentEndpoint = '/Vendors/v2';
  return apiClient.get(currentEndpoint);
}

// Category: unrecognized-callee -- a property-access call whose method name matches an HTTP
// verb (the shape check the extractor's detection set looks for), but whose receiver does not
// resolve to any known client -- no axios.create() instance anywhere in the program. The shape
// looks like a real HTTP call and is worth flagging (not silently dropped), but the extractor
// correctly declines to guess that this specific, unrecognized object is one.
const customClient = { get: (_url: string) => Promise.resolve(null) };
export function fetchViaCustom() {
  return customClient.get('/Custom/endpoint');
}

// Category: dynamic-import-or-indirection -- the client itself arrives through a dynamic
// `import()`; static resolution stops at the indirection.
export async function fetchViaDynamicImport() {
  const client = await import('./apiClient');
  return client.default.get('/Deferred/endpoint');
}

// Category: resolution-depth-exceeded -- every hop of this chain holds a literal, but nine
// hops separate the call site from that literal (chain10 -> chain9 -> ... -> chain1), deeper
// than the extractor's self-imposed recursion bound (mirrors EndpointFacts.MaxResolutionDepth
// = 8). Declared, not guessed, even though a human could resolve it by inspection.
const chain1 = '/Deep/endpoint';
const chain2 = chain1;
const chain3 = chain2;
const chain4 = chain3;
const chain5 = chain4;
const chain6 = chain5;
const chain7 = chain6;
const chain8 = chain7;
const chain9 = chain8;
const chain10 = chain9;
export function fetchDeep() {
  return apiClient.get(chain10);
}
