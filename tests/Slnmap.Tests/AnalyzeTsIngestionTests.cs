using System.Diagnostics;
using System.Linq;
using Slnmap.Analysis;
using Slnmap.Core.Graph;
using Slnmap.Core.Storage;
using Slnmap.Storage;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// Deeper `analyze-ts` ingestion tests (reports/analyze-ts-verb-report.md Part 3) — mirrors
/// EndpointNodeTests.cs's relationship to EndpointNodeGapTests.cs: the gap file pins the
/// designed shape and stays where it is once shipped; this file tests the real, shipped
/// mechanics in depth. Covers: fqn/name construction against TsArtifactFacts directly (§7.2),
/// the kind-scoped prune-and-replace leaving non-frontend nodes untouched, and end-to-end
/// idempotence through the real CLI.
/// </summary>
public sealed class AnalyzeTsIngestionTests
{
    private static readonly TsArtifact SampleArtifact = new(
        SchemaVersion: 2,
        Producer: "slnmap-ts",
        ProducerVersion: "0.2.0",
        Stats: new TsArtifactStats(1, 1, 50.0, new Dictionary<string, int> { ["dynamic-base-url"] = 1 }),
        CallSites:
        [
            new TsArtifactCallSite(
                Kind: "FrontendCallSite", Verb: "GET", Template: "/Vendors", ResolutionTier: "literal",
                Category: null, Reason: null, File: "src/services/vendors.ts", Line: 5, Column: 10,
                SpanStart: 100, SpanEnd: 130),
            new TsArtifactCallSite(
                Kind: "UnresolvedCallSite", Verb: "GET", Template: null, ResolutionTier: null,
                Category: "dynamic-base-url", Reason: "value is read from an environment variable",
                File: "src/services/vendors.ts", Line: 9, Column: 4, SpanStart: 200, SpanEnd: 230),
        ]);

    [Fact]
    public void BuildNodes_ProducesFqnAndNameExactlyPerTheInvestigationsScheme()
    {
        string frontendRoot = Path.Combine(Path.GetTempPath(), $"slnmap-ts-root-{Guid.NewGuid():N}");
        var nodes = TsArtifactFacts.BuildNodes(SampleArtifact, frontendRoot);

        var resolved = Assert.Single(nodes, n => n.Kind == NodeKind.FrontendCallSite);
        Assert.Equal("/Vendors", resolved.Name);
        Assert.Equal("GET src/services/vendors.ts:5:10", resolved.Fqn);
        Assert.Equal(new SourceSpan(100, 130), resolved.Span);
        Assert.Equal(Path.GetFullPath(Path.Combine(frontendRoot, "src/services/vendors.ts")), resolved.FilePath);

        var unresolved = Assert.Single(nodes, n => n.Kind == NodeKind.UnresolvedCallSite);
        Assert.Equal("dynamic-base-url: value is read from an environment variable", unresolved.Name);
        Assert.Equal("GET dynamic-base-url src/services/vendors.ts:9:4", unresolved.Fqn);
        Assert.Equal(new SourceSpan(200, 230), unresolved.Span);
    }

