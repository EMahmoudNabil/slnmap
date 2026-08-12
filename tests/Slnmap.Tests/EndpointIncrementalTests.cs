using Slnmap.Analysis;
using Slnmap.Core.Graph;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// The endpoint-nodes design's incremental provisos, verified rather than assumed
/// (reports/endpoint-nodes-investigation.md §2.5): the #6 source-scoped eviction must handle
/// Endpoint nodes with ZERO special-casing — because every endpoint carries its registration
/// file (proviso 1) and the cross-file prefix dependency rides the existing Calls edge into the
/// in-source forwarder (proviso 2). Same 3-file-chain shape as IncrementalEvictionScopeTests.
/// </summary>
public sealed class EndpointIncrementalTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "slnmap-tests", Guid.NewGuid().ToString("N"));

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
    public async Task TouchingTheRegistrationFile_ColdEqualsIncremental_EndpointsIntact()
    {
        var (analyzer, solutionPath) = Prepare();
        var cold = await analyzer.AnalyzeAsync(solutionPath);
        AssertConventionEndpointsPresent(cold.Graph);

        // Whitespace-only touch of the group file: the endpoint nodes and their HandledBy edges
        // are evicted with the file and must regenerate identically (deterministic ids).
        File.AppendAllText(Path.Combine(_root, "FixtureWeb", "ReminderEndpoints.cs"), "\n");
        var incremental = await analyzer.AnalyzeAsync(solutionPath, cold);

        Assert.True(
            incremental.Stats.DocumentsAnalyzed < cold.Stats.DocumentsAnalyzed,
            $"expected an incremental re-walk, got the full {incremental.Stats.DocumentsAnalyzed} documents");
        AssertGraphsIdentical(cold.Graph, incremental.Graph);
        AssertConventionEndpointsPresent(incremental.Graph);
    }

    [Fact]
    public async Task TouchingTheForwarderInfrastructureFile_EndpointTemplatesRederive()
    {
        // Proviso 2: the composed template depends on ConventionInfrastructure.cs (a different
        // file from the node's own). The group's Map method holds a Calls edge into the in-source
        // forwarders, so editing the infrastructure file must re-analyze the registration file
        // and re-derive every endpoint FQN — this rides the existing planner rule, no special case.
        var (analyzer, solutionPath) = Prepare();
        var cold = await analyzer.AnalyzeAsync(solutionPath);

        File.AppendAllText(Path.Combine(_root, "FixtureWeb", "ConventionInfrastructure.cs"), "\n");
        var incremental = await analyzer.AnalyzeAsync(solutionPath, cold);

        Assert.True(
            incremental.Stats.DocumentsAnalyzed < cold.Stats.DocumentsAnalyzed,
            $"expected an incremental re-walk, got the full {incremental.Stats.DocumentsAnalyzed} documents");
        AssertGraphsIdentical(cold.Graph, incremental.Graph);
        AssertConventionEndpointsPresent(incremental.Graph);
    }

    [Fact]
    public async Task TouchingTheHandlerFile_HandledByEdgeSurvivesTheEvictionBoundary()
    {
        // GET /health is registered in Program.cs; its handler lives in VendorEndpoints.cs.
        // Editing the handler's file evicts the handler node — the planner must re-walk the
        // registration file (the HandledBy edge targets the affected handler), and the edge must
        // come back. This is the v0.6.0 Event-extension shape, for the first new edge kind.
        var (analyzer, solutionPath) = Prepare();
        var cold = await analyzer.AnalyzeAsync(solutionPath);
        AssertHealthHandledByPing(cold.Graph);

        File.AppendAllText(Path.Combine(_root, "FixtureWeb", "VendorEndpoints.cs"), "\n");
        var incremental = await analyzer.AnalyzeAsync(solutionPath, cold);

        Assert.True(
            incremental.Stats.DocumentsAnalyzed < cold.Stats.DocumentsAnalyzed,
            $"expected an incremental re-walk, got the full {incremental.Stats.DocumentsAnalyzed} documents");
        AssertGraphsIdentical(cold.Graph, incremental.Graph);
        AssertHealthHandledByPing(incremental.Graph);
    }

    [Fact]
    public async Task RenamingARoute_EvictsTheOldEndpointNode()
    {
        // A deleted route must not linger (the immortal-node hazard proviso 1 exists to prevent):
        // rename the nested group's segment and the old endpoint id must disappear.
        var (analyzer, solutionPath) = Prepare();
        var cold = await analyzer.AnalyzeAsync(solutionPath);
        var oldEndpoint = GraphAssert.Node(cold.Graph, NodeKind.Endpoint, "DELETE /api/Reminders/archive/{id:int}");

        string groupPath = Path.Combine(_root, "FixtureWeb", "ReminderEndpoints.cs");
        // LF-normalize before matching — the standing line-ending lesson (core.autocrlf checkouts).
        string original = File.ReadAllText(groupPath).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("\"/archive\"", original, StringComparison.Ordinal);
        File.WriteAllText(groupPath, original.Replace("\"/archive\"", "\"/vault\"", StringComparison.Ordinal));

        var incremental = await analyzer.AnalyzeAsync(solutionPath, cold);

        Assert.DoesNotContain(incremental.Graph.Nodes, n => n.Id == oldEndpoint.Id);
        GraphAssert.Node(incremental.Graph, NodeKind.Endpoint, "DELETE /api/Reminders/vault/{id:int}");

        // No dangling edges either: the old node's HandledBy/Contains edges went with it.
        Assert.DoesNotContain(
            incremental.Graph.Edges,
            e => e.SourceId == oldEndpoint.Id || e.TargetId == oldEndpoint.Id);
    }

    private (RoslynSolutionAnalyzer Analyzer, string SolutionPath) Prepare()
    {
        CopyDirectory(TestPaths.FixtureSolutionDirectory, _root);
        DotNet.Run("restore FixtureSolution.sln", _root);
        return (new RoslynSolutionAnalyzer(), Path.Combine(_root, "FixtureSolution.sln"));
    }

    private static void AssertConventionEndpointsPresent(CodeGraph graph)
    {
        var endpoint = GraphAssert.Node(graph, NodeKind.Endpoint, "GET /api/Reminders");
        GraphAssert.Edge(
            graph,
            endpoint,
            GraphAssert.Node(graph, NodeKind.Method, "Fixture.Web.Reminders.GetAll()"),
            RelationshipKind.HandledBy);
        GraphAssert.Node(graph, NodeKind.Endpoint, "DELETE /api/Reminders/archive/{id:int}");
    }

    private static void AssertHealthHandledByPing(CodeGraph graph)
    {
        GraphAssert.Edge(
            graph,
            GraphAssert.Node(graph, NodeKind.Endpoint, "GET /health"),
            GraphAssert.Node(graph, NodeKind.Method, "Fixture.Web.VendorEndpoints.Ping()"),
            RelationshipKind.HandledBy);
    }

    private static void AssertGraphsIdentical(CodeGraph expected, CodeGraph actual)
    {
        var expectedNodeIds = expected.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var actualNodeIds = actual.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        Assert.True(expectedNodeIds.SetEquals(actualNodeIds), "node sets diverged between cold and incremental");
        Assert.True(expected.Edges.ToHashSet().SetEquals(actual.Edges), "edge sets diverged between cold and incremental");
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
