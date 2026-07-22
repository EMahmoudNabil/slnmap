# Slnmap Backlog

Tracked items deferred for later, so they are not lost.

## First-run review — deferred items

**Status:** open.

1. **File paths in `impact_analysis` output (🟡).** Dependents list `[Kind] FQN @depth N` with no file
   path, so a dev can't jump straight to each site. The nodes already carry `FilePath`.
2. **🟢 polish:**
   - Tool-routing hints — `find_usages` vs `get_dependencies(incoming)` vs `impact_analysis` overlap;
     add a "use this vs that" line to each description.
   - Summary wording — `Documents: N analyzed` vs `Files: M hashed` discrepancy is unexplained; the
     bare `Warnings: N` undersells that these are usually NuGet CVE advisories.
   - `Loading` phase shows an incrementing counter with unknown total; add a "large solutions can take
     a while" hint (ties into watch mode).
   - `doctor --help` prints the absolute CWD as the arg default (path noise in help text).
   - Version string mismatch: MCP `serverInfo.version` = `0.1.x.0` vs `--version` = `0.1.x+<sha>`.
   - CLI em-dash renders as `-`/`?` on Windows consoles (MCP wire is fine); use ASCII `-` in CLI strings.

## v0.1.1 — HIGH priority (v0.1.0 field-test findings)

### 1. Unhandled crash when global.json pins an uninstalled SDK

**Status:** open — HIGH. User-facing crash.

**Symptom:** analyzing a solution whose `global.json` pins a .NET SDK that is not installed throws an
unhandled `RemoteInvocationException` with a full stack trace. (Root cause is `hostfxr_resolve_sdk2` /
"A compatible .NET SDK was not found" from the out-of-process MSBuild build host during
`OpenSolutionAsync`/`OpenProjectAsync`.)

**Fix:** wrap the build-host / `OpenSolutionAsync` failure path in `RoslynSolutionAnalyzer`. Detect the
SDK-resolution failure (`hostfxr_resolve_sdk2`, "compatible .NET SDK was not found") and replace the
stack trace with a two-line actionable message: the **required** SDK (from `global.json`) vs the
**installed** SDK(s), pointing the user at their `global.json` and at `slnmap doctor`. Exit non-zero,
no stack trace.

### 2. `slnmap doctor` misses global.json SDK pin

**Status:** open — HIGH. Pairs with #1.

**Symptom:** `doctor` currently reports all-ok in a directory where `analyze` will crash with the SDK
mismatch above — it checks that *some* SDK is installed, not that the *pinned* one is.

**Fix:** `doctor` must look for a `global.json` in the target directory and upward, read any `sdk.version`
pin, and verify that SDK (respecting `rollForward`) is installed — failing with the required-vs-installed
message when it isn't. Note: `doctor` today has no analysis-target argument (it checks the graph dir);
it likely needs an optional target path (or to scan from cwd) so it can find the relevant `global.json`.

## Package size

The packed tool is ~27.69 MB; investigate trim options (e.g. trimming unused Roslyn/MSBuild assets,
`PackAsToolShimRuntimeIdentifiers`, dependency pruning). **Open.**

## Warm analysis / incremental performance

**Status:** open — architecture-level, not blocking.

**Measured problem:** incremental re-analysis (~27 s median on the eShopOnWeb reference solution —
see BENCHMARKS.md) is far above the `< 5 s` target, and no faster than a cold run.

**Root cause:** incremental analysis already does the right *graph* work (only the changed document is
re-analyzed), but each run is a cold CLI invocation that pays the full, unavoidable cost of
`MSBuildWorkspace.OpenSolutionAsync` + `project.GetCompilationAsync` across the whole solution.
Compilations are intentionally not retained (run-and-exit, no daemon). The `< 5 s` target is a
property of a **warm/resident process**, not of a cold invocation — so no amount of graph-side
optimization reaches it while the workspace is reloaded every run.

**Options (need design + approval):**
1. `slnmap watch` daemon — keep the workspace + compilations warm; incremental runs skip the reload.
   This is where `< 5 s` becomes reachable.
2. Load only the changed project + its dependents' compilations instead of the whole solution.
3. Persist per-project compilation inputs to skip design-time builds of unchanged projects.

The performance section of the README states this honestly: re-analysis is fast on graph work but
currently pays a full workspace load; a watch mode is planned.

## Precompute project attribution at analyze time

**Status:** open — optimization + consolidation, not blocking.

Project membership is currently attributed by file path (a symbol's file lives under its project's
directory). `impact_analysis` does this over its (small) result set; `get_architecture_overview` does
it over the whole graph, which requires **one transient full-graph load per call**.

**Proposal:** add a `project` column to the `nodes` table, populated during `SaveAsync` using the same
file-path logic (the project set and their csproj directories are already in the graph). Benefits:
- Removes `get_architecture_overview`'s transient full-graph load entirely (query `project` directly).
- Centralizes attribution in one place (analyze time), instead of recomputing it per tool call.
- Makes per-project grouping a plain SQL `GROUP BY`.

At current scale the transient `LoadGraphAsync` costs ~60–100 ms warm, so this is a
cleanliness/consolidation win more than a hot-path fix; it matters more for very large solutions where
a full load is expensive.

## Store line numbers at analyze time

**Status:** open — accuracy, not blocking.

`find_usages` reports the containing member's line best-effort: the graph stores char spans
(`span_start`/`span_end`), not line numbers, so the tool reads the source file at query time and counts
newlines (falls back to `?` if the file moved/changed since analysis).

**Proposal:** compute the line number at analyze time (Roslyn's `SourceText.Lines` gives it directly
from the char span) and store it alongside the span. `find_usages` then reports exact lines with no
file read and no drift risk.

## `--verbose` switch for `slnmap serve`

**Status:** open — polish, not blocking.

`serve` now defaults to Warning-level logging (stdout stays clean JSON-RPC; stderr is quiet in normal
use). Add an opt-in `--verbose` flag that lowers the host log level to Information so operators can see
per-request lifecycle logs when debugging an MCP integration.

## Opt-in update check (deferred — only if users ask)

**Status:** deferred by design, not planned.

Slnmap makes no network calls — that is a hard guarantee in the Privacy section, not an accident.
A built-in "new version available" check (even a lightweight query of the NuGet index) would soften
that guarantee, so it stays out unless real users request it. If it ever lands, it must be explicit
opt-in behind a manual command (e.g. `slnmap doctor --check-update`), never ambient, and the Privacy
section must document it in the same release. Until then: `dotnet tool update -g Slnmap` and
watching GitHub Releases are the supported channels (see README → Updating).
