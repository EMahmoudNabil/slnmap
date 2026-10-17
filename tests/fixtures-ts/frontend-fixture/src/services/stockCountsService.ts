import apiClient from './apiClient';

// Case C (spike §2): const + barrel + service-object indirection. WAREHOUSES is a
// compile-time-constant hole; the barrel (index.ts) renames the default export to a
// named one on the way out.
const WAREHOUSES = '/Warehouses';

export const stockCountsService = {
  getWarehouses: () => apiClient.get(WAREHOUSES),
};
