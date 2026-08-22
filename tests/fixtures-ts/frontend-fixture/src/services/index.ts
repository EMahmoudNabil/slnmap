// Barrel re-export: apiClient crosses a default->named rename on its way out, one of
// the "5 distinct import spellings resolving to one client" shapes the spike measured
// (frontend-feasibility-spike.md §1). Symbol resolution must follow this without regexing
// every possible import spelling.
export { default as apiClient } from './apiClient';
export { boardMeetingsService } from './boardMeetingsService';