    /// <summary>
    /// v0.12.2 regression (foreign-patterns-trial finding #3): node identity is `(kind, fqn)`,
    /// and fqn embeds `line:column` — so two call sites that legitimately land at DIFFERENT
    /// positions must persist as two distinct graph nodes. The Turborepo kitchen-sink bug (4
    /// reported, 3 persisted) was never a defect here — this ingestion step has always
    /// deduplicated correctly by identity; the defect was upstream in slnmap-ts reporting two
    /// distinct fluent-chain call sites at the SAME line:column (fixed in walk.ts's
    /// <c>positionOf</c>/<c>anchorStart</c>). This test pins the ingestion half of that
    /// contract directly, independent of the TS extractor: given two artifact records that
    /// already carry distinct positions (as the fixed extractor now always produces), both
    /// must come through as separate nodes with an "identity count == reported count" tally.
    /// </summary>
    [Fact]
    public void BuildNodes_TwoUnresolvedCallSitesAtDistinctPositions_PersistAsTwoDistinctNodes()
    {
        // Same verb/category/file — the exact shape of two chained Express `.get(...)` calls
        // classified as unrecognized-callee — differing ONLY in line/column, as the fixed
        // extractor now guarantees for every link in a fluent chain.
        var artifact = new TsArtifact(
            SchemaVersion: 2,
            Producer: "slnmap-ts",
            ProducerVersion: "0.2.2",
            Stats: new TsArtifactStats(0, 2, 0.0, new Dictionary<string, int> { ["unrecognized-callee"] = 2 }),
            CallSites:
            [
                new TsArtifactCallSite(
                    Kind: "UnresolvedCallSite", Verb: "GET", Template: null, ResolutionTier: null,
                    Category: "unrecognized-callee", Reason: "receiver 'app' does not resolve to a known HTTP client",
                    File: "src/server.ts", Line: 6, Column: 6, SpanStart: 137, SpanEnd: 223),
                new TsArtifactCallSite(
                    Kind: "UnresolvedCallSite", Verb: "GET", Template: null, ResolutionTier: null,
                    Category: "unrecognized-callee", Reason: "receiver 'app' does not resolve to a known HTTP client",
                    File: "src/server.ts", Line: 9, Column: 6, SpanStart: 229, SpanEnd: 301),
            ]);

        string frontendRoot = Path.Combine(Path.GetTempPath(), $"slnmap-ts-root-{Guid.NewGuid():N}");
        var nodes = TsArtifactFacts.BuildNodes(artifact, frontendRoot);
        Assert.Equal(2, nodes.Count);
        Assert.Equal(2, nodes.Select(n => n.Fqn).Distinct().Count());

        var graph = new CodeGraph();
        foreach (var node in nodes)
        {
            Assert.True(graph.AddNode(node), $"expected '{node.Fqn}' to be added as a new node, not collapsed into an existing one");
        }

        Assert.Equal(2, graph.Nodes.Count(n => n.Kind == NodeKind.UnresolvedCallSite));
    }

    [Fact]
    public void MergeIntoGraph_PrunesOnlyFrontendKinds_LeavesEverythingElseUntouched()
    {
        var existing = new CodeGraph();
        var csharpClass = SymbolNode.Create(NodeKind.Class, "Widget", "N.Widget", "Widget.cs", new SourceSpan(0, 10));
        var csharpMethod = SymbolNode.Create(NodeKind.Method, "Do", "N.Widget.Do()", "Widget.cs", new SourceSpan(11, 20));
        var staleFrontend = SymbolNode.Create(
            NodeKind.FrontendCallSite, "/Old", "GET src/old.ts:1:1", "/abs/src/old.ts", new SourceSpan(0, 5));
        existing.AddNode(csharpClass);
        existing.AddNode(csharpMethod);
        existing.AddNode(staleFrontend);
        existing.AddEdge(new RelationshipEdge(csharpClass.Id, csharpMethod.Id, RelationshipKind.Contains));

        var newNodes = TsArtifactFacts.BuildNodes(SampleArtifact, "/frontend-root");
        var merged = TsArtifactFacts.MergeIntoGraph(existing, newNodes);

        // C# nodes and their edge survive byte-for-byte.
        Assert.True(merged.ContainsNode(csharpClass.Id));
        Assert.True(merged.ContainsNode(csharpMethod.Id));
        Assert.Contains(merged.OutgoingEdges(csharpClass.Id, RelationshipKind.Contains), e => e.TargetId == csharpMethod.Id);

        // The stale frontend node is GONE, replaced by the new set.
        Assert.False(merged.ContainsNode(staleFrontend.Id));
        Assert.Equal(2, merged.Nodes.Count(n => n.Kind is NodeKind.FrontendCallSite or NodeKind.UnresolvedCallSite));
        Assert.DoesNotContain(merged.Nodes, n => n.Fqn == staleFrontend.Fqn);

        // Nothing else was touched: 2 C# nodes + 2 fresh frontend nodes, exactly.
        Assert.Equal(4, merged.NodeCount);
        Assert.Equal(1, merged.EdgeCount);
    }

