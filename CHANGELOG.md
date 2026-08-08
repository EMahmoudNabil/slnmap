# Changelog

All notable changes to Slnmap are documented here. Versions follow [SemVer](https://semver.org).

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
