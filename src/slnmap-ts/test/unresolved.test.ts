import assert from 'node:assert/strict';
import { after, test } from 'node:test';
import { extract } from '../src/index.js';
import { createTempProject, removeTempProject } from './helpers.js';

/** The six closed unresolved categories — investigation §Q3.3. Nothing falls through
 * uncategorized: each test asserts the SPECIFIC category, not merely "unresolved". */

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

function singleUnresolved(root: string) {
  const artifact = extract({ projectRoot: root });
  const site = artifact.callSites[0];
  assert.ok(site, `expected exactly one call site; got: ${JSON.stringify(artifact.callSites, null, 2)}`);
  assert.equal(site!.kind, 'UnresolvedCallSite');
  return site as { category: string; reason: string; verb: string };
}

test('dynamic-base-url: URL built from an environment/runtime config value', () => {
  const root = project({
    'src/apiClient.ts': AXIOS_CLIENT,
    'src/call.ts': `
      const API_BASE = process.env.NEXT_PUBLIC_API_BASE as string;
      export function call(path: string) { return fetch(\`\${API_BASE}/\${path}\`); }
    `,
  });
  const site = singleUnresolved(root);
  assert.equal(site.category, 'dynamic-base-url');
});

test('runtime-computed-segment: segment built from a non-literal-preserving runtime computation', () => {
  const root = project({
    'src/apiClient.ts': AXIOS_CLIENT,
    'src/call.ts': `
      import apiClient from './apiClient';
      export function call() {
        const basePath = '/Reports/' + new Date().getFullYear();
        return apiClient.get(basePath);
      }
    `,
  });
  const site = singleUnresolved(root);
  assert.equal(site.category, 'runtime-computed-segment');
});

test('non-constant-identifier: let-declared binding, even when every assignment is a literal', () => {
  const root = project({
    'src/apiClient.ts': AXIOS_CLIENT,
    'src/call.ts': `
      import apiClient from './apiClient';
      let currentEndpoint = '/Vendors';
      export function call() {
        currentEndpoint = '/Vendors/v2';
        return apiClient.get(currentEndpoint);
      }
    `,
  });
  const site = singleUnresolved(root);
  assert.equal(site.category, 'non-constant-identifier');
});

test('unrecognized-callee: verb-shaped method call on a receiver that is not a known client', () => {
  const root = project({
    'src/call.ts': `
      const customClient = { get: (_url: string) => Promise.resolve(null) };
      export function call() { return customClient.get('/Custom/endpoint'); }
    `,
  });
  const site = singleUnresolved(root);
  assert.equal(site.category, 'unrecognized-callee');
  assert.equal(site.verb, 'GET'); // the verb is known from syntax even though the receiver isn't
});

test('dynamic-import-or-indirection: the client itself arrives through a dynamic import()', () => {
  const root = project({
    'src/apiClient.ts': AXIOS_CLIENT,
    'src/call.ts': `
      export async function call() {
        const client = await import('./apiClient');
        return client.default.get('/Deferred/endpoint');
      }
    `,
  });
  const site = singleUnresolved(root);
  assert.equal(site.category, 'dynamic-import-or-indirection');
});

test('resolution-depth-exceeded: a const chain deeper than the self-imposed recursion bound', () => {
  const chain = Array.from({ length: 10 }, (_, i) => i + 1)
    .map((n) => (n === 1 ? `const chain1 = '/Deep/endpoint';` : `const chain${n} = chain${n - 1};`))
    .join('\n');
  const root = project({
    'src/apiClient.ts': AXIOS_CLIENT,
    'src/call.ts': `
      import apiClient from './apiClient';
      ${chain}
      export function call() { return apiClient.get(chain10); }
    `,
  });
  const site = singleUnresolved(root);
  assert.equal(site.category, 'resolution-depth-exceeded');
});

test('a shadowed local variable named "fetch" is silently excluded, not reported unresolved', () => {
  const root = project({
    'src/call.ts': `
      export function useExportPreview(fetch: (url: string) => Promise<unknown>) {
        return fetch('/not-http-shaped-from-here');
      }
    `,
  });
  const artifact = extract({ projectRoot: root });
  assert.equal(artifact.callSites.length, 0, `shadowed fetch must produce no call site at all; got: ${JSON.stringify(artifact.callSites)}`);
});
