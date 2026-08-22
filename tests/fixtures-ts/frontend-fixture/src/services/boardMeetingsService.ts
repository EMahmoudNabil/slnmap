import apiClient from './apiClient';

// Case C (spike §2): const + barrel + service-object indirection. COMMITTEES is a
// compile-time-constant hole; the barrel (index.ts) renames the default export to a
// named one on the way out.
const COMMITTEES = '/Committees';

export const boardMeetingsService = {
  getCommittees: () => apiClient.get(COMMITTEES),
};
