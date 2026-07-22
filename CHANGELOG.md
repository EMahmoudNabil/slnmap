# Changelog

All notable changes to Slnmap are documented here. Versions follow [SemVer](https://semver.org).

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
