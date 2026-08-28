// Modeled on gothinkster/react-redux-realworld-example-app's src/agent.js (MIT License),
// trimmed to the one shape that matters here: an HTTP-verb wrapper OBJECT whose methods forward
// their `url` parameter into a real low-level HTTP client call, one level removed from the call
// site that actually uses it. Real-world field trial (2026-08-28, reports/...): this exact shape
// made every one of that repo's 22 frontend call sites unresolvable before `resolveHttpWrapper`
// (detection.ts) existed -- `requests` is a plain object literal, not an `axios.create()`
// instance, so `isKnownHttpClient` alone can never see through it to the `superagent.get(...)`
// call happening one level down inside `requests.get`'s own body.

import superagent from 'superagent';

const API_ROOT = 'https://conduit.productionready.io/api';

const tokenPlugin = (req: any) => req;
const responseBody = (res: any) => res.body;

const requests = {
  get: (url: string) => superagent.get(`${API_ROOT}${url}`).use(tokenPlugin).then(responseBody),
  post: (url: string, body: unknown) => superagent.post(`${API_ROOT}${url}`, body as object).use(tokenPlugin).then(responseBody),
  del: (url: string) => superagent.del(`${API_ROOT}${url}`).use(tokenPlugin).then(responseBody),
};

// Category: unrecognized-callee -- looks like the same wrapper shape, but the body BRANCHES on
// its own parameter instead of cleanly forwarding it into the recognized client call.
// `resolveHttpWrapper` requires a single, clean forwarding expression (investigation-style
// "never guess" rule) -- a branch means which superagent call actually runs isn't a static fact,
// so this correctly stays unrecognized rather than resolving to either branch.
const inconsistentRequests = {
  get: (url: string) => {
    if (url.startsWith('/health')) {
      return superagent.get(`${API_ROOT}/status`).then(responseBody);
    }
    return superagent.get(`${API_ROOT}${url}`).then(responseBody);
  },
};

export const Articles = {
  all: (page: number) => requests.get(`/articles?limit=10&offset=${page}`),
  favorite: (slug: string) => requests.post(`/articles/${slug}/favorite`, {}),
  unfavorite: (slug: string) => requests.del(`/articles/${slug}/favorite`),
};

export function fetchHealth() {
  return inconsistentRequests.get('/health');
}
