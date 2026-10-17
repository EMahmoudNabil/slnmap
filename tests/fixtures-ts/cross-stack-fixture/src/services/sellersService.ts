import apiClient from './apiClient';

// cross-stack-linker-investigation.md §Q5: six call sites, one per named linker outcome,
// each matched 1:1 against a FixtureWeb (tests/fixtures/FixtureSolution/FixtureWeb/
// SellerEndpoints.cs) route of the same shape. Every path here is written exactly as a real
// frontend author would write it (no `/api` prefix — the linker's base-path config supplies
// that, per ts-extractor-investigation.md §Q4).
export const sellersService = {
  // Unique link: literal call site, literal endpoint (GET /api/sellers).
  list: () => apiClient.get('/sellers'),

  // Unique link: literal call site, single parameterized endpoint (POST /api/sellers/{id}).
  // A hardcoded literal segment, not a template interpolation, is deliberate here: it proves
  // the hole-matches-concrete rule works from the CALL SITE's literal side too, and it excludes
  // the pre-existing sibling POST /api/sellers/archive (a literal "archive" segment can never
  // match a call site literally requesting "42") -- a real bug this fixture caught during
  // implementation: the original template-interpolated version (`/sellers/${id}`) put a HOLE at
  // this position, which correctly also matched .../archive per the same rule, making this call
  // site genuinely ambiguous rather than the clean unique-link shape this test documents.
  update: (data: unknown) => apiClient.post('/sellers/42', data),

  // Ambiguous (row 4): a literal call site skeleton-matches BOTH GET /api/sellers/current
  // (literal) and GET /api/sellers/{sellerId} (param) — the real, currently-present-day
  // AccountProfiles/current-vs-{id} shape found on a production backend.
  // Because the call site's own segment here is a concrete literal ("current"), the linker's
  // route-precedence rule (literal beats parameter) resolves this to a single edge.
  current: () => apiClient.get('/sellers/current'),

  // Fan-out: the call site's own segment is a hole (`channel` is genuinely runtime-chosen,
  // not a compile-time-constant set), so it deterministically reaches all three sibling
  // endpoints (POST /api/sellers/notify/{email,sms,push}) and only those three — a truthful
  // set edge, not a guess.
  notify: (channel: string, payload: unknown) => apiClient.post(`/sellers/notify/${channel}`, payload),

  // Deliberate orphan: no FixtureWeb route of this shape exists anywhere (mirrors a real
  // verb-mismatch bug found in a production frontend).
  bulkImport: (payload: unknown) => apiClient.post('/sellers/reports/export/csv', payload),

  // Deliberate verb mismatch: skeleton-matches GET /api/sellers exactly, but the call site is
  // a DELETE — no endpoint registers DELETE at this template, so this must produce no edge
  // (disclosed as a verb mismatch, never guessed as the GET endpoint).
  removeAll: () => apiClient.delete('/sellers'),
};
