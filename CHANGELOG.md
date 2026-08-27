# Changelog

All notable changes to Slnmap are documented here. Versions follow [SemVer](https://semver.org).

## 0.12.3

### Fixed

- **The cross-stack linker double-prefixed call sites whose own path already included the base
  path**, reporting `NoSkeletonMatch` on every one — including byte-identical literal paths (a
  frontend `fetch('/api/orders')` against a real `/api/orders` endpoint). The linker
  unconditionally prepended its `/api` base-path prefix before matching; a call site that already
  carried the prefix (any codebase that doesn't route it through an axios `baseURL` — a bare
  `fetch()`/`XMLHttpRequest` call, or any client without base-URL config) got matched as
  `/api/api/orders`, which can never skeleton-match `/api/orders` — different segment count,
  rejected before any string comparison happens. Confirmed present, unchanged, in every published
  version through 0.12.2, and not caught by prior field trials because their codebase's own
  convention (axios `baseURL` absorbs the prefix, call sites never carry it literally) never
  exercised the failing shape. Fixed by trying both the call site's raw path and its
  prefixed path as independent match candidates: if exactly one matches, link through it as
  before (no change for the already-working case); if both independently match a real endpoint,
  that's disclosed as a set edge with its own reason rather than silently guessing; if neither
  matches, `NoSkeletonMatch` as before.

### Added

- **`slnmap link --base-path <prefix>`** — the base path is now configurable (default `/api`,
  matching prior behavior). Pass `--base-path ""` to disable it entirely for a codebase whose
  call sites already carry their full path.

## 0.12.2

### Fixed

- **Two real cross-stack-linker/TS-extractor bugs, caught by the same pre-launch discipline that
  found v0.12.1's linker bug** — a foreign-pattern trial deliberately outside the tool's usual
  React+.NET test corpus. Neither reaches OSSUS_Frontend or the published RealWorld sample;
  both are confirmed exact-match (reported == persisted) on both before and after this fix.
  - A fluent method chain (two HTTP-verb-named calls on one statement, e.g. an Express
    `app.use(...).get(A).get(B)` route-registration idiom) reported byte-identical position data
    for every link, silently collapsing the second call site into the first downstream. Fixed by
    anchoring each link's position to its own callee name instead of the whole chain's start.
  - A `+`-built URL argument on an unrecognized HTTP-client receiver (e.g. Angular's
    `this.http.post('/x/' + id + '/y', ...)`) was silently dropped instead of disclosed whenever
    it didn't fully constant-fold — the same free pass a template literal already got, now
    extended to string concatenation. A new `string-concatenation` unresolved category covers the
    equivalent case on a recognized client (`axios.get(base + '/users')`). Disclosure only, by
    design — a non-constant concatenation operand is still never resolved to a template.

### Added

- **`analyze` now detects and discloses two previously-silent gaps** instead of reporting a clean
  "0 skipped": `.razor` files present on disk (Blazor component markup is not analyzed — a
  file-system-level check, since Roslyn's own document set unreliably excludes them) and Razor
  Pages (`PageModel`-derived classes with `OnGet`/`OnPost`/... handlers — route by file location,
  a different routing system, disclosed the same way conventionally-routed MVC controllers
  already are). Neither is modeled; both are now honestly counted, with a note on `analyze`'s
  summary and the same disclosure `find_endpoint`/`list_endpoints` give conventional routing.

## 0.12.1

### Fixed

- **The cross-stack linker no longer reports false ambiguity between unrelated,
  literal-anchored routes.** Caught by a pre-launch field trial on a second, larger codebase:
  cross-position literal absorption produced false ambiguity edges, and `impact_analysis`
  over-reported blast radius on the affected sites. `RouteTemplate.Matches` treated a hole
  absorbing the other side's literal as independently excusable at each segment position,
  regardless of direction — so two completely unrelated routes could coincidentally
  skeleton-match when a call site's hole absorbed one endpoint's literal at one position while
  that same endpoint's hole absorbed a different call-site literal at another position. Absorption
  is now required to run in one direction per comparison; genuine fan-out and route-precedence
  shapes are unaffected. Re-measured on the reference field-trial pair: 24 → 11 false set-edges
  resolve to their one correct endpoint; linked/disclosed totals are unchanged (407/434 either
  way) — this corrects **how** call sites resolve, not whether they do.

## 0.12.0

### Added

- **`slnmap link`** — the cross-stack linker, the final piece of the frontend-analysis project.
  Joins every `FrontendCallSite` (0.11.0) to the C# `Endpoint` (0.7.0/0.8.0) it actually hits, via
  a new `CallsEndpoint` edge: exact verb matching (an honestly-unresolvable verb is disclosed,
  never guessed), route-precedence tie-breaking scoped to the one case it's actually knowable (a
  literal call site beats a parameterized sibling endpoint — the same way ASP.NET's own router
  would, not a general precedence engine), and a truthful set edge for real runtime fan-out and
  irreducible ambiguity alike — never a guessed single link. Full recompute every run (drops and
  rebuilds every `CallsEndpoint` edge), cheap even at real scale. Every call site lands in exactly
  one of six disclosed outcomes; nothing is ever silently dropped.
- **`impact_analysis` and `find_usages` now continue through a C# handler's endpoint to its
  frontend callers** once `slnmap link` has run — "what breaks in the React app if I change this
  handler?" is a real, checkable answer from the same tool, not a separate query. `find_endpoint`
  gained a "Called from the frontend by:" line for the same reason.
- **`find_orphan_calls`** and **`list_frontend_callsites`** — two new MCP tools (13 → 15).
  `find_orphan_calls` groups unlinked call sites by exact reason: no endpoint resembles this path
  at any verb, an endpoint shares the exact route under a different verb (named in the message,
  e.g. `"no POST registered; GET /api/OrganizationUsers exists"`), or the extractor genuinely
  couldn't determine the verb. Both tools compute live against the current graph, so they're
  never stale even between `slnmap link` runs.
- **A staleness note on `analyze`/`analyze-ts`** when stored `CallsEndpoint` edges may now be
  older than the graph — one line, informational, pointing at `slnmap link`.

### Field-trial results

Run against a real, live production codebase (433 `Endpoint` nodes[^658], 0 unresolved
registrations; 434 frontend call sites extracted): **407 of 434 real call sites (93.8%) link
deterministically or via the precedence rule.** Every one of the remaining 27 is individually
disclosed by exact reason (23 no match, 4 verb mismatch) — including the specific production bug
that motivated this entire project: a frontend `POST` to `/organizationusers` with no matching
registration, surfacing exactly as designed under `verb-mismatch`, naming the
`GET /api/OrganizationUsers` registration it happens to share a route shape with. Two additional
real verb mismatches this project's own investigation phase hadn't previously named turned up in
the same run.

[^658]: an earlier field-trial report measured 658 endpoints on a separate, larger snapshot of
the same application; this release's own field-trial checkout measured 433 — a real difference
between two distinct snapshots, not an inconsistency in this run.

### Notes

- **`slnmap link` requires both `slnmap analyze` and `slnmap analyze-ts` to have run first**, and
  should be re-run after either one changes the graph — a re-analyze wipes and rebuilds the
  Endpoint/FrontendCallSite populations it draws edges between, so previously-computed links no
  longer describe the current graph until `link` runs again.
- **The route-precedence rule is deliberately narrow**: literal-beats-parameter only when the
  call site's own segment is a known, concrete value. When it's genuinely unknown (a
  runtime-chosen value), every matching endpoint gets a truthful set edge rather than a guess —
  the same treatment as real fan-out, by design; the linker does not distinguish the two.
- **No general ASP.NET route-precedence engine** (constraint-type ranking beyond literal-vs-hole,
  catch-all routes, custom `IRouteConstraint`s) — the field-trial data never needed one.

## 0.11.0

### Added

- **`slnmap analyze-ts <frontend-root>`** — frontend HTTP call sites now land in the same code
  graph as your C# code. Walks a TypeScript/React project with the TypeScript Compiler API (via
  the companion [`slnmap-ts`](https://www.npmjs.com/package/slnmap-ts) npm package) and resolves
  each call site to a route template, deterministically: literal strings, `const`-through-barrel
  re-exports, `API_ROUTES`-style object properties, and template literals with a mix of
  const-folded segments and genuinely runtime holes (rendered as an anonymous `{*}`). Detection
  covers axios instances (`axios.create()`, tracked by declaration through however many
  re-export/rename hops) and the ambient global `fetch` (never a locally shadowed one). Two new
  node kinds, `FrontendCallSite` and `UnresolvedCallSite` — zero database schema changes (`kind`
  is a plain `TEXT` column; an older `slnmap` binary reading a newer graph degrades unknown kinds
  to `Unknown` instead of crashing, the same forward-compatibility contract `Endpoint` shipped
  under in 0.7.0).
- **The unresolved bucket is disclosed, not hidden**, across six named categories
  (`dynamic-base-url`, `runtime-computed-segment`, `non-constant-identifier`,
  `unrecognized-callee`, `dynamic-import-or-indirection`, `resolution-depth-exceeded`) — every
  call site the extractor can't resolve statically is counted and labeled with why, never
  silently dropped and never guessed. `analyze-ts --verbose` prints the per-category breakdown
  alongside the resolved/unresolved summary line.
- **Node 18+ required for `analyze-ts`** — checked explicitly, with a clean, actionable message
  (not a stack trace) if missing. The `slnmap-ts` package itself is fetched automatically via
  `npx` on first use (a local/global install is preferred first if one exists, for
  air-gapped/CI setups) — nothing to install by hand beyond Node itself.

### Field-trial results

Verified across 4 codebases before shipping — two independent, real production snapshots of the
same application (~2 weeks/one refactor apart), a frontend-only static site, and a deliberately
foreign public sample (plain JavaScript, no TypeScript at all, a third HTTP client library):
**zero false positives anywhere.** 95.0% and 95.5% of call sites resolved on the two real-app
snapshots respectively — consistent across time, not a one-snapshot fluke. One real gap found and
fixed pre-release: `superagent`'s official `.del()` method name for DELETE was not in the
recognized verb set, silently invisible rather than merely unresolved.

### Notes

- **This is the extraction half only — cross-stack linking (matching a frontend call site to the
  backend `Endpoint` it hits) is not in this release.** Both node populations now live in one
  graph, and the route-template normalizer needed for the eventual join (`RouteTemplate`,
  shipped under `find_endpoint` since 0.7.0) is already verified byte-compatible between the two
  producers — but nothing today draws the edge between them. That's next.
- **Monorepo / TypeScript project-references tsconfigs are not yet field-verified.** Every
  project exercised so far uses a single, flat `tsconfig.json`. The loader understands
  `references` in principle (`ts.parseJsonConfigFileContent`), but this has not been run against
  an Nx/Turborepo-style setup.
- **A URL built from a helper function's own parameter is correctly counted unresolved, not
  guessed from its callers** — resolution is call-site-local by design; tracing every call site
  of a shared helper to see what its callers pass is a different, larger feature (interprocedural
  analysis), not attempted here.

## 0.10.0

### Added

- **`slnmap watch <solution>`** — the roadmap's top item, shipped. Analyzes once, keeps the
  Roslyn `MSBuildWorkspace` resident, and re-analyzes on every file save. The expensive part of a
  re-run was never the analysis — it was reopening the workspace (~90% of the cost, measured) —
  so watch pays it once: a one-file semantic change on a 10-project solution (eShopOnWeb) lands
  in the database in ~1 s (`re-analyzed 1 file(s) in 0.93s … saved in 0.17s`), versus a full
  workspace reload for run-and-exit `analyze`. Each batch prints one line with the re-analysis
  time, graph size, and save time; Ctrl+C exits cleanly.
- **Designed to run beside `slnmap serve`** — the server reads the same database file and
  survives the atomic save swap mid-query (pinned by test before watch existed), so a connected
  agent's answers stay fresh while you type.
- **Honest fallbacks, no guessing**: `.cs` content edits ride the warm path; structural changes
  (`.csproj`/`.sln`/`.slnx`/`.props`/`.targets`/`global.json`) and brand-new files trigger an
  *announced* full workspace reload — csproj glob semantics make in-snapshot document membership
  a guess, and Slnmap doesn't guess. The reload itself stays hash-incremental. Editor save storms
  are debounced (400 ms) and coalesced; a failed batch warns and keeps watching; saves are
  skipped entirely when the graph is unchanged (a whitespace-only save costs milliseconds).

### Changed

- The analysis core is now shared between the cold path (`analyze`) and the resident path
  (`watch`) — one implementation, two entry points. Cold-path behavior is unchanged (same graph,
  same incremental planner, same warnings).

### Notes

- The resident workspace holds compilations in memory — expect hundreds of MB on very large
  solutions. Watch is an opt-in dev-loop verb; `analyze` remains the CI/one-shot path.
- On very large graphs (100k+ edges) the database save dominates the loop (~7 s measured);
  a delta-save is the documented fast-follow.

### Upgrading

Nothing required — existing databases keep working, and `watch` picks up an existing
same-version database exactly like `analyze` does.

## 0.9.0

### Added

- **Enum members are first-class graph nodes** (#13) — new kind `EnumMember`. Every member
  materializes via the declaration walk, referenced or not (the census-consistency concern that
  kept them unmodeled), and usage sites produce `References` edges to the member itself — so
  `find_usages`/`impact_analysis` answer at `MyEnum.Value` granularity, the same "who uses this
  domain value" question v0.6.1 answered for consts. The enum-TYPE edge is unchanged; member
  granularity is additive. Enumerants are deliberately NOT labeled `Field`, keeping field
  censuses honest.
- **Event usage is tracked** (#8) — `+=`/`-=` subscription sites and `Event?.Invoke(...)` raising
  sites now produce `References` edges to the event node. `find_usages` on an event finds its
  subscribers and raisers (previously: "No usages found" for every event, as observed on
  eShopOnWeb's `RefreshBroadcast.RefreshRequested` — now returns its two subscription sites and
  the raiser exactly). Both fold into `References`, consistent with the field/const precedent.
- **`typeof()` inside assembly-level attributes produces a reference** (#11) — attributed to the
  project node (the assembly IS the project), so
  `[assembly: HostingStartup(typeof(MyStartup))]` makes `impact_analysis(MyStartup)` answer
  honestly instead of "nothing depends on it".

### Verified, no change needed

- Generic type arguments to **external** framework methods (`UseMiddleware<T>()`, #9) already
  produce edges — the issue's reconstructed repro does not reproduce on current code; a pinning
  test now guards the behavior permanently.

### Known limits

- Removing an assembly-level attribute leaves its project-sourced reference edge until the next
  full re-analysis (project nodes have no file for incremental eviction to key on). Adding one is
  incrementally correct.

### Upgrading

Just re-run `slnmap analyze` — the tool-version check forces the one-time full rebuild; enum
members and event edges appear after it.

## 0.8.2

### Fixed

- **`analyze` progress no longer floods redirected/CI output** (#16). The single-line progress
  display relies on carriage-return rewrites that only work on a live terminal — captured output
  (logs, CI, `Tee-Object`) got one line per analyzed document, thousands per run. Redirected
  stderr now prints milestone lines only: the first report of each stage plus every 10%.
  Interactive terminals are unchanged.
- **`viz` no longer stacks edge-count labels between the same node pair** (#17). Multiple edge
  kinds between two projects (either direction) drew straight overlapping lines with unreadable
  overlapping counts at the collapsed level; parallel bundles now curve apart so both the lines
  and their labels are legible.

## 0.8.1

### Changed — MCP failures are now machine-checkable (#15)

Production feedback from MCP-server operators (the r/mcp launch thread) reshaped how tool
failures reach the model. Success payloads are untouched — correct calls see zero difference.

- **Failures return as normal tool results, never protocol-level `isError`** (clients render
  isError inconsistently; a plain result always reaches the model). The payload is a small JSON
  object with `status`/`code`/`message`/`hint`, the offending parameter, and the valid parameter
  list — state a model can act on, not an exception string. Codes: `invalid_parameter` /
  `missing_parameter` (hint `fix_call`) and `internal_error` (hint `retry`, with the
  re-run-analyze remediation). "Not found" deliberately stays a normal prose answer with
  suggestions — zero results is an answer, not a malfunction.
- **Wrong-parameter calls are corrective now**: `find_usages(symbol: …)` used to return the
  opaque `"An error occurred invoking 'find_usages'"` with the real cause visible only in the
  server's stderr; it now returns
  `unknown parameter 'symbol' — this tool takes fqn (required, string)` plus the machine fields.
- **Unknown arguments are no longer silently dropped.** Previously, a wrong parameter name on a
  tool whose parameters are all optional (e.g. `get_project_dependencies(projekt: …)`) ran with
  defaults and returned the full unfiltered result — a failure shaped exactly like a success.
  Now a hard, corrective failure.
- **Sanitization is structural**: every tool call flows through one wrapper; stack traces, file
  paths, and internal type names can never reach a payload (full detail still goes to stderr for
  humans). Validation runs against each tool's own advertised schema, so messages can't drift
  from the contract.
- **Every tool description now carries one concrete example invocation** (`Example: {…}`), and
  the test suite executes each example against a real analyzed graph — an example can never rot.
- Fixed: `slnmap serve` against a corrupt/non-Slnmap database file now fails with the same clean
  two-line message `status`/`viz` use, instead of crashing with a raw unhandled exception.

### Upgrading

No action needed — the graph format is unchanged; only tool-failure payloads and descriptions
changed. Re-analysis is forced by the version check as usual on your next `slnmap analyze`.

## 0.8.0

### Added

- **Attribute-routed ASP.NET Core controllers now produce `Endpoint` nodes.** Public ordinary
  actions on non-abstract `ControllerBase`-derived classes — including base classes reached
  through package metadata (Ardalis.ApiEndpoints works out of the box) — become
  `VERB /route/template` nodes with the same shape, edges, MCP tools, and incremental behavior as
  Minimal-API endpoints (v0.7.0). Composition follows MVC's own selector semantics: class-level
  `[Route]` (nearest declared or inherited; multiple = one route each), templated
  `[HttpGet("…")]`/`[Route("…")]` attributes each yield a route, bare verb attributes constrain
  them or ride the class template, absolute (`/…`) templates override, and
  `[controller]`/`[action]`/`[area]` tokens substitute with MVC's conventions (`Controller`/
  `Async` suffix stripping). Duplicate verb+template collapses onto one node across both
  extractors.
- **Conventionally-routed controllers are detected and disclosed, never silently absent**: a
  controller with no route attributes anywhere in its inheritance chain is a different routing
  system (`MapControllerRoute`), not an extraction failure — the analyze summary, a per-class
  warning, and a `list_endpoints` note all say so, so "0 endpoints" on an MVC codebase is never
  a mystery.
- Deterministic-or-declared, as always: verb-less attribute-routed actions, actions declared on
  abstract controllers, unknown route tokens, and verbs outside the modeled set
  (`HEAD`/`OPTIONS`/`AcceptVerbs`) are counted and reported with reasons.

### Known limits

- A route registered with an identical verb+template in two **different files** can lose one of
  its `HandledBy` edges under *incremental* re-analysis (full analysis is unaffected). ASP.NET
  itself rejects such duplicates at request time, so real codebases don't ship them; documented
  for completeness.
- Conventional routing (`MapControllerRoute` patterns) and Razor Pages remain unmodeled —
  detected and disclosed where applicable, as above.

### Upgrading

Just re-run `slnmap analyze` — the tool-version check forces the one-time full rebuild
automatically; controller endpoints appear in the graph after it.

## 0.7.0

### Added

- **HTTP endpoints are first-class graph nodes.** Every ASP.NET Core Minimal-API registration
  (`MapGet`/`MapPost`/`MapPut`/`MapDelete`/`MapPatch`) becomes an `Endpoint` node whose identity
  is the composed route template as authored (`GET /api/vendors/{id:int}` — parameter names and
  constraints preserved), located at the registration call site and linked to its handler method
  by a new `HandledBy` edge. `impact_analysis` and `find_usages` on a handler now surface the
  actual HTTP routes that break — the chain `command → handler → endpoint` resolves end to end.
- **Two new MCP tools** (eleven → thirteen):
  - `list_endpoints(verb?, prefix?)` — all endpoints grouped by project as
    `VERB /route → handler — file:line`, optionally filtered by HTTP verb and/or route prefix.
  - `find_endpoint(route, verb?)` — matches an exact template *or* a concrete request path
    (`/api/vendors/42` finds `GET /api/vendors/{id}`), with the framework's own semantics:
    case-insensitive, `{param}`/`{param:constraint}` holes bind concrete segments. A miss
    suggests near matches.
- **Route templates resolve statically through real-world registration shapes**, decided by
  overload resolution (never argument position): string literals; `const` patterns;
  `string.Empty`; omitted patterns (overload defaults); literal and nested `MapGroup` prefixes;
  single-hop in-source forwarders (custom reversed-argument `Map*(handler, pattern)` extensions);
  and the CleanArchitecture-template convention (`MapGroup(this)` → `$"/api/{GetType().Name}"`),
  guarded by the receiver type having no in-solution subtypes. Field verification on a 26k-node
  production solution: **658 of 658 registrations resolved, zero unresolved**.
- **Deterministic-or-declared:** every registration that cannot be resolved statically is counted
  and reported with a reason (analyze summary line, warnings, and a disclosure note in
  `list_endpoints`) — never guessed. Lambda/local-function handlers yield an endpoint node
  without a `HandledBy` edge, declared as such.
- **Forward-compatible graph reading:** a database written by a newer Slnmap whose node/edge
  kinds this binary does not know now degrades gracefully (kinds map to `Unknown`, one warning)
  instead of crashing older readers — `Endpoint`/`HandledBy` are the first kinds to cross that
  line, but the hardening covers all future ones.
- `slnmap viz` renders Endpoint nodes (own color, legend entry) and `HandledBy` edges.

### Known limits

- **Attribute-routed controllers (`[Route]`/`[HttpGet]`) are not modeled yet** — planned as a
  follow-up with the same node shape (separate extractor). Conventional routing
  (`MapControllerRoute`), `MapFallback`/`MapHub`/health-check/gRPC surfaces, and middleware-served
  paths are also out of scope for now.
- A `Map*` forwarder declared in a *different project* than its call site cannot be verified from
  the caller's compilation — such registrations are counted unresolved, never guessed.

### Upgrading

Just re-run `slnmap analyze` — the tool-version check forces the one-time full rebuild
automatically; endpoints appear in the graph after it.

## 0.6.1

### Fixed

- **`find_usages`/`impact_analysis` on fields and constants now return real usage edges.**
  Fields and consts had nodes (v0.5.0) and outgoing references (v0.6.0), but no INCOMING usage
  edge was ever recorded — so `find_usages` on any field or const confidently answered
  "No usages found" and `impact_analysis` answered "nothing else depends on it", even with real
  usages across many files. On codebases that use const classes for domain values instead of
  enums, that was a repo-wide false "safe to change". Reads, writes, compound assignments,
  argument/interpolation positions, `nameof(...)` sites, and reads from other fields'
  initializers all now produce `References` edges attributed to the correct enclosing member.
  Field evidence that drove the fix: a reference-solution eval found a const with 5 real usages
  in 3 files reported as unused.
- **Structurally identical anonymous types no longer collapse into one node.** Anonymous types
  render structural FQNs (`<anonymous type: int Id, string Label>`), and FQN is node identity —
  so identical shapes declared in *different projects* merged into a single node pinned to
  whichever file was analyzed first, fabricating cross-project dependency rows in
  `get_architecture_overview` (the eval saw 9 false rows make a clean-architecture solution look
  layering-violating). Anonymous types (and their properties) now produce no nodes at all: they
  are unnameable and unqueryable, and their only observed graph effect was these false edges plus
  census inflation.
- **Also guarded: named tuple elements.** The field-edge fix above would have recreated the same
  collapse defect under a different type kind — named tuple elements are in-source field symbols
  whose FQNs (`(string From, string To).From`) are identical across files, and their containing
  tuple type never gets a node, so they'd also float disconnected. Caught by the pre-ship
  benchmark gate's node-diff; excluded by the same guard.

### Known remaining limits

- **Enum members are still not modeled as nodes** — `find_usages` at enum-member granularity
  (`MyEnum.Value`) is not available; the usage is visible at enum-type granularity only. This was
  deliberately kept out of this release: enabling it via the new field-edge path produced an
  inconsistent census (only *referenced* members would materialize as nodes), so it needs its own
  declaration-walk feature. Tracked in #13.

**Upgrade note:** just update and re-run `slnmap analyze` — the v0.6.0 tool-version check detects
the upgrade and forces the full rebuild automatically; no flags, no manual `slnmap.db` deletion.

## 0.6.0

### Fixed

- **Fully-qualified type references (no `using` shortcut) now produce `References` edges.**
  Closes the exact gap the previous release's own "known remaining limits" flagged: a parameter,
  field, or return type spelled out fully (e.g. `Fixture.Lib.SomeType` with no
  `using Fixture.Lib;` in scope) was invisible to `find_usages`/`impact_analysis`, caught by the
  same syntax-exclusion rule that (correctly) ignores `using` directives themselves — that
  exclusion has been narrowed to no longer swallow this valid reference shape too. This is the
  single largest contributor to this release's edge growth (see Benchmarks below). (Fixes #4)
- **Incremental re-analysis no longer silently drops edges owned by a re-walked dependent file's
  own declarations.** A whitespace-only touch to a file that other files depend on could
  previously cause a subsequent incremental `analyze` to produce *fewer* edges than a cold
  analyze of the identical state — a silent correctness bug, not a performance one. Verified via
  two real-world repro points on eShopOnWeb (a single-dependency touch and a 20-file fan-in
  touch): incremental edge count now matches cold edge count exactly in both cases. (Fixes #6)
- **`analyze` now detects a `slnmap` tool-version change and forces a full rebuild automatically.**
  Re-analyzing an existing `slnmap.db` written by a different tool version no longer risks mixing
  graph output from two different analyzer behaviors — a version mismatch is treated the same as
  an empty/missing database, with a warning printed to `stderr` (`serve` still starts; the warning
  is advisory, not a hard failure). No action needed on your part; this triggers automatically the
  next time you upgrade and re-run `analyze`.

### Added

- **Events are now modeled as graph nodes.** `public event EventHandler Foo;` (field-style) and
  explicit-accessor (`event` with `add`/`remove`) declarations are both first-class `Event` nodes,
  findable via `find_symbol` and `find_symbol(kind: "Event")` — the same visibility fields got in
  v0.5.0. **Event subscription and invocation are not yet tracked as usage edges** (the same
  limitation fields have — see "Known remaining limits" below): a `+=`/`-=` subscription site or
  an `?.Invoke()` raise site produces no edge to the event itself today. Node visibility is real
  and useful on its own (an agent can now find an event by name at all, which it couldn't
  before); don't read this as full usage-tracking parity with methods/classes yet. Tracked as a
  follow-up in #8. (Partially addresses #5)

### Benchmarks

Measured on the `eShopOnWeb` benchmark target (10 projects, 279 files; see
[BENCHMARKS.md](BENCHMARKS.md) for full methodology), before/after this release, same machine,
same session:

| Metric | v0.5.0 | v0.6.0 |
|---|---|---|
| Graph size | 1,311 nodes / 2,922 edges | 1,332 nodes / 3,014 edges (**+1.6% / +3.1%**) |
| Cold analyze (median of 3) | 22.0 s | 20.9 s (flat, within run-to-run noise) |
| Incremental analyze (median of 3) | 18.9 s | 18.7 s (flat) |

The fully-qualified-reference fix (above) accounts for 89 of the 92 new edges; the event-node fix
contributes the remaining 3 (structural containment edges for the 3 new `Event` nodes — consistent
with it not yet producing usage edges, see above). Edge growth is well under the 2.5x threshold
that would call for gating this behind a flag.

**Upgrade note:** re-run `slnmap analyze` on your existing `slnmap.db` to pick up the new edges
and event nodes — **this now happens automatically** the moment `analyze` detects it was last
written by a different tool version (see the tool-version-check fix above), so a normal upgrade-
and-reanalyze needs no extra flag or manual `slnmap.db` deletion.

**Known remaining limits** (not fixed by this release):
- **Event subscription/invocation is not yet tracked as usage edges** — `find_usages` on an event
  currently returns no results even when real subscribers exist in your code. Node visibility
  (`find_symbol`) works; usage tracking doesn't yet. (#8)
- A generic type argument passed to an **external** (framework/library) generic method — e.g.
  `app.UseMiddleware<T>()` — still doesn't produce an edge, even though the v0.5.0/v0.6.0 fixes
  cover the same shape for first-party generic methods. (#9)
- A fully-qualified `typeof()` reference inside an **assembly-level attribute**
  (`[assembly: SomeAttribute(typeof(X))]`) produces no edge — there's no containing member for the
  analyzer to attribute the reference to. Architecturally explainable, low priority. (#11)
- NuGet package references and `Directory.Packages.props` version pins remain out of the graph
  by design — a deliberate model boundary, not a gap.

## 0.5.0

### Fixed

- **Type references reachable only through a generic type argument, `typeof()`, or an attribute
  constructor argument were invisible to `find_usages` and `impact_analysis`.** Found by
  dogfooding Slnmap against a real 26k-node, 5-project ASP.NET solution: a middleware class
  registered only via `app.UseMiddleware<T>()`, a Hangfire job type passed only as
  `RecurringJob.AddOrUpdate<T>(...)`, or a class named only in `typeof(X)` inside a lookup table
  all reported **zero usages and zero impact** — even though each was genuinely load-bearing.
  That's not merely incomplete: an agent asking "is it safe to delete/rename this?" got an
  actively wrong "yes." Generic type arguments, bare `typeof(X)`, and attribute arguments
  (`[Foo(typeof(X))]`) now all produce `References` edges, same as any other type mention.
  (Fixes #1)
- **Fields were not modeled as graph nodes.** `find_symbol` couldn't find a field by name at all
  — the only workaround was `get_symbol_source` on the containing class and reading the body as
  text. This hit exactly the symbols agents ask about most: version constants, feature-flag sets,
  `typeof`-keyed lookup tables (e.g. `private static readonly HashSet<Type> KnownTypes = { ... }`).
  Fields are now first-class `Field` nodes, findable via `find_symbol` and `find_symbol(kind:
  "Field")`; a field's initializer expressions (including the `typeof(...)` fix above) now
  attribute their `References` edges to the field itself, not to the containing class. Additive
  only — no schema migration (`nodes.kind` is stored as free-form text, not a constrained/int
  column). (Fixes #2)

Measured on Slnmap's own solution (6 projects, 81 documents): **+13.9% nodes, +32.1% edges**
(nearly all of the edge growth is the new `References` edges — `Calls` didn't move), analyze time
unchanged within run-to-run noise, `slnmap.db` size +27%. Well under the 2.5x-edge-growth /
30%-time-regression thresholds that would have called for gating this behind a flag — ships
as-is, no opt-out needed.

**Upgrade note:** the new edges are produced at analysis time — **re-run `slnmap analyze`** (no
flag needed) to pick them up on an existing `slnmap.db`; no schema migration, delete, or
`--full` re-analysis is required.

**Known remaining limits** (not fixed by this release):
- A **fully-qualified** type reference without a `using` shortcut (e.g. a parameter typed
  `Fixture.Lib.SomeType` with no `using Fixture.Lib;` in scope) is still invisible — it's caught
  by the same syntax-exclusion rule that (correctly) ignores `using` directives themselves. Rare
  in idiomatic C#, tracked as a follow-up.
- **Events** (`public event EventHandler Foo;`) are still not modeled — a distinct symbol kind
  from fields (`IEventSymbol`, not `IFieldSymbol`) despite the similar declaration syntax.
- NuGet package references and `Directory.Packages.props` version pins remain out of the graph
  by design — a deliberate model boundary, not a gap.

## 0.4.0

### Added

- **`slnmap viz`** — exports the code graph as a single self-contained, interactive HTML file. No
  server, no CDN dependency, works fully offline; the graph rendering library is embedded in the
  output. Starts collapsed to one node per project; click a project, namespace, or class to drill
  into it and see its members and dependency edges. `--output <path>` sets the file to write;
  `--project <name>` exports just one project's subtree, with the rest shown as collapsed stubs.

### Fixed

- `viz` now shows a clean error ("The graph file is corrupted or not a Slnmap database...") instead
  of a raw stack trace when pointed at a stale or corrupted `slnmap.db`.
- Clicking a node to expand or collapse it now re-frames the camera on the result, matching the
  search and reset-view behavior — previously the expanded subgraph could end up outside the
  visible viewport.
- Node labels are now truncated for display (full name still shown in the detail panel). A small
  number of compiler-synthesized names (e.g. anonymous types from EF Core migration lambdas) could
  run into the thousands of characters, which defeated the viewer's auto-fit framing.

## 0.3.0

### Added

Six new read-only MCP tools (5 → 11), all counts-first, capped, with honest truncation notes:

- `find_implementations` — the concrete types that implement an interface or derive from a base type,
  and the members that implement/override an interface or virtual member; transitive, grouped by
  project with file:line.
- `get_type_hierarchy` — the base and/or derived type tree as an indented view (`up`, `down`, or
  `both`), depth-capped.
- `find_tests_for_symbol` — test members that transitively exercise a symbol, grouped by test
  project (detection is name-based — the output says so).
- `get_project_dependencies` — the project-to-project reference map with cross-project reference
  counts and a hotspot line for the most-coupled pair.
- `find_circular_dependencies` — dependency cycles between projects or namespaces, worst offenders
  first; an acyclic solution reports `0 cycles` explicitly.
- `get_symbol_source` — the actual source of a symbol, read from its stored span with configurable
  context lines.

### Fixed

- **Silent incremental edge loss when a solution contains multiple top-level-statements projects.**
  Every project's top-level entry point (and its `Program` class, synthesized or an explicit
  `partial class Program`) rendered the same fully qualified name. FQNs are node identity, so those
  nodes merged across projects — and on every incremental re-analysis the planner attributed one
  project's edges to another project's file and silently dropped them. The corruption was permanent
  and compounding: each changed-file run lost more edges (on eShopOnWeb, 12 edges per run). Entry
  points and top-level `Program` classes are now qualified per assembly
  (e.g. `Web.<top-level-statements-entry-point>`), which also makes reference graph counts slightly
  larger and more accurate (eShopOnWeb: 1,107 → 1,111 nodes, 2,168 → 2,175 edges — previously merged
  nodes and collapsed edges are now correctly distinct). **If you ran incremental analyses with an
  earlier version on a solution with more than one top-level-statements project, your graph may be
  missing edges: delete `slnmap.db` and re-run `slnmap analyze` once to rebuild it fully.**

## 0.2.1
- Open-sourced under the MIT license. No functional changes.

## 0.2.0
- Colored CLI output with a plain-ANSI theme; expanded test suite. MCP output unchanged.

## 0.1.9
- Output honesty and diagnostics: clearer capped-result wording, and cleaner error messages (full
  stack trace only under `--verbose`).

## 0.1.8
- First-run polish: fixed a crash when `global.json` pins an uninstalled SDK; added a `serve` readiness
  line; clearer empty `get_dependencies` output.

## 0.1.7
- Corrected the support contact address in the README.

## 0.1.5
- README repositioning and package metadata polish. No source changes.

## 0.1.3
- Warnings UX: quiet by default with a single `Warnings: N (M unique)` summary line and grouped detail
  under `--verbose`; non-language projects are skipped silently.

## 0.1.2
- Renamed the project to **Slnmap**.

## 0.1.1
- Fixes from v0.1.0 field testing.
