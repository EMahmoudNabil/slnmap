using System.ComponentModel;
using ModelContextProtocol.Server;
using Slnmap.Core.Storage;

namespace Slnmap.Mcp;

/// <summary>
/// The MCP tool surface over the Slnmap code graph. Each tool reads from the graph store per call
/// (no graph is held in memory); inputs are fully qualified names, not internal ids. Logic lives in
/// <see cref="SlnmapQueries"/>; these methods only bind MCP arguments and the injected store.
/// </summary>
[McpServerToolType]
public static class SlnmapTools
{
    /// <summary>
    /// Liveness check kept callable in-process (e.g. from tests), but intentionally NOT decorated as an
    /// MCP tool — the advertised surface is exactly the thirteen documented, read-only tools.
    /// </summary>
    public static string Ping() => "pong";

    [McpServerTool(Name = "find_symbol")]
    [Description(
        "Search the code graph for symbols by name or fully qualified name (FQN). Returns up to 20 " +
        "matches, each with its kind, FQN and file. An FQN alone does not reveal whether a member is " +
        "an explicit interface implementation (those collapse to the Type.Member(params) form), so " +
        "when several symbols share an FQN all are returned — disambiguate by kind. " +
        "Example: {\"query\": \"IShape\", \"kind\": \"Interface\"}")]
    public static Task<string> FindSymbol(
        IGraphStore store,
        [Description("Text matched against symbol name and FQN (case-insensitive substring).")] string query,
        [Description("Optional kind filter: Namespace, Class, Interface, Struct, Record, Enum, EnumMember, Delegate, Method, Constructor, Property, Field, Event, Endpoint.")] string? kind = null,
        CancellationToken cancellationToken = default)
        => new SlnmapQueries(store).FindSymbolAsync(query, kind, cancellationToken);

    [McpServerTool(Name = "get_dependencies")]
    [Description(
        "List a symbol's dependencies grouped by relationship kind (Calls, Implements, Inherits, " +
        "References), with optional transitive depth. Pass the symbol's fully qualified name. " +
        "direction 'outgoing' = what this symbol depends on; 'incoming' = what depends on this symbol. " +
        "depth is 1-3. Results are capped at 50 with a truncation note. " +
        "Example: {\"fqn\": \"Fixture.Lib.Circle.Area()\", \"direction\": \"incoming\", \"depth\": 1}")]
    public static Task<string> GetDependencies(
        IGraphStore store,
        [Description("Fully qualified name of the symbol.")] string fqn,
        [Description("'outgoing' (what it depends on) or 'incoming' (what depends on it).")] string direction = "outgoing",
        [Description("Transitive depth, 1-3.")] int depth = 1,
        CancellationToken cancellationToken = default)
        => new SlnmapQueries(store).GetDependenciesAsync(fqn, direction, depth, cancellationToken);

    [McpServerTool(Name = "impact_analysis")]
    [Description(
        "Answer \"what breaks if I change this symbol?\". Returns every symbol that transitively " +
        "depends on the given FQN (depth 5): counts first (totals by project and by kind), then the " +
        "dependent list nearest-first. When the target is an interface or an interface member, its " +
        "concrete implementations/overrides and their dependents are included — that is the point of " +
        "the tool. Pass a fully qualified name. Example: {\"fqn\": \"Fixture.Lib.IShape.Area()\"}")]
    public static Task<string> ImpactAnalysis(
        IGraphStore store,
        [Description("Fully qualified name of the symbol under consideration.")] string fqn,
        CancellationToken cancellationToken = default)
        => new SlnmapQueries(store).ImpactAnalysisAsync(fqn, cancellationToken);

    [McpServerTool(Name = "get_architecture_overview")]
    [Description(
        "High-level map of the analyzed solution: projects, project-to-project dependencies derived " +
        "from symbol references, node/edge counts by kind, and top-level namespaces. " +
        "Takes no parameters. Example: {}")]
    public static Task<string> GetArchitectureOverview(
        IGraphStore store,
        CancellationToken cancellationToken = default)
        => new SlnmapQueries(store).GetArchitectureOverviewAsync(cancellationToken);

