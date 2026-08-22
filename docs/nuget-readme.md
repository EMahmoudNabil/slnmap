# Slnmap

*Slnmap (sln-map) — a semantic map of your .sln for AI coding agents.*

**Open source under the MIT license.** Source, issues, and full docs:
[github.com/EMahmoudNabil/slnmap](https://github.com/EMahmoudNabil/slnmap)

**Your AI agent can't refactor .NET code it can't see.** Ask an agent *"what breaks if I change this
interface?"* and it guesses from the files in its context — missing callers in other projects and
files it never opened. Slnmap gives the agent a precise, compiler-accurate map of your whole solution,
so it answers correctly: every caller, every implementation, across every project. It runs locally and
serves the map to your agent or editor over [MCP](https://modelcontextprotocol.io).

## Quickstart

**1. Install** (requires the [.NET SDK](https://dotnet.microsoft.com/download) 9.0+):

```console
dotnet tool install --global Slnmap
```

**2. Analyze** your solution (builds `slnmap.db` in the current folder):

```console
slnmap analyze path/to/YourSolution.sln
```

**3. Connect** your MCP client. For Claude Code, add this to `.mcp.json`, using an **absolute** path to
the `slnmap.db` you just built:

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

## The tools

The server exposes thirteen read-only tools — `find_symbol`, `get_dependencies`, `impact_analysis`,
`get_architecture_overview`, `find_usages`, `find_implementations`, `get_type_hierarchy`,
`find_tests_for_symbol`, `get_project_dependencies`, `find_circular_dependencies`,
`get_symbol_source`, `list_endpoints`, and `find_endpoint` (full descriptions in the
[README](https://github.com/EMahmoudNabil/slnmap#what-you-can-ask)).
For an interface, `impact_analysis` follows both the interface's callers **and** its concrete
implementations/overrides — across projects, in files nobody has open. HTTP endpoints are
first-class graph nodes — from ASP.NET Core Minimal APIs (v0.7.0) and attribute-routed controllers
(v0.8.0): ask "which endpoint serves `/api/vendors/42`?" or "what breaks if I change this handler?"
and get the actual route back.

There's also `slnmap viz`: exports the graph as a single self-contained, interactive HTML file —
no server, no CDN, works offline. And `slnmap watch` (v0.10.0): analyze once, keep the workspace
warm, and re-analyze on every file save — a one-file change lands in the database in about a
second on a 10-project solution, and a `slnmap serve` running beside it keeps answering from the
same file, so your agent's answers stay fresh while you type.

And `slnmap analyze-ts` (v0.11.0): adds TypeScript/React frontend HTTP call sites to the same
graph — resolved route templates and an honestly-labeled unresolved bucket, six named reasons,
never a guess. Requires Node 18+; the extractor itself is fetched via `npx` automatically.

## Privacy

**100% local — and now you can verify it.** No telemetry, no cloud service; `analyze`/`watch`/
`serve`/`viz`/`status`/`doctor` make no network calls at all. One documented exception:
`analyze-ts` fetches its extractor from the public npm registry via `npx` (skip it to stay fully
offline). Now that the CLI and MCP server are open source, the claim is auditable.

## License & support

Slnmap is open source under the [MIT license](https://github.com/EMahmoudNabil/slnmap/blob/main/LICENSE).
The CLI and MCP server are MIT-licensed and will stay that way. Future hosted or team-oriented features
may be commercial.

For questions or to report an issue, open a
[GitHub issue](https://github.com/EMahmoudNabil/slnmap/issues) or contact **hello@slnmap.dev**.
