import assert from 'node:assert/strict';
import { after, test } from 'node:test';
import { extract } from '../src/index.js';
import { createTempProject, removeTempProject } from './helpers.js';

/**
 * The constant-folding matrix — investigation §Q3.2. Each row is the spike's own real case,
 * exercised end-to-end through the public `extract()` entry point against a real ts.Program.
 */

const cleanups: string[] = [];
after(() => {
  for (const root of cleanups) {
    removeTempProject(root);
  }
});

function project(files: Record<string, string>): string {
  const root = createTempProject(files);
  cleanups.push(root);
  return root;
}

const AXIOS_CLIENT = `
import axios from 'axios';
const apiClient = axios.create({ baseURL: '/api/ogw' });
export default apiClient;
`;

test('row 1: literal string argument resolves at resolutionTier "literal"', () => {
  const root = project({
    'src/apiClient.ts': AXIOS_CLIENT,
    'src/call.ts': `
      import apiClient from './apiClient';
      export function call() { return apiClient.get('/Vendors'); }
    `,
  });

  const artifact = extract({ projectRoot: root });
  assert.equal(artifact.callSites.length, 1);
  const site = artifact.callSites[0]!;
  assert.equal(site.kind, 'FrontendCallSite');
  assert.equal((site as { template: string }).template, '/Vendors');
  assert.equal((site as { resolutionTier: string }).resolutionTier, 'literal');
});

test('row 2: const identifier referenced at the call site resolves at "const-resolved"', () => {
  const root = project({
    'src/apiClient.ts': AXIOS_CLIENT,
    'src/call.ts': `
      import apiClient from './apiClient';
      const VENDORS = '/Vendors';
      export function call() { return apiClient.get(VENDORS); }
    `,
  });

  const artifact = extract({ projectRoot: root });
  const site = artifact.callSites[0]!;
  assert.equal(site.kind, 'FrontendCallSite');
  assert.equal((site as { template: string }).template, '/Vendors');
  assert.equal((site as { resolutionTier: string }).resolutionTier, 'const-resolved');
});

test('row 3: property access into a literal-typed const object (API_ROUTES-style) folds', () => {
  const root = project({
    'src/apiClient.ts': AXIOS_CLIENT,
    'src/routes.ts': `
      export const API_ROUTES = { userTasksSummary: '/UserTasks/assigned-tasks-with-summary' };
    `,
    'src/call.ts': `
      import apiClient from './apiClient';
      import { API_ROUTES } from './routes';
      export function call() { return apiClient.get(API_ROUTES.userTasksSummary); }
    `,
  });

  const artifact = extract({ projectRoot: root });
  const site = artifact.callSites[0]!;
  assert.equal(site.kind, 'FrontendCallSite');
  assert.equal((site as { template: string }).template, '/UserTasks/assigned-tasks-with-summary');
  assert.equal((site as { resolutionTier: string }).resolutionTier, 'const-resolved');
});

test('row 4: const-through-barrel (default->named rename) folds through the re-export chain', () => {
  const root = project({
    'src/services/apiClient.ts': AXIOS_CLIENT,
    'src/services/boardMeetingsService.ts': `
      import apiClient from './apiClient';
      const COMMITTEES = '/Committees';
      export const boardMeetingsService = { getCommittees: () => apiClient.get(COMMITTEES) };
    `,
    'src/services/index.ts': `
      export { default as apiClient } from './apiClient';
      export { boardMeetingsService } from './boardMeetingsService';
    `,
    'src/call.ts': `
      import { boardMeetingsService } from './services';
      export function call() { return boardMeetingsService.getCommittees(); }
    `,
  });

  const artifact = extract({ projectRoot: root });
  const site = artifact.callSites[0]!;
  assert.equal(site.kind, 'FrontendCallSite');
  assert.equal((site as { template: string }).template, '/Committees');
});

test('row 5: template literal with every hole resolved folds at "template-folded"', () => {
  const root = project({
    'src/apiClient.ts': AXIOS_CLIENT,
    'src/call.ts': `
      import apiClient from './apiClient';
      const BASE = '/Vendors';
      const ID = '42';
      export function call() { return apiClient.get(\`\${BASE}/\${ID}/notes\`); }
    `,
  });

  const artifact = extract({ projectRoot: root });
  const site = artifact.callSites[0]!;
  assert.equal(site.kind, 'FrontendCallSite');
  assert.equal((site as { template: string }).template, '/Vendors/42/notes');
  assert.equal((site as { resolutionTier: string }).resolutionTier, 'template-folded');
});

test('row 6: template literal with a genuinely runtime hole folds to an anonymous {*} token', () => {
  const root = project({
    'src/apiClient.ts': AXIOS_CLIENT,
    'src/call.ts': `
      import apiClient from './apiClient';
      const moduleMap: Record<string, string> = { compliance: 'compliances' };
      export function call(taskModule: string, taskId: string) {
        const resolvedModule = moduleMap[taskModule] || taskModule;
        return apiClient.post(\`/TaskCenter/\${resolvedModule}/\${taskId}/reminder\`, {});
      }
    `,
  });

  const artifact = extract({ projectRoot: root });
  const site = artifact.callSites[0]!;
  assert.equal(site.kind, 'FrontendCallSite');
  assert.equal((site as { template: string }).template, '/TaskCenter/{*}/{*}/reminder');
  assert.equal((site as { resolutionTier: string }).resolutionTier, 'template-param-holes');
});

test('5 distinct import spellings of the same axios instance all resolve to one identity', () => {
  const root = project({
    'src/shared/services/apiClient.ts': AXIOS_CLIENT,
    'src/shared/services/index.ts': `export { default as apiClient } from './apiClient';`,
    'src/callA.ts': `
      import { apiClient } from '@/shared/services';
      export function a() { return apiClient.get('/A'); }
    `,
    'src/callB.ts': `
      import { apiClient } from './shared/services';
      export function b() { return apiClient.get('/B'); }
    `,
    'src/callC.ts': `
      import { apiClient } from './shared/services/index';
      export function c() { return apiClient.get('/C'); }
    `,
    'src/nested/callD.ts': `
      import apiClient from '../shared/services/apiClient';
      export function d() { return apiClient.get('/D'); }
    `,
    'src/nested/deeper/callE.ts': `
      import { apiClient } from '../../shared/services';
      export function e() { return apiClient.get('/E'); }
    `,
  });

  const artifact = extract({ projectRoot: root });
  const resolved = artifact.callSites.filter((c) => c.kind === 'FrontendCallSite');
  assert.equal(resolved.length, 5, `all 5 import spellings should resolve; got: ${JSON.stringify(artifact.callSites, null, 2)}`);
  const templates = resolved.map((c) => (c as { template: string }).template).sort();
  assert.deepEqual(templates, ['/A', '/B', '/C', '/D', '/E']);
});
