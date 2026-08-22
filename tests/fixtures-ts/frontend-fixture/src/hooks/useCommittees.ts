import { boardMeetingsService } from '@/services';

// Row 3: three-hop indirection (hook -> barrel -> service object -> apiClient + const),
// exactly the spike's Case C chain. Resolved to /Committees.
export function useCommittees() {
  return boardMeetingsService.getCommittees();
}
