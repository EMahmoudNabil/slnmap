import apiClient from './apiClient';

// cross-stack-linker-investigation.md §Q5: six call sites, one per named linker outcome,
// each matched 1:1 against a FixtureWeb (tests/fixtures/FixtureSolution/FixtureWeb/
// VendorEndpoints.cs) route of the same shape. Every path here is written exactly as a real
// frontend author would write it (no `/api` prefix — the linker's base-path config supplies
// that, per ts-extractor-investigation.md §Q4).
export const vendorsService = {
  // Unique link: literal call site, literal endpoint (GET /api/vendors).
  list: () => apiClient.get('/vendors'),

  // Unique link: literal call site, single parameterized endpoint (POST /api/vendors/{id}).
  // A hardcoded literal segment, not a template interpolation, is deliberate here: it proves
  // the hole-matches-concrete rule works from the CALL SITE's literal side too, and it excludes
  // the pre-existing sibling POST /api/vendors/archive (a literal "archive" segment can never
  // match a call site literally requesting "42") -- a real bug this fixture caught during
  // implementation: the original template-interpolated version (`/vendors/${id}`) put a HOLE at
  // this position, which correctly also matched .../archive per the same rule, making this call
  // site genuinely ambiguous rather than the clean unique-link shape this test documents.
  update: (data: unknown) => apiClient.post('/vendors/42', data),

  // Ambiguous (row 4): a literal call site skeleton-matches BOTH GET /api/vendors/current
  // (literal) and GET /api/vendors/{vendorId} (param) — the real, currently-present-day
  // UserProfiles/current-vs-{id} shape this investigation found on live OSSUS_Backend.
  // Because the call site's own segment here is a concrete literal ("current"), the linker's
  // route-precedence rule (literal beats parameter) resolves this to a single edge.
  current: () => apiClient.get('/vendors/current'),

  // Fan-out: the call site's own segment is a hole (`channel` is genuinely runtime-chosen,
  // not a compile-time-constant set), so it deterministically reaches all three sibling
  // endpoints (POST /api/vendors/notify/{email,sms,push}) and only those three — a truthful
  // set edge, not a guess.
  notify: (channel: string, payload: unknown) => apiClient.post(`/vendors/notify/${channel}`, payload),

  // Deliberate orphan: no FixtureWeb route of this shape exists anywhere (mirrors the real,
  // still-live OSSUS_Frontend organizationUsers.ts:98 bug this investigation re-confirmed).
  bulkImport: (payload: unknown) => apiClient.post('/vendors/reports/export/csv', payload),

  // Deliberate verb mismatch: skeleton-matches GET /api/vendors exactly, but the call site is
  // a DELETE — no endpoint registers DELETE at this template, so this must produce no edge
  // (disclosed as a verb mismatch, never guessed as the GET endpoint).
  removeAll: () => apiClient.delete('/vendors'),
};
