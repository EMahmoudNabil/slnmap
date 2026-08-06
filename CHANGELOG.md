# Changelog

All notable changes to Slnmap are documented here. Versions follow [SemVer](https://semver.org).

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
