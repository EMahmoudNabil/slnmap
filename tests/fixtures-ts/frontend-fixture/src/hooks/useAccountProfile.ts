import apiClient from '@/services/apiClient';

// Row 4: param-vs-literal sibling case (spike §2, "ambiguous cases"). This call site is a
// plain literal; the backend registers BOTH /api/AccountProfiles/current (literal) and
// /api/AccountProfiles/{id} (param) as distinct endpoints. The frontend cannot and should not
// guess which one this is closer to -- it stores the literal it wrote, and Phase 3's
// linker resolves the two-candidate match via route precedence (literal beats parameter).
export function useAccountProfile() {
  return apiClient.get('/AccountProfiles/current');
}
