using Slnmap.Analysis;
using Slnmap.Core.Graph;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// v1.1 controller-endpoint behaviors beyond the gap tests: the cross-extractor duplicate
/// collapse, the conventional-controller detection note, and the cross-file inherited-route
/// incremental dependency (the v0.7.0 proviso-2 mechanism, now riding the Inherits edge).
/// </summary>
public sealed class ControllerEndpointTests : IClassFixture<AnalyzedFixtureSolution>, IDisposable
{
    private readonly AnalyzedFixtureSolution _fixture;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "slnmap-tests", Guid.NewGuid().ToString("N"));

    public ControllerEndpointTests(AnalyzedFixtureSolution fixture) => _fixture = fixture;

    private CodeGraph Graph => _fixture.Graph;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the OS temp cleaner will get it eventually.
        }
    }

    [Fact]
    public void DuplicateRoute_AcrossExtractors_CollapsesToOneNodeWithBothHandlers()
    {
        // Program.cs registers app.MapGet("/api/Status", VendorEndpoints.Ping) — the same
        // verb+template as StatusController's bare [HttpGet]. Identity is "VERB template", so
        // both extractors converge on ONE node whose HandledBy edges point at BOTH handlers
        // (they would collide at runtime too; the superposition is the honest answer).
        var endpoint = GraphAssert.Node(Graph, NodeKind.Endpoint, "GET /api/Status");
        var handlers = Graph.OutgoingEdges(endpoint.Id, RelationshipKind.HandledBy)
            .Select(e => Graph.TryGetNode(e.TargetId, out var n) ? n.Fqn : e.TargetId)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
            ["Fixture.Web.StatusController.GetStatus()", "Fixture.Web.VendorEndpoints.Ping()"],
            handlers);
    }

    [Fact]
    public void MinimalApiCounts_AreUntouchedByTheControllerExtractor()
    {
        // The two extractors are additive: every v0.7.0 fixture endpoint still exists.
        GraphAssert.Node(Graph, NodeKind.Endpoint, "GET /api/vendors");
        GraphAssert.Node(Graph, NodeKind.Endpoint, "GET /api/Reminders");
        GraphAssert.Node(Graph, NodeKind.Endpoint, "GET /health");
    }

    [Fact]
    public void ControllerEndpoints_CarryTheActionDeclarationFileAndSpan()
    {
        // Proviso 1 (immortal-node hazard): every controller endpoint anchors to its action's
        // declaration file, so incremental eviction owns it.
        var controllerEndpoints = Graph.Nodes
            .Where(n => n.Kind == NodeKind.Endpoint && n.FilePath is { } f
                && (f.EndsWith("StatusController.cs", StringComparison.Ordinal)
                    || f.EndsWith("LegacyControllers.cs", StringComparison.Ordinal)
                    || f.EndsWith("InheritedRouteController.cs", StringComparison.Ordinal)))
            .ToList();
        Assert.True(controllerEndpoints.Count >= 7, $"expected the fixture's controller endpoints, found {controllerEndpoints.Count}");
        Assert.All(controllerEndpoints, e => Assert.NotNull(e.Span));
    }

    [Fact]
    public async Task TouchingTheBaseControllerFile_RederivesTheInheritedRoute_ColdEqualsIncremental()
    {
        // InheritedRouteController.cs declares no [Route]; its composed template depends on
        // LegacyControllers.cs (the abstract base's attribute) — a different file. The planner
        // must re-analyze the derived file when the base file changes, via the existing
        // derived→base Inherits edge: zero special-casing, verified not assumed.
        CopyDirectory(TestPaths.FixtureSolutionDirectory, _root);
        DotNet.Run("restore FixtureSolution.sln", _root);
        string solutionPath = Path.Combine(_root, "FixtureSolution.sln");
        var analyzer = new RoslynSolutionAnalyzer();

        var cold = await analyzer.AnalyzeAsync(solutionPath);
        GraphAssert.Node(cold.Graph, NodeKind.Endpoint, "GET /api/InheritedRoute/own");

        File.AppendAllText(Path.Combine(_root, "FixtureWeb", "LegacyControllers.cs"), "\n");
        var incremental = await analyzer.AnalyzeAsync(solutionPath, cold);

        Assert.True(
            incremental.Stats.DocumentsAnalyzed < cold.Stats.DocumentsAnalyzed,
            $"expected an incremental re-walk, got the full {incremental.Stats.DocumentsAnalyzed} documents");

        var coldNodeIds = cold.Graph.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var incrementalNodeIds = incremental.Graph.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        Assert.True(coldNodeIds.SetEquals(incrementalNodeIds), "node sets diverged between cold and incremental");
        Assert.True(
            cold.Graph.Edges.ToHashSet().SetEquals(incremental.Graph.Edges),
            "edge sets diverged between cold and incremental");
        GraphAssert.Node(incremental.Graph, NodeKind.Endpoint, "GET /api/InheritedRoute/own");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            if (relative.StartsWith("bin", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("obj", StringComparison.OrdinalIgnoreCase)
                || relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
