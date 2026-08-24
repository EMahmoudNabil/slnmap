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

test('runtime-computed-segment: a bare, non-concatenated call expression result', () => {
  const root = project({
    'src/apiClient.ts': AXIOS_CLIENT,
    'src/call.ts': `
      import apiClient from './apiClient';
      export function call() {
        const basePath = String(new Date().getFullYear());
        return apiClient.get(basePath);
      }
    `,
  });
  const site = singleUnresolved(root);
  assert.equal(site.category, 'runtime-computed-segment');
});

test('string-concatenation (v0.12.2): a `+`-built URL with a non-constant operand is DISCLOSED, not silently dropped', () => {
  // Was `runtime-computed-segment` before v0.12.2 — reclassified because the concatenation
  // SHAPE is itself the more specific, more actionable fact (foreign-patterns-trial finding #3).
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
  assert.equal(site.category, 'string-concatenation');
});

test('string-concatenation: the exact reported shape — known client, base variable + literal suffix', () => {
  const root = project({
    'src/apiClient.ts': AXIOS_CLIENT,
    'src/call.ts': `
      import apiClient from './apiClient';
      export function call(base: string) {
        return apiClient.get(base + '/users');
      }
    `,
  });
  const site = singleUnresolved(root);
  assert.equal(site.category, 'string-concatenation');
  assert.equal(site.verb, 'GET');
});

test('string-concatenation: three-operand chain on an UNKNOWN receiver is disclosed as unrecognized-callee, not dropped', () => {
  // The real Angular shape from the foreign-patterns trial: '/profiles/' + username + '/follow'
  // on `this.http`, a receiver the extractor doesn't recognize as a known HTTP client. Before
  // v0.12.2 this call site produced NO node at all — not even counted unresolved.
  const root = project({
    'src/call.ts': `
      class ProfileService {
        constructor(private http: { post: (url: string, body: unknown) => Promise<unknown> }) {}
        follow(username: string) {
          return this.http.post('/profiles/' + username + '/follow', {});
        }
      }
    `,
  });
  const site = singleUnresolved(root);
  assert.equal(site.category, 'unrecognized-callee');
  assert.equal(site.verb, 'POST');
});

test('fully-constant concatenation still resolves normally — the fix does not regress the working case', () => {
  const root = project({
    'src/apiClient.ts': AXIOS_CLIENT,
    'src/call.ts': `
      import apiClient from './apiClient';
      const BASE = '/api';
      export function call() { return apiClient.get(BASE + '/users'); }
    `,
  });
  const artifact = extract({ projectRoot: root });
  const site = artifact.callSites[0]!;
  assert.equal(site.kind, 'FrontendCallSite');
  assert.equal((site as { template: string }).template, '/api/users');
});

test('a concatenation-shaped template hole still folds to {*}, not string-concatenation (no regression)', () => {
  const root = project({
    'src/apiClient.ts': AXIOS_CLIENT,
    'src/call.ts': `
      import apiClient from './apiClient';
      export function call(prefix: string, id: string) {
        return apiClient.get(\`/Items/\${prefix + id}\`);
      }
    `,
  });
  const artifact = extract({ projectRoot: root });
  const site = artifact.callSites[0]!;
  assert.equal(site.kind, 'FrontendCallSite');
  assert.equal((site as { template: string }).template, '/Items/{*}');
  assert.equal((site as { resolutionTier: string }).resolutionTier, 'template-param-holes');
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

test('fluent chain (v0.12.2): two verb-named links on one statement get DISTINCT positions, not a collision', () => {
  // The real Turborepo kitchen-sink shape from the foreign-patterns trial: two `.get(...)`
  // registrations chained onto the same Express app statement. Before v0.12.2, both reported
  // the exact same line/column/spanStart (the chain's own leftmost token), which collapsed the
  // second call site into the first downstream (4 reported, 3 persisted) since node identity
  // keys on that position.
  const root = project({
    'src/server.ts': `
      import express from 'express';
      export function createServer() {
        const app = express();
        app
          .disable('x-powered-by')
          .get('/message/:name', (req: unknown, res: { json: (b: unknown) => void }) => {
            return res.json({ ok: true });
          })
          .get('/status', (_req: unknown, res: { json: (b: unknown) => void }) => {
            return res.json({ ok: true });
          });
        return app;
      }
    `,
  });

  const artifact = extract({ projectRoot: root });
  const sites = artifact.callSites.filter((c) => c.kind === 'UnresolvedCallSite') as Array<{
    line: number;
    column: number;
    spanStart: number;
  }>;

  assert.equal(sites.length, 2, `both chained .get() calls must be extracted as distinct call sites; got: ${JSON.stringify(artifact.callSites, null, 2)}`);
  assert.notEqual(
    `${sites[0]!.line}:${sites[0]!.column}`,
    `${sites[1]!.line}:${sites[1]!.column}`,
    'two distinct links in a fluent chain must never report the same line:column',
  );
  assert.notEqual(sites[0]!.spanStart, sites[1]!.spanStart, 'two distinct links in a fluent chain must never share spanStart');
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
