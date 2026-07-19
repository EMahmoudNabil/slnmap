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
    /// MCP tool — the advertised surface is exactly the five documented, read-only tools.
    /// </summary>
    public static string Ping() => "pong";

    [McpServerTool(Name = "find_symbol")]
    [Description(
        "Search the code graph for symbols by name or fully qualified name (FQN). Returns up to 20 " +
        "matches, each with its kind, FQN and file. An FQN alone does not reveal whether a member is " +
        "an explicit interface implementation (those collapse to the Type.Member(params) form), so " +
        "when several symbols share an FQN all are returned — disambiguate by kind.")]
    public static Task<string> FindSymbol(
        IGraphStore store,
        [Description("Text matched against symbol name and FQN (case-insensitive substring).")] string query,
        [Description("Optional kind filter: Namespace, Class, Interface, Struct, Record, Enum, Delegate, Method, Constructor, Property, Field, Event.")] string? kind = null,
        CancellationToken cancellationToken = default)
        => new SlnmapQueries(store).FindSymbolAsync(query, kind, cancellationToken);

    [McpServerTool(Name = "get_dependencies")]
    [Description(
        "List a symbol's dependencies grouped by relationship kind (Calls, Implements, Inherits, " +
        "References), with optional transitive depth. Pass the symbol's fully qualified name. " +
        "direction 'outgoing' = what this symbol depends on; 'incoming' = what depends on this symbol. " +
        "depth is 1-3. Results are capped at 50 with a truncation note.")]
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
        "the tool. Pass a fully qualified name.")]
    public static Task<string> ImpactAnalysis(
        IGraphStore store,
        [Description("Fully qualified name of the symbol under consideration.")] string fqn,
        CancellationToken cancellationToken = default)
        => new SlnmapQueries(store).ImpactAnalysisAsync(fqn, cancellationToken);

    [McpServerTool(Name = "get_architecture_overview")]
    [Description(
        "High-level map of the analyzed solution: projects, project-to-project dependencies derived " +
        "from symbol references, node/edge counts by kind, and top-level namespaces.")]
    public static Task<string> GetArchitectureOverview(
        IGraphStore store,
        CancellationToken cancellationToken = default)
        => new SlnmapQueries(store).GetArchitectureOverviewAsync(cancellationToken);

    [McpServerTool(Name = "find_usages")]
    [Description(
        "Find where a symbol is used (called or referenced). Returns the containing member, file and " +
        "line for each usage, up to 50. Pass a fully qualified name. An FQN does not reveal " +
        "explicit-interface-ness; if several symbols share the FQN, usages of all are reported.")]
    public static Task<string> FindUsages(
        IGraphStore store,
        [Description("Fully qualified name of the symbol.")] string fqn,
        CancellationToken cancellationToken = default)
        => new SlnmapQueries(store).FindUsagesAsync(fqn, cancellationToken);
}