    [Fact]
    public void MergeIntoGraph_PrunesDanglingEdgesFromRemovedFrontendNodes_LeavesCSharpEdgesUntouched()
    {
        // cross-stack-linker-investigation.md §Q3 prerequisite: MergeIntoGraph's old contract
        // ("these two kinds carry zero edges, so none can be left dangling by the prune") breaks
        // the moment ANY edge is attached to a FrontendCallSite/UnresolvedCallSite node — exactly
        // what the Phase 3 linker's CallsEndpoint edges do. This test predates
        // RelationshipKind.CallsEndpoint on purpose (Part 0 is a standalone, bisect-safe fix,
        // independent of the linker feature that motivated finding it) — it stands in with
        // RelationshipKind.References, since the bug is about ANY edge sourced from a node the
        // kind-scoped prune removes, not about CallsEndpoint's specific semantics. The same
        // mechanism this proves is what makes a real CallsEndpoint edge survive correctly too.
        var existing = new CodeGraph();
        var csharpClass = SymbolNode.Create(NodeKind.Class, "Widget", "N.Widget", "Widget.cs", new SourceSpan(0, 10));
        var csharpMethod = SymbolNode.Create(NodeKind.Method, "Do", "N.Widget.Do()", "Widget.cs", new SourceSpan(11, 20));
        var staleFrontend = SymbolNode.Create(
            NodeKind.FrontendCallSite, "/Old", "GET src/old.ts:1:1", "/abs/src/old.ts", new SourceSpan(0, 5));
        existing.AddNode(csharpClass);
        existing.AddNode(csharpMethod);
        existing.AddNode(staleFrontend);
        existing.AddEdge(new RelationshipEdge(csharpClass.Id, csharpMethod.Id, RelationshipKind.Contains));

        // The edge a future linker would write, sourced from the node about to be pruned.
        existing.AddEdge(new RelationshipEdge(staleFrontend.Id, csharpMethod.Id, RelationshipKind.References));

        var newNodes = TsArtifactFacts.BuildNodes(SampleArtifact, "/frontend-root");
        var merged = TsArtifactFacts.MergeIntoGraph(existing, newNodes);

        // The C# edge survives untouched.
        Assert.Contains(merged.OutgoingEdges(csharpClass.Id, RelationshipKind.Contains), e => e.TargetId == csharpMethod.Id);

        // The edge sourced from the pruned frontend node must NOT survive as a dangling row —
        // its source id no longer exists anywhere in the merged graph.
        Assert.False(merged.ContainsNode(staleFrontend.Id));
        Assert.DoesNotContain(merged.Edges, e => e.SourceId == staleFrontend.Id || e.TargetId == staleFrontend.Id);

        // Exactly the one legitimate C# edge remains; the dangling one is gone, not silently
        // duplicated or otherwise miscounted.
        Assert.Equal(1, merged.EdgeCount);
    }