    [McpServerTool(Name = "find_usages")]
    [Description(
        "Find where a symbol is used (called or referenced). Returns the containing member, file and " +
        "line for each usage, up to 50. Pass a fully qualified name. An FQN does not reveal " +
        "explicit-interface-ness; if several symbols share the FQN, usages of all are reported. " +
        "Example: {\"fqn\": \"Fixture.Lib.IShape.Area()\"}")]
    public static Task<string> FindUsages(
        IGraphStore store,
        [Description("Fully qualified name of the symbol.")] string fqn,
        CancellationToken cancellationToken = default)
        => new SlnmapQueries(store).FindUsagesAsync(fqn, cancellationToken);

    [McpServerTool(Name = "find_implementations")]
    [Description(
        "List the concrete types that implement an interface / derive from a base type, or the members " +
        "that implement or override an interface member or virtual/abstract member. Transitive over " +
        "Implements + Inherits, grouped by project with file:line. Pass a fully qualified name. Use " +
        "this for \"who are the concrete types/overrides\"; for the full blast radius of a change use " +
        "impact_analysis; for the inheritance shape as a tree use get_type_hierarchy. " +
        "Example: {\"fqn\": \"Fixture.Lib.IShape\"}")]
    public static Task<string> FindImplementations(
        IGraphStore store,
        [Description("Fully qualified name of an interface, base type, or interface/virtual member.")] string fqn,
        CancellationToken cancellationToken = default)
        => new SlnmapQueries(store).FindImplementationsAsync(fqn, cancellationToken);

    [McpServerTool(Name = "get_type_hierarchy")]
    [Description(
        "Show the base and/or derived type tree for a type as an indented text tree, transitive over " +
        "Inherits + Implements, depth-capped. direction: 'up' = base types/interfaces, 'down' = " +
        "derived types/implementers, 'both'. Pass a fully qualified name. For just the concrete " +
        "implementer list use find_implementations; for a mixed one-hop dependency list use " +
        "get_dependencies. Example: {\"fqn\": \"Fixture.Lib.ShapeBase\", \"direction\": \"down\"}")]
    public static Task<string> GetTypeHierarchy(
        IGraphStore store,
        [Description("Fully qualified name of the type.")] string fqn,
        [Description("'up' (base types), 'down' (derived types), or 'both'.")] string direction = "both",
        [Description("Maximum transitive depth, 1-10.")] int depth = 5,
        CancellationToken cancellationToken = default)
        => new SlnmapQueries(store).GetTypeHierarchyAsync(fqn, direction, depth, cancellationToken);

    [McpServerTool(Name = "find_tests_for_symbol")]
    [Description(
        "Find the test members that transitively exercise a symbol: incoming callers (depth 5) filtered " +
        "to test projects, grouped by project with file:line. Heuristic: a project is a test project " +
        "when its name contains \"Test\" (there is no package data to detect a framework directly) — the " +
        "output states this. Pass a fully qualified name. For all usages (not just tests) use " +
        "find_usages; for the full set of dependents use impact_analysis. " +
        "Example: {\"fqn\": \"Fixture.Lib.Circle.Area()\"}")]
    public static Task<string> FindTestsForSymbol(
        IGraphStore store,
        [Description("Fully qualified name of the symbol under test.")] string fqn,
        CancellationToken cancellationToken = default)
        => new SlnmapQueries(store).FindTestsForSymbolAsync(fqn, cancellationToken);

    [McpServerTool(Name = "get_project_dependencies")]
    [Description(
        "Project-to-project reference map with cross-project reference counts, ending in a hotspot line " +
        "(the most-coupled pair). Pass a project name to focus on it, or 'all' (default) for the whole " +
        "map. This is the focused, per-project drill-down; get_architecture_overview includes the same " +
        "map plus node/edge/namespace census. To check for cycles use find_circular_dependencies. " +
        "Example: {\"project\": \"FixtureLib\"}")]
    public static Task<string> GetProjectDependencies(
        IGraphStore store,
        [Description("A project name, or 'all' for the full map.")] string project = "all",
        CancellationToken cancellationToken = default)
        => new SlnmapQueries(store).GetProjectDependenciesAsync(project, cancellationToken);

