# Slnmap

*Slnmap (sln-map) — a semantic map of your .sln for AI coding agents.*

**Open source under the [MIT license](LICENSE).**

[![CI](https://github.com/EMahmoudNabil/slnmap/actions/workflows/ci.yml/badge.svg)](https://github.com/EMahmoudNabil/slnmap/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/Slnmap.svg)](https://www.nuget.org/packages/Slnmap)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

**Your AI agent can't refactor .NET code it can't see.** Ask an agent *"what breaks if I change this
interface?"* and it guesses from the files in its context — missing callers in other projects and
files it never opened. Slnmap gives the agent a precise, compiler-accurate map of your whole solution,
so it answers correctly: every caller, every implementation, across every project. Fewer broken
changes, no hallucinated dependencies. It runs locally and serves the map to your agent or editor over
[MCP](https://modelcontextprotocol.io).

## Quickstart (3 steps)

**1. Install** the global tool (requires the [.NET SDK](https://dotnet.microsoft.com/download) 9.0+):

```console
dotnet tool install --global Slnmap
```

**2. Analyze** your solution (or a single `.csproj`) — this builds `slnmap.db` in the current folder:

```console
slnmap analyze path/to/YourSolution.sln
```

**3. Connect** your MCP client. For Claude Code, add this to `.mcp.json` in your project. Use an
**absolute** path to the `slnmap.db` you just built — an MCP client's working directory is usually not
your project folder, so a relative path can silently resolve to the wrong (or a missing) file:

```json
{
  "mcpServers": {
    "slnmap": {
      "command": "slnmap",
      "args": ["serve", "--db", "C:/path/to/your/project/slnmap.db"]
    }
  }
}
```

On macOS/Linux, use a POSIX absolute path instead, e.g. `/home/you/project/slnmap.db`.

That's it. Ask your agent an architecture question and it will call Slnmap.
(Run `slnmap doctor` first if anything looks off — see [Troubleshooting](#troubleshooting).)

## What you can ask

The server exposes eleven read-only tools. Give them fully qualified names; results are capped and
counts-first. (A note the tools also carry: an FQN does not reveal whether a member is an explicit
interface implementation.)

| Tool | Example question |
|---|---|
| `find_symbol` | "Find the `IBasketService` interface." |
| `get_dependencies` | "What does `CartController.Index` depend on?" |
| `impact_analysis` | "What breaks if I change `IBasketService`?" |
| `get_architecture_overview` | "Show me the projects and how they depend on each other." |
| `find_usages` | "Where is `BasketService.GetBasket` used?" |
| `find_implementations` | "Who implements `IBasketService` / overrides this virtual member?" |
| `get_type_hierarchy` | "Show the base and derived type tree for `BaseEntity`." |
| `find_tests_for_symbol` | "Which tests exercise `BasketService.AddItemToBasket`?" |
| `get_project_dependencies` | "How do the projects reference each other, and where is the coupling worst?" |
| `find_circular_dependencies` | "Are there dependency cycles between projects or namespaces?" |
| `get_symbol_source` | "Show me the actual source of `IBasketService`." |

For an interface (or interface member), `impact_analysis` follows both the interface's callers **and**
its concrete implementations/overrides — so the answer includes code that only touches the interface,
across projects, in files nobody has open.

## CLI

```console
slnmap analyze <solution>   # build or update the code graph (incremental on re-run)
slnmap serve                # serve the graph to MCP clients over stdio
slnmap status               # show node/edge counts and when it was last analyzed
slnmap doctor               # check the environment can run Slnmap
```

`--db <path>` selects the database file (default `slnmap.db`).

## Build from source

Slnmap is a standard .NET solution — clone, build, and test it with the SDK:

```console
git clone https://github.com/EMahmoudNabil/slnmap.git
cd slnmap
dotnet build -c Release
dotnet test  -c Release
```

To run the CLI without installing the global tool:

```console
dotnet run --project src/Slnmap.Cli -- analyze path/to/YourSolution.sln
```

## Compatibility

Analyzes C# solutions targeting .NET 8 and .NET 9 (earlier targets are untested — feedback welcome);
runs on Windows, macOS, and Linux; works with any MCP client (tested with Claude Code).

## Privacy

**100% local — and now you can verify it.** Slnmap runs on your machine, reads your source with
Roslyn, and writes a single local SQLite file. The MCP server reads only that local file. There is no
telemetry, no network calls, and no cloud service — analysis works fully offline. Now that the CLI and
MCP server are open source, that claim is auditable: read the code, or watch the process — nothing
leaves your machine.

## Performance

Measured on [`eShopOnWeb`](https://github.com/dotnet-architecture/eShopOnWeb) (10 projects, `net8.0`),
.NET 9 SDK, on a 2-core laptop. Each timing is the median of 3 runs; full methodology, machine spec,
and pinned commit are in [BENCHMARKS.md](BENCHMARKS.md).

| Metric | Result |
|---|---|
| Graph size | 1,111 nodes / 2,175 edges |
| Cold analyze (10 projects) | ~25.6 s (median of 3) |
| Re-analyze after a one-file change | ~27.1 s (median of 3 — see note) |
| `impact_analysis` on `IBasketService` (18 dependents) | ~270 ms (end-to-end MCP round-trip) |

Graph counts are for v0.3.0: the entry-point fix (see the [changelog](CHANGELOG.md)) makes each
project's top-level `Program` distinct, so counts grew slightly versus v0.2.x (1,107 / 2,168).
Timings were measured on v0.2.1; the analyzer work per document is unchanged in v0.3.0.

**Incremental re-analysis.** Re-analysis re-walks only the changed file and its dependents, but each
run still pays a full workspace load of the solution — because the CLI is run-and-exit and does not
keep a warm workspace. In practice that means re-analysis is currently **about as fast as a cold run,
not faster**. A resident **`watch` mode** that keeps the workspace warm (targeting sub-second
re-analysis) is the top item on the roadmap.

## Troubleshooting

Run **`slnmap doctor`** first — it checks the three things that actually block analysis and prints a
fix for each:

```console
$ slnmap doctor
[ok] .NET SDK: 1 SDK(s) installed; newest: 9.0.314 …
[ok] MSBuild workspace: Roslyn MSBuild workspace initialized …
[ok] Graph directory: Writable: /path/to/cwd
```

- **"No .NET SDKs are installed" / MSBuild fails to load projects.** Slnmap analyzes via
  `MSBuildWorkspace`, which runs design-time builds using your installed .NET SDK. Install the SDK
  (not just the runtime) from <https://dotnet.microsoft.com/download>. On **Windows**, if projects
  still fail to load, install the **Visual Studio Build Tools** (or Visual Studio) so MSBuild and the
  targeting packs resolve.
- **Analysis reports warnings but finishes.** That is expected and safe: a project that can't be loaded
  (e.g. a missing SDK or targeting pack) is reported as a warning and skipped — Slnmap indexes everything
  that *did* load rather than failing the whole run (a *partial load*). By default these are condensed
  into a single `Warnings: N (M unique)` summary line; run `slnmap analyze --verbose` for the full,
  grouped detail.
- **The first analysis of a large solution takes a while.** Cold analysis compiles every project once.
  Re-runs are faster on graph work but still reload the workspace — see the performance note above.
  This is normal; the graph is cached in `slnmap.db` between runs.
- **`slnmap: command not found` after install.** Ensure the .NET global tools directory
  (`~/.dotnet/tools`) is on your `PATH`, then open a new shell.

## How it works

Slnmap uses the Roslyn compiler platform to build a precise semantic graph of your solution — every
type and member, and the relationships between them (calls, implementations, inheritance, references).
The graph is stored locally and served to your AI agent or editor over MCP. Updates are incremental
and crash-safe: an interrupted run never corrupts your existing graph.

## License & support

Slnmap is open source under the [MIT license](LICENSE).

The CLI and MCP server are MIT-licensed and will stay that way. Future hosted or team-oriented
features may be commercial.

For questions or to report an issue, open a [GitHub issue](https://github.com/EMahmoudNabil/slnmap/issues)
or contact **hello@slnmap.dev**. Contributions are welcome — see [CONTRIBUTING.md](CONTRIBUTING.md).