    [Fact]
    public async Task EndToEnd_ReIngestion_ReplacesStaleFrontendNodesAndLeavesCSharpNodesUntouched()
    {
        string db = Path.Combine(Path.GetTempPath(), $"slnmap-ts-reingest-{Guid.NewGuid():N}.db");
        try
        {
            // Seed the db with a C# node (simulating a prior `slnmap analyze`) plus a stale
            // frontend node with an fqn that will NOT appear in a fresh extraction — proving
            // "replaced", not "merged/duplicated".
            var seedGraph = new CodeGraph();
            var csharpNode = SymbolNode.Create(NodeKind.Class, "Widget", "N.Widget", "Widget.cs", new SourceSpan(0, 10));
            var staleFrontendNode = SymbolNode.Create(
                NodeKind.FrontendCallSite, "/StaleRoute", "GET this-file-does-not-exist.ts:999:1",
                "this-file-does-not-exist.ts", new SourceSpan(0, 5));
            seedGraph.AddNode(csharpNode);
            seedGraph.AddNode(staleFrontendNode);

            await using (var seedStore = new SqliteGraphStore(db))
            {
                await seedStore.SaveAsync(seedGraph, [], new Dictionary<string, string>(StringComparer.Ordinal));
            }

            string fixtureDir = Path.Combine(TestPaths.RepoRoot, "tests", "fixtures-ts", "frontend-fixture");
            var (exit, stdout, stderr) = RunCli("analyze-ts", fixtureDir, "--db", db);
            Assert.True(exit == 0, $"analyze-ts failed: exit={exit}\n{stdout}\n{stderr}");

            await using var store = new SqliteGraphStore(db);
            var graph = await store.LoadGraphAsync();

            // C# node untouched.
            Assert.True(graph.ContainsNode(csharpNode.Id));
            Assert.True(graph.TryGetNode(csharpNode.Id, out var reloadedCsharp));
            Assert.Equal(csharpNode, reloadedCsharp);

            // Stale frontend node gone; fresh set present (matching Task A's known fixture counts).
            Assert.False(graph.ContainsNode(staleFrontendNode.Id));
            Assert.Equal(5, graph.Nodes.Count(n => n.Kind == NodeKind.FrontendCallSite));
            Assert.Equal(6, graph.Nodes.Count(n => n.Kind == NodeKind.UnresolvedCallSite));
            Assert.Equal(1 + 5 + 6, graph.NodeCount);
        }
        finally
        {
            TryDelete(db);
        }
    }

    [Fact]
    public async Task Idempotence_RunningTwiceOnUnchangedInput_ProducesAnIdenticalGraph()
    {
        string db = Path.Combine(Path.GetTempPath(), $"slnmap-ts-idempotent-{Guid.NewGuid():N}.db");
        try
        {
            string fixtureDir = Path.Combine(TestPaths.RepoRoot, "tests", "fixtures-ts", "frontend-fixture");

            var first = RunCli("analyze-ts", fixtureDir, "--db", db);
            Assert.True(first.ExitCode == 0, $"First run failed: {first.Stdout}\n{first.Stderr}");

            await using var storeAfterFirst = new SqliteGraphStore(db);
            var graphAfterFirst = await storeAfterFirst.LoadGraphAsync();

            var second = RunCli("analyze-ts", fixtureDir, "--db", db);
            Assert.True(second.ExitCode == 0, $"Second run failed: {second.Stdout}\n{second.Stderr}");

            await using var storeAfterSecond = new SqliteGraphStore(db);
            var graphAfterSecond = await storeAfterSecond.LoadGraphAsync();

            Assert.Equal(graphAfterFirst.NodeCount, graphAfterSecond.NodeCount);
            Assert.Equal(graphAfterFirst.EdgeCount, graphAfterSecond.EdgeCount);
            Assert.True(graphAfterFirst.Nodes.ToHashSet().SetEquals(graphAfterSecond.Nodes));
            Assert.True(graphAfterFirst.Edges.ToHashSet().SetEquals(graphAfterSecond.Edges));
        }
        finally
        {
            TryDelete(db);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a temp file.
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) RunCli(params string[] args)
    {
        string config = AppContext.BaseDirectory.Replace('\\', '/')
            .Contains("/Release/", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        string cliDll = Path.Combine(TestPaths.RepoRoot, "src", "Slnmap.Cli", "bin", config, "net9.0", "slnmap.dll");
        Assert.True(File.Exists(cliDll), $"CLI not built at {cliDll}");

        string workDir = Path.Combine(Path.GetTempPath(), $"slnmap-ts-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = workDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(cliDll);
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start the slnmap CLI.");
            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(60_000))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException("slnmap CLI timed out.");
            }

            return (process.ExitCode, stdout, stderr);
        }
        finally
        {
            try
            {
                Directory.Delete(workDir, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a temp directory.
            }
        }
    }
}
