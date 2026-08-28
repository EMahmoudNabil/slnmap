import assert from 'node:assert/strict';
import { after, test } from 'node:test';
import { extract } from '../src/index.js';
import { createTempProject, removeTempProject } from './helpers.js';

/**
 * `resolveHttpWrapper` (detection.ts) — tracing a verb-named call's receiver ONE level through a
 * locally-declared object/function wrapper whose own implementation forwards its first parameter
 * into a recognized low-level HTTP-client call. Modeled on the real
 * `gothinkster/react-redux-realworld-example-app` `src/agent.js` shape (field trial, 2026-08-28):
 * `requests.get: url => superagent.get(`${API_ROOT}${url}`)...` — every one of that repo's 22
 * frontend call sites was `unrecognized-callee` before this resolution path existed.
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

test('a wrapper object that cleanly forwards its parameter into a superagent call resolves', () => {
  const root = project({
    'src/call.ts': `
      import superagent from 'superagent';
      const API_ROOT = 'https://conduit.productionready.io/api';
      const requests = {
        get: (url: string) => superagent.get(\`\${API_ROOT}\${url}\`).then((res: any) => res.body),
      };
      export function fetchArticles() {
        return requests.get('/articles');
      }
    `,
  });
  const artifact = extract({ projectRoot: root });
  const site = artifact.callSites[0]!;
  assert.equal(site.kind, 'FrontendCallSite');
  assert.equal((site as { verb: string }).verb, 'GET');
  assert.equal((site as { template: string }).template, 'https://conduit.productionready.io/api/articles');
  assert.equal((site as { resolutionTier: string }).resolutionTier, 'template-folded');
});

test('the wrapper URL template and the original call argument fold together, holes included', () => {
  const root = project({
    'src/call.ts': `
      import superagent from 'superagent';
      const API_ROOT = 'https://conduit.productionready.io/api';
      const requests = {
        get: (url: string) => superagent.get(\`\${API_ROOT}\${url}\`).then((res: any) => res.body),
      };
      export function fetchArticle(slug: string) {
        return requests.get(\`/articles/\${slug}\`);
      }
    `,
  });
  const artifact = extract({ projectRoot: root });
  const site = artifact.callSites[0]!;
  assert.equal(site.kind, 'FrontendCallSite');
  assert.equal((site as { template: string }).template, 'https://conduit.productionready.io/api/articles/{*}');
  assert.equal((site as { resolutionTier: string }).resolutionTier, 'template-param-holes');
});

test('a wrapper forwarding into a plain axios.create() instance (not superagent) also resolves', () => {
  const root = project({
    'src/apiClient.ts': AXIOS_CLIENT,
    'src/call.ts': `
      import apiClient from './apiClient';
      const requests = {
        get: (url: string) => apiClient.get(url),
      };
      export function fetchVendors() {
        return requests.get('/Vendors');
      }
    `,
  });
  const artifact = extract({ projectRoot: root });
  const site = artifact.callSites[0]!;
  assert.equal(site.kind, 'FrontendCallSite');
  assert.equal((site as { template: string }).template, '/Vendors');
});

test('a standalone (non-object-literal) wrapper function also resolves', () => {
  const root = project({
    'src/call.ts': `
      import superagent from 'superagent';
      const httpGet = (url: string) => superagent.get(\`/api\${url}\`);
      const wrapper = { get: httpGet };
      export function fetchStatus() {
        return wrapper.get('/status');
      }
    `,
  });
  const artifact = extract({ projectRoot: root });
  const site = artifact.callSites[0]!;
  assert.equal(site.kind, 'FrontendCallSite');
  assert.equal((site as { template: string }).template, '/api/status');
});

test('unrecognized-callee: a wrapper whose body BRANCHES does not cleanly forward, so it is not guessed', () => {
  const root = project({
    'src/call.ts': `
      import superagent from 'superagent';
      const requests = {
        get: (url: string) => {
          if (url.startsWith('/health')) {
            return superagent.get('/api/status');
          }
          return superagent.get(\`/api\${url}\`);
        },
      };
      export function fetchHealth() {
        return requests.get('/health');
      }
    `,
  });
  const artifact = extract({ projectRoot: root });
  const site = artifact.callSites[0]!;
  assert.equal(site.kind, 'UnresolvedCallSite');
  assert.equal((site as { category: string }).category, 'unrecognized-callee');
});

test('unrecognized-callee: a wrapper that calls a recognized client with an UNRELATED url (no param forwarding) is not guessed', () => {
  const root = project({
    'src/call.ts': `
      import superagent from 'superagent';
      const requests = {
        get: (_url: string) => superagent.get('/api/health'),
      };
      export function fetchArticles() {
        return requests.get('/articles');
      }
    `,
  });
  const artifact = extract({ projectRoot: root });
  const site = artifact.callSites[0]!;
  assert.equal(site.kind, 'UnresolvedCallSite');
  assert.equal((site as { category: string }).category, 'unrecognized-callee');
});

test('unrecognized-callee: a destructured wrapper parameter cannot be cleanly substituted, so it is not guessed', () => {
  const root = project({
    'src/call.ts': `
      import superagent from 'superagent';
      const requests = {
        get: ({ url }: { url: string }) => superagent.get(\`/api\${url}\`),
      };
      export function fetchArticles() {
        return requests.get({ url: '/articles' });
      }
    `,
  });
  const artifact = extract({ projectRoot: root });
  const site = artifact.callSites[0]!;
  assert.equal(site.kind, 'UnresolvedCallSite');
  assert.equal((site as { category: string }).category, 'unrecognized-callee');
});

test('a wrapper found, but the ORIGINAL call site argument itself is non-constant, reports that specific category', () => {
  const root = project({
    'src/call.ts': `
      import superagent from 'superagent';
      const requests = {
        get: (url: string) => superagent.get(\`/api\${url}\`),
      };
      let endpoint = '/articles';
      export function fetchArticles() {
        endpoint = '/articles/v2';
        return requests.get(endpoint);
      }
    `,
  });
  const artifact = extract({ projectRoot: root });
  const site = artifact.callSites[0]!;
  assert.equal(site.kind, 'UnresolvedCallSite');
  assert.equal((site as { category: string }).category, 'non-constant-identifier');
});

test('DELETE via the superagent `del` alias, through a wrapper, resolves with the correct verb', () => {
  const root = project({
    'src/call.ts': `
      import superagent from 'superagent';
      const requests = {
        del: (url: string) => superagent.del(\`/api\${url}\`),
      };
      export function unfollow(username: string) {
        return requests.del(\`/profiles/\${username}/follow\`);
      }
    `,
  });
  const artifact = extract({ projectRoot: root });
  const site = artifact.callSites[0]!;
  assert.equal(site.kind, 'FrontendCallSite');
  assert.equal((site as { verb: string }).verb, 'DELETE');
  assert.equal((site as { template: string }).template, '/api/profiles/{*}/follow');
});
