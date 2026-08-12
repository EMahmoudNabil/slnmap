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

If this is the first .NET global tool ever installed on the machine, the tools directory
(`~/.dotnet/tools`) may not be on your `PATH` yet — open a new terminal before running `slnmap`.

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

Or register it from the command line:

```console
claude mcp add slnmap -- slnmap serve --db C:/path/to/your/project/slnmap.db
```

> **Restart your MCP client after registering.** Fully quit and relaunch it — starting a new
> conversation or reconnecting mid-session is not enough; a running session will not see the new
> tools until the client process restarts.

That's it. Ask your agent an architecture question and it will call Slnmap.
(Run `slnmap doctor` first if anything looks off — see [Troubleshooting](#troubleshooting).)

## What you can ask

The server exposes thirteen read-only tools. Give them fully qualified names; results are capped and
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
| `list_endpoints` | "List every HTTP endpoint, or just the `POST`s under `/api/basket`." |
| `find_endpoint` | "Which endpoint serves `/api/basket/42/items`, and which method handles it?" |

For an interface (or interface member), `impact_analysis` follows both the interface's callers **and**
its concrete implementations/overrides — so the answer includes code that only touches the interface,
across projects, in files nobody has open.

HTTP endpoints are first-class graph nodes — from **ASP.NET Core Minimal APIs** (v0.7.0) *and*
**attribute-routed controllers** (v0.8.0): each `MapGet`/`MapPost`/… registration and each
`[Route]`/`[HttpGet("…")]` action appears as `VERB /route/template` linked to its handler method,
so `impact_analysis` and `find_usages` on a handler surface the actual routes that break. Route
templates are resolved statically — `MapGroup` prefixes, `const` patterns, the common
CleanArchitecture registration conventions, class-level `[Route]` (including inherited ones and
`[controller]`/`[action]` tokens), and controller base classes reached through packages
(Ardalis.ApiEndpoints works out of the box). Anything that can't be resolved statically is counted
and reported, never guessed — and controllers routed *conventionally* (`MapControllerRoute`, no
route attributes) are detected and disclosed rather than silently absent.

## MCP tools reference

The exact parameter names, for clients that call the tools directly. Most tools take `fqn` — the
symbol's fully qualified name — not `symbol`, `name`, or `type`; a wrong parameter name fails the
call.

| Tool | Parameters | Description |
|---|---|---|
| `find_symbol` | `query` *(required)*, `kind` *(optional)* | Search symbols by name or FQN, case-insensitive substring; returns kind, FQN, and file for up to 20 matches. |
| `get_architecture_overview` | *(none)* | Projects, project-to-project dependencies, node/edge counts by kind, and top-level namespaces. |
| `get_symbol_source` | `fqn` *(required)*, `context_lines` *(optional, 0–20, default 5)* | Print a symbol's source, read from its file at the declaration span. |
| `find_usages` | `fqn` *(required)* | Where a symbol is called or referenced — containing member, file, and line, up to 50. |
| `get_dependencies` | `fqn` *(required)*, `direction` *(optional: `outgoing`/`incoming`, default `outgoing`)*, `depth` *(optional, 1–3, default 1)* | A symbol's dependencies grouped by relationship kind (Calls, Implements, Inherits, References). |
| `find_implementations` | `fqn` *(required)* | Concrete types implementing an interface / deriving from a base, or members overriding a virtual/interface member. |
| `get_type_hierarchy` | `fqn` *(required)*, `direction` *(optional: `up`/`down`/`both`, default `both`)*, `depth` *(optional, 1–10, default 5)* | Base and/or derived type tree as an indented text tree. |
| `get_project_dependencies` | `project` *(optional, default `all`)* | Project-to-project reference map with cross-project reference counts and a hotspot line. |
| `impact_analysis` | `fqn` *(required)* | Every symbol that transitively depends on the given one (depth 5) — counts first, then nearest-first. |
| `find_tests_for_symbol` | `fqn` *(required)* | Test members that transitively exercise a symbol, grouped by project with file:line. |
| `find_circular_dependencies` | `scope` *(optional: `project`/`namespace`, default `project`)* | Dependency cycles reported as path chains, worst offenders first. |
| `list_endpoints` | `verb` *(optional: `GET`/`POST`/`PUT`/`DELETE`/`PATCH`)*, `prefix` *(optional route prefix, e.g. `/api/vendors`)* | HTTP endpoints (Minimal APIs + attribute-routed controllers) grouped by project: `VERB /route → handler — file:line`; unresolved registrations and conventionally-routed controllers disclosed in trailing notes. |
| `find_endpoint` | `route` *(required: a template or a concrete path)*, `verb` *(optional)* | Endpoints matching a route — case-insensitive, `{param}` holes bind concrete segments; a miss suggests near matches. |

## CLI

```console
slnmap analyze <solution>   # build or update the code graph (incremental on re-run)
slnmap serve                # serve the graph to MCP clients over stdio
slnmap status               # show node/edge counts and when it was last analyzed
slnmap viz                  # export the graph as a self-contained interactive HTML file
slnmap doctor               # check the environment can run Slnmap
```

These five verbs are the whole CLI. Symbol, usage, and impact querying is MCP-only — there is no
`find`/`usages`/`impact` command; connect an MCP client to `slnmap serve` to query the graph.

`--db <path>` selects the database file (default `slnmap.db`). `-v`/`--verbose` prints per-document
progress on its own line per update — useful in an interactive terminal, but it floods piped or
redirected output (logs, CI), so omit it there.

## Visualizing the graph

```console
slnmap viz --output graph.html      # export the whole graph
slnmap viz --project YourProject    # export one project's subtree; others render as collapsed stubs
```

Opens as a single HTML file — double-click it, no server or internet connection required. It starts
collapsed to one node per project; click a project, namespace, or class to drill into it. Like the
rest of Slnmap, the export is self-contained: the graph library is embedded in the file, so nothing is
fetched from a CDN and it works fully offline.

## Updating

.NET tools do not update themselves, and Slnmap makes no network calls — so it will never nag you
about (or check for) new versions. To update:

```console
dotnet tool update -g Slnmap
```

To hear about releases, watch the GitHub repo (**Watch → Custom → Releases**); each release ships
with notes in the [changelog](CHANGELOG.md). After a major-version update, re-run
`slnmap analyze` if the tool asks for it — release notes call out when a graph rebuild is needed.

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
| Graph size | 1,332 nodes / 3,014 edges |
| Cold analyze (10 projects) | ~20.9 s (median of 3) |
| Re-analyze after a one-file change | ~18.7 s (median of 3 — see note) |
| `impact_analysis` on `IBasketService` (29 dependents, last measured v0.5.0) | ~240–290 ms (end-to-end MCP round-trip) |

Numbers are for v0.6.0: fully-qualified type references (no `using` shortcut) now produce edges,
and events are modeled as graph nodes (see the [changelog](CHANGELOG.md)) — the fully-qualified-
reference fix accounts for nearly all of this release's edge growth (89 of 92 new edges) versus
v0.5.0 (1,311 / 2,922 edges). Timings are flat within normal run-to-run noise; the analyzer's
per-document work is otherwise unchanged. Full before/after detail, including the v0.5.0 and
v0.3.0 baselines, is in [BENCHMARKS.md](BENCHMARKS.md).

To estimate your own solution's cold analyze time, scale by size rather than anchoring on any single
number above: field measurements on real-world solutions (antivirus real-time protection on, no
exclusions) come out at roughly **55–60 seconds per 1,000 analyzed documents**. Treat it as
approximate — hardware and antivirus overhead move it either way.

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
- **The first analysis of a large solution takes a while.** Cold analysis compiles every project once;
  as a rough guide from field measurements, expect around **55–60 seconds per 1,000 analyzed
  documents** (approximate). Re-runs are faster on graph work but still reload the workspace — see
  the performance note above. This is normal; the graph is cached in `slnmap.db` between runs.
- **Windows Defender (or other antivirus) slows analysis.** Real-time protection scans every file
  Roslyn reads while compiling your solution. Adding an exclusion for your repository folder can
  speed analysis up, but changing exclusions requires local admin rights — corporate users without
  them may need an IT ticket. No exclusion is required for correctness: analysis completes fine
  without one, and the ~55–60 s per 1,000 documents guide above was measured with real-time
  protection on and no exclusions in place.
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
