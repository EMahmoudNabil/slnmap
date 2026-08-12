using Slnmap.Core.Graph;
using Slnmap.Core.Storage;
using Slnmap.Mcp;
using Slnmap.Storage;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// Guards that every tool's output stays bounded on a large graph (caps engaged, no raw dumps),
/// and that a query reflects a database swap performed between calls (serve resilience).
/// </summary>
public sealed class McpToolBudgetTests : IDisposable
{
    private const int Callers = 2000;
    private const int Endpoints = 300;
    private const int OutputBudgetChars = 12_000;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "slnmap-mcp-budget", Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_directory, "big.db");

    private static readonly IReadOnlyDictionary<string, string> AnalyzedMeta =
        new Dictionary<string, string>(StringComparer.Ordinal) { [MetaKeys.LastAnalyzed] = "test" };

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    // A hub method with thousands of callers, so every tool has far more to report than its cap.
    private static CodeGraph BuildLargeGraph()
    {
        var graph = new CodeGraph();
        var project = SymbolNode.Create(NodeKind.Project, "Big", "Big");
        var ns = SymbolNode.Create(NodeKind.Namespace, "Big", "Big");
        var hubType = SymbolNode.Create(NodeKind.Class, "Hub", "Big.Hub", "Hub.cs", new SourceSpan(0, 10));
        var hub = SymbolNode.Create(NodeKind.Method, "Process", "Big.Hub.Process()", "Hub.cs", new SourceSpan(11, 20));
        graph.AddNode(project);
        graph.AddNode(ns);
        graph.AddNode(hubType);
        graph.AddNode(hub);
        graph.AddEdge(new RelationshipEdge(project.Id, ns.Id, RelationshipKind.Contains));
        graph.AddEdge(new RelationshipEdge(ns.Id, hubType.Id, RelationshipKind.Contains));
        graph.AddEdge(new RelationshipEdge(hubType.Id, hub.Id, RelationshipKind.Contains));

        for (int i = 0; i < Callers; i++)
        {
            var callerType = SymbolNode.Create(NodeKind.Class, $"C{i}", $"Big.C{i}", "C.cs", new SourceSpan(i, i + 1));
            var caller = SymbolNode.Create(NodeKind.Method, $"M{i}", $"Big.C{i}.M{i}()", "C.cs", new SourceSpan(i, i + 1));
            graph.AddNode(callerType);
            graph.AddNode(caller);
            graph.AddEdge(new RelationshipEdge(ns.Id, callerType.Id, RelationshipKind.Contains));
            graph.AddEdge(new RelationshipEdge(callerType.Id, caller.Id, RelationshipKind.Contains));
            graph.AddEdge(new RelationshipEdge(caller.Id, hub.Id, RelationshipKind.Calls));
            graph.AddEdge(new RelationshipEdge(caller.Id, hubType.Id, RelationshipKind.References));
        }

        // Far more endpoints than either endpoint tool's cap, all handled by the hub.
        for (int i = 0; i < Endpoints; i++)
        {
            var endpoint = SymbolNode.Create(NodeKind.Endpoint, $"/api/big/{i}", $"GET /api/big/{i}", "Hub.cs", new SourceSpan(i, i + 1));
            graph.AddNode(endpoint);
            graph.AddEdge(new RelationshipEdge(hubType.Id, endpoint.Id, RelationshipKind.Contains));
            graph.AddEdge(new RelationshipEdge(endpoint.Id, hub.Id, RelationshipKind.HandledBy));
        }

        return graph;
    }

    [Fact]
    public async Task EveryToolOutput_StaysWithinBudget_OnLargeGraph()
    {
        await using var store = new SqliteGraphStore(DbPath);
        await store.SaveAsync(BuildLargeGraph(), [], AnalyzedMeta);
        var queries = new SlnmapQueries(store);

        var outputs = new[]
        {
            await queries.FindSymbolAsync("M", null),
            await queries.FindSymbolAsync("M", "Method"),
            await queries.GetDependenciesAsync("Big.Hub.Process()", "incoming", 1),
            await queries.ImpactAnalysisAsync("Big.Hub.Process()"),
            await queries.GetArchitectureOverviewAsync(),
            await queries.FindUsagesAsync("Big.Hub.Process()"),
            await queries.ListEndpointsAsync(null, null),
            await queries.FindEndpointAsync("/api/big/{id}", null),
        };

        foreach (string output in outputs)
        {
            Assert.True(output.Length <= OutputBudgetChars, $"output exceeded budget: {output.Length} chars");
        }

        // The caps must actually be engaging (otherwise "within budget" is vacuous).
        string impact = await queries.ImpactAnalysisAsync("Big.Hub.Process()");
        Assert.Contains("more", impact, StringComparison.Ordinal);          // > 100 dependents → truncated list
        string dependencies = await queries.GetDependenciesAsync("Big.Hub.Process()", "incoming", 1);
        Assert.Contains("truncated", dependencies, StringComparison.Ordinal); // > 50 direct edges → truncated
        string find = await queries.FindSymbolAsync("M", null);
        Assert.Contains("20+ matches", find, StringComparison.Ordinal);       // cap engaged, disclosed honestly
        Assert.Contains("showing first 20", find, StringComparison.Ordinal);
        string endpoints = await queries.ListEndpointsAsync(null, null);
        Assert.Contains("more", endpoints, StringComparison.Ordinal);          // > 100 endpoints → truncated
        string routes = await queries.FindEndpointAsync("/api/big/{id}", null);
        Assert.Contains("more", routes, StringComparison.Ordinal);             // a hole matches all 300 → capped
    }

    [Fact]
    public async Task Query_ReflectsDatabaseSwapBetweenCalls()
    {
        // A single long-lived store instance simulates the running server holding no db handle.
        await using var server = new SqliteGraphStore(DbPath);

        var v1 = new CodeGraph();
        v1.AddNode(SymbolNode.Create(NodeKind.Class, "Alpha", "Big.Alpha", "a.cs", new SourceSpan(0, 1)));
        await server.SaveAsync(v1, [], AnalyzedMeta);

        var queries = new SlnmapQueries(server);
        string before = await queries.FindSymbolAsync("Alpha", null);
        Assert.Contains("Big.Alpha", before, StringComparison.Ordinal);

        // A separate writer (the analyzer process) atomically swaps in a new graph.
        await using (var writer = new SqliteGraphStore(DbPath))
        {
            var v2 = new CodeGraph();
            v2.AddNode(SymbolNode.Create(NodeKind.Class, "Beta", "Big.Beta", "b.cs", new SourceSpan(0, 1)));
            await writer.SaveAsync(v2, [], AnalyzedMeta);
        }

        // The next query on the original instance sees the swapped-in graph without error.
        string after = await queries.FindSymbolAsync("Beta", null);
        Assert.Contains("Big.Beta", after, StringComparison.Ordinal);
        string gone = await queries.FindSymbolAsync("Alpha", null);
        Assert.Contains("No symbols match", gone, StringComparison.Ordinal);
    }
}
