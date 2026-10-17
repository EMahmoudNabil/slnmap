import apiClient from '@/services/apiClient';
import { API_ROUTES } from '@/routes';

// Case B (spike §2): template with a semantic dispatch. moduleMap is a literal object;
// the two interpolation holes are genuinely runtime-chosen, so the call site is a template
// with two anonymous holes -- never guessed down to one branch.
const moduleMap: Record<string, string> = {
  pickup: 'pickups',
  refund: 'refunds',
  risk: 'risks',
};

export function useUserOrderQueue() {
  function fetchSummary() {
    // Row 1: literal-through-object -- API_ROUTES.pickerTasksSummary is a literal-typed
    // property access, const-resolved by the checker.
    return apiClient.get(API_ROUTES.pickerTasksSummary);
  }

  function sendReminder(taskModule: string, taskId: string) {
    const resolvedModule = moduleMap[taskModule] || taskModule;
    // Row 2: template with two const-unresolvable (runtime) holes -- both taskId and
    // resolvedModule are true runtime values. Stored with anonymous {*} holes.
    return apiClient.post(`/OrderQueue/${resolvedModule}/${taskId}/reminder`, {});
  }

  return { fetchSummary, sendReminder };
}
