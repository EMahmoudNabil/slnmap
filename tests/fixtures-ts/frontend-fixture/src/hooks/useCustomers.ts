import apiClient from '@/services/apiClient';

// Row 5: the spike's real dangling-bug case (frontend-feasibility-spike.md §2, "1 remaining
// no-match is a real bug the linker caught"). POSTs to a path with no matching backend
// registration at all -- a live 404 in production. Deliberately lowercase, exactly as
// the production frontend wrote it, to also exercise the case-fold requirement documented in
// RouteTemplate.Normalize.
export function useCustomers() {
  function createUser(payload: unknown) {
    return apiClient.post('/customers', payload);
  }

  return { createUser };
}