    [McpServerTool(Name = "find_circular_dependencies")]
    [Description(
        "Detect dependency cycles between projects (scope='project') or namespaces (scope='namespace') " +
        "over the derived container graph. Each cycle is reported as a path chain, worst offenders " +
        "(most crossing references) first. An acyclic solution reports '0 cycles' — a real answer. To " +
        "see the underlying edges use get_project_dependencies. Example: {\"scope\": \"namespace\"}")]
    public static Task<string> FindCircularDependencies(
        IGraphStore store,
        [Description("'project' or 'namespace'.")] string scope = "project",
        CancellationToken cancellationToken = default)
        => new SlnmapQueries(store).FindCircularDependenciesAsync(scope, cancellationToken);

    [McpServerTool(Name = "list_endpoints")]
    [Description(
        "List the HTTP endpoints extracted from ASP.NET Core Minimal API registrations AND " +
        "attribute-routed controllers ([Route]/[HttpGet]...), grouped by project: " +
        "\"VERB /route/template → handler — file:line\". Endpoint identity is the composed route " +
        "template as authored ({param} names and :int constraints preserved). Optionally filter by " +
        "verb and/or route prefix. Registrations that could not be resolved statically are counted " +
        "in a trailing note, never guessed; conventionally-routed controllers (no route attributes) " +
        "are disclosed but not modeled. Example: {\"verb\": \"GET\", \"prefix\": \"/api/vendors\"}")]
    public static Task<string> ListEndpoints(
        IGraphStore store,
        [Description("Optional HTTP verb filter: GET, POST, PUT, DELETE, or PATCH.")] string? verb = null,
        [Description("Optional route prefix filter, e.g. '/api/vendors' (case-insensitive, compared against the composed template).")] string? prefix = null,
        CancellationToken cancellationToken = default)
        => new SlnmapQueries(store).ListEndpointsAsync(verb, prefix, cancellationToken);

    [McpServerTool(Name = "find_endpoint")]
    [Description(
        "Find the endpoint(s) matching a route — an exact template (\"/api/vendors/{id}\") or a " +
        "concrete path (\"/api/vendors/42\"), matched with the framework's own semantics: " +
        "case-insensitive, {param}/{param:constraint} holes bind concrete segments. Returns each " +
        "match with its handler method and registration file:line; a miss suggests near matches. " +
        "Use impact_analysis on the handler to see an endpoint's blast radius. " +
        "Example: {\"route\": \"/api/vendors/42\"}")]
    public static Task<string> FindEndpoint(
        IGraphStore store,
        [Description("The route to find: a template or a concrete request path.")] string route,
        [Description("Optional HTTP verb filter: GET, POST, PUT, DELETE, or PATCH.")] string? verb = null,
        CancellationToken cancellationToken = default)
        => new SlnmapQueries(store).FindEndpointAsync(route, verb, cancellationToken);

    [McpServerTool(Name = "get_symbol_source")]
    [Description(
        "Return the real source code of a symbol, read from its file at the stored declaration span and " +
        "expanded by context_lines on each side (capped at 120 lines). Pass a fully qualified name. " +
        "find_symbol locates a symbol (kind + path, no body); this prints the body — the natural next " +
        "call after find_symbol. Example: {\"fqn\": \"Fixture.Lib.Circle.Area()\", \"context_lines\": 3}")]
    public static Task<string> GetSymbolSource(
        IGraphStore store,
        [Description("Fully qualified name of the symbol.")] string fqn,
        [Description("Context lines to show on each side of the declaration, 0-20.")] int context_lines = 5,
        CancellationToken cancellationToken = default)
        => new SlnmapQueries(store).GetSymbolSourceAsync(fqn, context_lines, cancellationToken);
}
