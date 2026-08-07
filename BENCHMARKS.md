# Benchmarks

Slnmap's performance numbers are measured on a public reference solution so anyone can reproduce them.
This document is **methodology first** — the commands below are exactly what produced the results
table, and the target repository is pinned to a commit so a re-run is comparable. Honest numbers, no
massaging: where a result is unflattering (see *incremental re-analysis*), it is reported as measured.

## Target

- **Repository:** [`dotnet-architecture/eShopOnWeb`](https://github.com/dotnet-architecture/eShopOnWeb)
- **Commit:** `4da8212117e87d808d4bbc7da6286fd2147ce606`
- **Solution:** `eShopOnWeb.sln` — **10 projects**, target framework **net8.0**
- **SDK:** .NET 9 (`9.0.314`) — the only SDK installed; the net8.0 target is built by the .NET 9 SDK
- **Slnmap:** v0.2.0 (`0.2.0+6d85dcc`) for the original numbers below; re-measured on **v0.5.0**
  where noted (type-reference edges + Field nodes — see CHANGELOG) — the analyzer's per-document
  work is otherwise unchanged since v0.2.x

## Machine

- **CPU:** Intel Core i5-5200U (2 cores / 4 threads @ 2.20 GHz) — a modest laptop part; a faster CPU will beat these numbers
- **RAM:** 12 GB
- **OS:** Windows 10 (build 19044)

## Reproduce

```console
# 1. Clone the target at the pinned commit
git clone https://github.com/dotnet-architecture/eShopOnWeb.git
cd eShopOnWeb
git checkout 4da8212117e87d808d4bbc7da6286fd2147ce606

# 2. Cold analyze (no existing slnmap.db) — builds the graph from scratch
rm -f slnmap.db && slnmap analyze eShopOnWeb.sln

# 3. Incremental re-analyze — touch one file, then run analyze again against the same db
#    (re-walks only the changed file and its dependents, but still reloads the workspace)
slnmap analyze eShopOnWeb.sln

# 4. Impact query timing — served over MCP; measure a high-fan-in symbol such as IBasketService
slnmap serve --db slnmap.db
```

The `analyze` command prints `Elapsed` on its final line; that is the wall-clock figure used for the
cold and incremental rows below. Each timing row is the **median of 3 runs**.

## Results

| Metric | v0.2.0 / v0.3.0 (original) | **v0.5.0 (re-measured)** |
|---|---|---|
| Graph size (10 projects, 279 files) | 1,107 / 2,168 edges; v0.3.0: 1,111 / 2,175 (entry-point fix) | **1,311 nodes / 2,922 edges** — +18.0% nodes / +34.3% edges vs. v0.3.0 (type-reference edges + Field nodes; see CHANGELOG) |
| Cold analyze (282 docs) | 25.6 s median (23.5 / 25.6 / 30.9) | **27.0 s** median (25.7 / 27.0 / 27.2) — flat within the ±5 s run-to-run noise already noted below |
| Re-analyze after a one-file change (6 docs re-walked) | 27.1 s median (24.7 / 27.1 / 29.4) | **22.7 s** median (22.3 / 22.7 / 25.9) — flat/within noise |
| Re-analyze, nothing changed | ~18.8 s (single observation) | **23.0 s** (single observation) — within noise |
| `impact_analysis` on `IBasketService` | 18 dependents, ~270 ms warm median (231 ms best) | **29 dependents**, ~239–289 ms across 3 warm runs |

The dependent-count jump on `impact_analysis` (18 → 29) is the type-reference-edges fix doing its
job, not measurement noise: the 11 newly-visible dependents include the DI registration call site
(`Web.Configuration.ConfigureCoreServices.AddCoreServices`, reached transitively from
`Web.<top-level-statements-entry-point>` at depth 2) and three mock-object test fields
(`[Field] ...BasketServiceTests.*._mockLogger`) that a pre-v0.5.0 graph reported as having zero
relationship to `IBasketService` at all.

For query-latency context, on the same warm `slnmap serve` process `find_symbol` returns in **~14 ms**
warm and `get_architecture_overview` in ~52 ms (v0.5.0) — comparable to the original ~17 ms / ~56 ms,
confirming the ~240–290 ms in `impact_analysis` is genuine graph traversal (a depth-5 recursive CTE per
seed plus interface-implementation expansion, now over a larger edge set), not transport overhead.
`impact_analysis` on `Microsoft.eShopWeb.ApplicationCore.Interfaces.IBasketService` returns 29 dependent
symbols (including 5 interface implementations/overrides) across ApplicationCore, Web, UnitTests, and
IntegrationTests as of v0.5.0 (18, across ApplicationCore, Web, and UnitTests, on earlier versions) —
the landing-page terminal demo predates this fix and shows the older, smaller count.

## Notes on incremental re-analysis

Incremental re-analysis re-walks only the changed file and its dependents — the graph work is small
(**5 of 282** documents re-analyzed above). Yet its median wall time is **not lower than cold**, and the
run-to-run variance (±~5 s) is larger than the gap. The dominant cost is a full `MSBuildWorkspace` load
+ per-project compilation (~20 s here) paid on **every** run, because the CLI is run-and-exit and does
not keep a warm workspace. Document-level skipping saves little against that fixed floor.

A resident **`watch` mode** that keeps the workspace and compilations warm (targeting sub-second
re-analysis) is the top roadmap item — sub-second incremental is a property of a warm process, not of a
cold CLI invocation. Until then, treat re-analysis as "about as fast as a cold run."
