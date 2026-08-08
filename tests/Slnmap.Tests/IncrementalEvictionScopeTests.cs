using Slnmap.Analysis;
using Slnmap.Core.Graph;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// Regression tests for issue #6: incremental re-analysis silently dropped edges owned by a
/// one-hop dependent's own (unrelated) declarations. Exercises the 3-file chain
/// E (<c>EvictionChainConsumer.cs</c>) -> D (<c>EvictionChainImplementor.cs</c>) ->
/// F (<c>EvictionChain.cs</c>) added to the fixture solution specifically for this bug — see the
/// comment atop <c>EvictionChain.cs</c> for why the existing fixture couldn't reproduce it.
/// </summary>
public sealed class IncrementalEvictionScopeTests : IDisposable
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
    public async Task TouchingF_PreservesEdgeCount_AndTheEToD_UnrelatedEdge()
    {
        CopyDirectory(TestPaths.FixtureSolutionDirectory, _root);
        DotNet.Run("restore FixtureSolution.sln", _root);
        string solutionPath = Path.Combine(_root, "FixtureSolution.sln");
        var analyzer = new RoslynSolutionAnalyzer();

        var cold = await analyzer.AnalyzeAsync(solutionPath);

        // The E -> D edge under test: EvictionChainConsumer.UseUnrelatedWork() calling
        // EvictionChainImplementor.UnrelatedWork() — an edge into D's own unrelated member,
        // never anything declared in F (EvictionChain.cs).
        AssertEToDEdgePresent(cold.Graph);

        // v0.6.0 additions (reports/v060-regression-plan.md §1b): a #4-shaped fully-qualified
        // E -> D edge, and D's own Event-kind node (UnrelatedEvent) + its Contains edge — both
        // must also survive the same eviction-and-rewalk cycle as the original Calls edge above.
        AssertFullyQualifiedEToDEdgePresent(cold.Graph);
        AssertUnrelatedEventNodeAndContainsEdgePresent(cold.Graph);

        // Whitespace-only touch of F: appends a trailing blank line, zero semantic change —
        // mirrors the eShopOnWeb repro in reports/issue-6-investigation.md.
        string fPath = Path.Combine(_root, "FixtureLib", "EvictionChain.cs");
        File.AppendAllText(fPath, "\n");

        var incremental = await analyzer.AnalyzeAsync(solutionPath, cold);

        // D (EvictionChainImplementor.cs) must have been re-walked as a one-hop dependent of F.
        Assert.True(
            incremental.Stats.DocumentsAnalyzed >= 2,
            $"expected at least F and D re-walked, got {incremental.Stats.DocumentsAnalyzed}");
        Assert.True(
            incremental.Stats.DocumentsAnalyzed < cold.Stats.DocumentsAnalyzed,
            $"expected fewer than the full {cold.Stats.DocumentsAnalyzed} documents, got {incremental.Stats.DocumentsAnalyzed}");

        // The core regression assertion: no edges lost across the incremental run.
        Assert.Equal(cold.Graph.EdgeCount, incremental.Graph.EdgeCount);

        // And specifically, all three E→D-chain elements survived: the original Calls edge, the
        // #4-shaped fully-qualified References edge, and #5's Event-kind node + Contains edge.
        AssertEToDEdgePresent(incremental.Graph);
        AssertFullyQualifiedEToDEdgePresent(incremental.Graph);
        AssertUnrelatedEventNodeAndContainsEdgePresent(incremental.Graph);
    }

    [Fact]
    public async Task RemovingAMethodFromD_DropsOnlyItsOwnEdges_AndLeavesNoDanglingEdge()
    {
        CopyDirectory(TestPaths.FixtureSolutionDirectory, _root);
        DotNet.Run("restore FixtureSolution.sln", _root);
        string solutionPath = Path.Combine(_root, "FixtureSolution.sln");
        var analyzer = new RoslynSolutionAnalyzer();

        var cold = await analyzer.AnalyzeAsync(solutionPath);
        var implementor = GraphAssert.Node(cold.Graph, NodeKind.Class, "Fixture.Lib.EvictionChainImplementor");
        var unrelatedWork = GraphAssert.Node(cold.Graph, NodeKind.Method, "Fixture.Lib.EvictionChainImplementor.UnrelatedWork()");
        Assert.NotEmpty(cold.Graph.IncomingEdges(unrelatedWork.Id, RelationshipKind.Calls));

        // A real content change to D itself: delete UnrelatedWork() entirely (not a rename, not
        // whitespace). EvictionChainConsumer.cs (E) still calls it in source, so E's re-walked
        // call site simply fails to resolve to anything — exactly the "genuinely deleted symbol"
        // case the post-merge dangling-edge prune exists for.
        string dPath = Path.Combine(_root, "FixtureLib", "EvictionChainImplementor.cs");
        const string method = "\n    // Unrelated to IEvictionContract. EvictionChainConsumer's edge into this member is the one\n    // that must survive an eviction-and-rewalk of this file triggered by touching EvictionChain.cs.\n    public int UnrelatedWork() => 42;\n";

        // Normalize line endings before matching: git's Windows checkout (core.autocrlf) rewrites
        // this fixture to CRLF, but `method` above is written with LF only — same class of issue
        // fixed for VizExporterTests in d078208. Match/replace on LF-normalized content instead of
        // assuming the checkout's line ending.
        string original = File.ReadAllText(dPath).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(method, original);
        File.WriteAllText(dPath, original.Replace(method, "\n"));

        var incremental = await analyzer.AnalyzeAsync(solutionPath, cold);

        // Generic dangling-edge invariant: every edge's endpoints must exist as nodes.
        var nodeIds = incremental.Graph.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var edge in incremental.Graph.Edges)
        {
            Assert.Contains(edge.SourceId, nodeIds);
            Assert.Contains(edge.TargetId, nodeIds);
        }

        // The removed method's node itself is gone.
        Assert.DoesNotContain(
            incremental.Graph.Nodes,
            n => n.Kind == NodeKind.Method && n.Fqn == "Fixture.Lib.EvictionChainImplementor.UnrelatedWork()");

        // Specifically, no edge anywhere still points at the removed method's old node id.
        Assert.DoesNotContain(incremental.Graph.Edges, e => e.TargetId == unrelatedWork.Id || e.SourceId == unrelatedWork.Id);

        // D itself survived (only the one member was removed).
        GraphAssert.Node(incremental.Graph, NodeKind.Class, "Fixture.Lib.EvictionChainImplementor");
        Assert.Equal(implementor.Id, GraphAssert.Node(incremental.Graph, NodeKind.Class, "Fixture.Lib.EvictionChainImplementor").Id);
    }

    [Fact]
    public async Task TouchingConstDeclaringFile_FieldUsageEdgesSurviveTheEvictionBoundary()
    {
        // v0.6.1 spot-check: the new field-usage References edges must survive the #6
        // eviction-and-rewalk cycle like every other edge kind. VendorActivity.cs declares
        // VendorActivityTypes.Deactivated; its usage edges arrive from DeactivateVendorCommand.cs
        // (same project) AND VendorAudit.cs (FixtureApp) — the latter crosses the eviction
        // boundary. #6's fix is edge-kind-agnostic by design; this verifies rather than assumes.
        CopyDirectory(TestPaths.FixtureSolutionDirectory, _root);
        DotNet.Run("restore FixtureSolution.sln", _root);
        string solutionPath = Path.Combine(_root, "FixtureSolution.sln");
        var analyzer = new RoslynSolutionAnalyzer();

        var cold = await analyzer.AnalyzeAsync(solutionPath);
        AssertCrossProjectConstUsageEdgePresent(cold.Graph);

        // Whitespace-only touch, zero semantic change — same shape as the #6 repro above.
        File.AppendAllText(Path.Combine(_root, "FixtureLib", "VendorActivity.cs"), "\n");

        var incremental = await analyzer.AnalyzeAsync(solutionPath, cold);

        Assert.True(
            incremental.Stats.DocumentsAnalyzed < cold.Stats.DocumentsAnalyzed,
            $"expected an incremental re-walk, got the full {incremental.Stats.DocumentsAnalyzed} documents");

        // cold == incremental, in full: identical node-id set and identical edge set.
        var coldNodeIds = cold.Graph.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var incrementalNodeIds = incremental.Graph.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        Assert.True(coldNodeIds.SetEquals(incrementalNodeIds), "node sets diverged between cold and incremental");
        Assert.True(
            cold.Graph.Edges.ToHashSet().SetEquals(incremental.Graph.Edges),
            "edge sets diverged between cold and incremental");

        AssertCrossProjectConstUsageEdgePresent(incremental.Graph);
    }

    private static void AssertCrossProjectConstUsageEdgePresent(CodeGraph graph)
    {
        var audit = GraphAssert.Node(graph, NodeKind.Method, "Fixture.App.VendorAudit.IsDeactivation(string)");
        var command = GraphAssert.Node(graph, NodeKind.Property, "Fixture.Lib.DeactivateVendorCommand.ActivityType");
        var deactivated = GraphAssert.Node(graph, NodeKind.Field, "Fixture.Lib.VendorActivityTypes.Deactivated");
        GraphAssert.Edge(graph, audit, deactivated, RelationshipKind.References);
        GraphAssert.Edge(graph, command, deactivated, RelationshipKind.References);
    }

    private static void AssertEToDEdgePresent(CodeGraph graph)
    {
        var consumerMethod = GraphAssert.Node(graph, NodeKind.Method, "Fixture.Lib.EvictionChainConsumer.UseUnrelatedWork()");
        var unrelatedWork = GraphAssert.Node(graph, NodeKind.Method, "Fixture.Lib.EvictionChainImplementor.UnrelatedWork()");
        GraphAssert.Edge(graph, consumerMethod, unrelatedWork, RelationshipKind.Calls);
    }

    private static void AssertFullyQualifiedEToDEdgePresent(CodeGraph graph)
    {
        var consumerMethod = GraphAssert.Node(graph, NodeKind.Method, "Fixture.Lib.EvictionChainConsumer.UseFullyQualifiedReferenceToD()");
        var implementor = GraphAssert.Node(graph, NodeKind.Class, "Fixture.Lib.EvictionChainImplementor");
        GraphAssert.Edge(graph, consumerMethod, implementor, RelationshipKind.References);
    }

    private static void AssertUnrelatedEventNodeAndContainsEdgePresent(CodeGraph graph)
    {
        var implementor = GraphAssert.Node(graph, NodeKind.Class, "Fixture.Lib.EvictionChainImplementor");
        var unrelatedEvent = GraphAssert.Node(graph, NodeKind.Event, "Fixture.Lib.EvictionChainImplementor.UnrelatedEvent");
        GraphAssert.Edge(graph, implementor, unrelatedEvent, RelationshipKind.Contains);
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
