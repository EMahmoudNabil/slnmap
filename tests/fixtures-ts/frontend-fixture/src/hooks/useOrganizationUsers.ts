import apiClient from '@/services/apiClient';

// Row 5: the spike's real dangling-bug case (frontend-feasibility-spike.md §2, "1 remaining
// no-match is a real bug the linker caught"). POSTs to a path with no matching backend
// registration at all -- a live 404 in production. Deliberately lowercase, exactly as
// OSSUS_Frontend wrote it, to also exercise the case-fold requirement documented in
// RouteTemplate.Normalize.
export function useOrganizationUsers() {
  function createUser(payload: unknown) {
    return apiClient.post('/organizationusers', payload);
  }

  return { createUser };
}
