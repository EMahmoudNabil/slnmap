using Slnmap.Core.Graph;
using Slnmap.Core.Storage;
using Slnmap.Mcp;
using Slnmap.Storage;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// v0.14.1 (Gate 8 QA #9): a link the linker inferred (prefix-stripped, or token-transformer
/// tolerant) is persisted as a plain CallsEndpoint edge, so impact_analysis and find_endpoint
/// showed it exactly like a literal one. Both now carry the same marker list_frontend_callsites
/// shows, recomputed live; a literal link stays unmarked.
/// </summary>
public sealed class InferredLinkMarkerTests : IAsyncLifetime
{
    private const string HandlerFqn = "Demo.ArticlesController.Get(string)";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "slnmap-inferred-links", Guid.NewGuid().ToString("N"));
    private SqliteGraphStore _store = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        var graph = new CodeGraph();
        var controller = SymbolNode.Create(NodeKind.Class, "ArticlesController", "Demo.ArticlesController", "Articles.cs", new SourceSpan(0, 1), "Public");
        var handler = SymbolNode.Create(NodeKind.Method, "Get", HandlerFqn, "Articles.cs", new SourceSpan(2, 3), "Public");
        // As extracted: no /api prefix (a convention adds it at runtime).
        var endpoint = SymbolNode.Create(NodeKind.Endpoint, "/articles/{slug}", "GET /articles/{slug}", "Articles.cs", new SourceSpan(4, 5));
        // The frontend's absolute base bakes /api in: links only via the stripped fallback.
        var inferred = SymbolNode.Create(NodeKind.FrontendCallSite, "https://conduit.example.org/api/articles/{*}", "GET src/agent.ts:10:5", "agent.ts", new SourceSpan(0, 1));
        // Same backend path as authored: a literal link.
        var literal = SymbolNode.Create(NodeKind.FrontendCallSite, "https://conduit.example.org/articles/{*}", "GET src/legacy.ts:3:7", "legacy.ts", new SourceSpan(0, 1));
        foreach (var node in new[] { controller, handler, endpoint, inferred, literal })
        {
            graph.AddNode(node);
        }

        graph.AddEdge(new RelationshipEdge(controller.Id, handler.Id, RelationshipKind.Contains));
        graph.AddEdge(new RelationshipEdge(endpoint.Id, handler.Id, RelationshipKind.HandledBy));
        var results = CrossStackLinker.Link(graph);
        Assert.Contains(results, r => r.CallSite.Id == inferred.Id && r.ViaPrefixStripped);
        Assert.Contains(results, r => r.CallSite.Id == literal.Id && !r.ViaPrefixStripped && r.Endpoints.Count == 1);
        foreach (var edge in CrossStackLinker.ToEdges(results))
        {
            graph.AddEdge(edge);
        }

        _store = new SqliteGraphStore(Path.Combine(_directory, "graph.db"));
        await _store.SaveAsync(graph, [], new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MetaKeys.LastAnalyzed] = "test",
            [MetaKeys.LinkerLastRun] = DateTimeOffset.UtcNow.ToString("O"),
        });
    }

    public async Task DisposeAsync()
    {
        await _store.DisposeAsync();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public async Task ImpactAnalysis_MarksTheInferredLink_NotTheLiteralOne()
    {
        string result = await new SlnmapQueries(_store).ImpactAnalysisAsync(HandlerFqn);

        Assert.Contains("[FrontendCallSite] GET src/agent.ts:10:5 @depth 2 via prefix-stripped path", result, StringComparison.Ordinal);
        Assert.Contains("[FrontendCallSite] GET src/legacy.ts:3:7 @depth 2", result, StringComparison.Ordinal);
        Assert.DoesNotContain("legacy.ts:3:7 @depth 2 via", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindEndpoint_MarksTheInferredCaller_NotTheLiteralOne()
    {
        string result = await new SlnmapQueries(_store).FindEndpointAsync("/articles/how-to", "GET");

        Assert.Contains("Called from the frontend by: GET src/agent.ts:10:5 via prefix-stripped path, GET src/legacy.ts:3:7", result, StringComparison.Ordinal);
    }
}
