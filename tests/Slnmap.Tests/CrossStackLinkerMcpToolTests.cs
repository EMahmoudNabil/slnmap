using Slnmap.Analysis;
using Slnmap.Core.Storage;
using Slnmap.Mcp;
using Slnmap.Storage;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// Analyzes the FixtureWeb solution for real, merges in the same six-call-site hand-built
/// frontend artifact <see cref="CrossStackLinkerGapTests"/> uses, runs the real
/// <see cref="CrossStackLinker"/>, and persists the fully-linked graph to a temp database —
/// so the MCP tool tests below read genuinely persisted state through <see cref="IGraphStore"/>,
/// the same path a real MCP client goes through.
/// </summary>
public sealed class LinkedCrossStackGraphStore : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "slnmap-crossstack-mcp-tests", Guid.NewGuid().ToString("N"));

    public SqliteGraphStore Store { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        DotNet.Run($"restore \"{TestPaths.FixtureSolution}\"", TestPaths.RepoRoot);
        var snapshot = await new RoslynSolutionAnalyzer().AnalyzeAsync(TestPaths.FixtureSolution);

        string frontendRoot = Path.Combine(TestPaths.RepoRoot, "tests", "fixtures-ts", "cross-stack-fixture");
        var frontendArtifact = new TsArtifact(
            SchemaVersion: 2,
            Producer: "slnmap-ts",
            ProducerVersion: "0.2.1",
            Stats: new TsArtifactStats(6, 0, 100.0, new Dictionary<string, int>()),
            CallSites:
            [
                new TsArtifactCallSite(Kind: "FrontendCallSite", Verb: "GET", Template: "/vendors",
                    ResolutionTier: "literal", Category: null, Reason: null,
                    File: "src/services/vendorsService.ts", Line: 10, Column: 15, SpanStart: 0, SpanEnd: 1),
                new TsArtifactCallSite(Kind: "FrontendCallSite", Verb: "POST", Template: "/vendors/42",
                    ResolutionTier: "literal", Category: null, Reason: null,
                    File: "src/services/vendorsService.ts", Line: 20, Column: 30, SpanStart: 2, SpanEnd: 3),
                new TsArtifactCallSite(Kind: "FrontendCallSite", Verb: "GET", Template: "/vendors/current",
                    ResolutionTier: "literal", Category: null, Reason: null,
                    File: "src/services/vendorsService.ts", Line: 27, Column: 18, SpanStart: 4, SpanEnd: 5),
                new TsArtifactCallSite(Kind: "FrontendCallSite", Verb: "POST", Template: "/vendors/notify/{*}",
                    ResolutionTier: "template-param-holes", Category: null, Reason: null,
                    File: "src/services/vendorsService.ts", Line: 33, Column: 50, SpanStart: 6, SpanEnd: 7),
                new TsArtifactCallSite(Kind: "FrontendCallSite", Verb: "POST", Template: "/vendors/reports/export/csv",
                    ResolutionTier: "literal", Category: null, Reason: null,
                    File: "src/services/vendorsService.ts", Line: 37, Column: 37, SpanStart: 8, SpanEnd: 9),
                new TsArtifactCallSite(Kind: "FrontendCallSite", Verb: "DELETE", Template: "/vendors",
                    ResolutionTier: "literal", Category: null, Reason: null,
                    File: "src/services/vendorsService.ts", Line: 42, Column: 20, SpanStart: 10, SpanEnd: 11),
            ]);

        var frontendNodes = TsArtifactFacts.BuildNodes(frontendArtifact, frontendRoot);
        var merged = TsArtifactFacts.MergeIntoGraph(snapshot.Graph, frontendNodes);
        var results = CrossStackLinker.Link(merged);
        foreach (var edge in CrossStackLinker.ToEdges(results))
        {
            merged.AddEdge(edge);
        }

        Store = new SqliteGraphStore(Path.Combine(_directory, "graph.db"));
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MetaKeys.SolutionPath] = TestPaths.FixtureSolution,
            [MetaKeys.LastAnalyzed] = "test",
            [MetaKeys.LinkerLastRun] = DateTimeOffset.UtcNow.ToString("O"),
        };
        await Store.SaveAsync(merged, snapshot.Files, meta);
    }

    public async Task DisposeAsync()
    {
        await Store.DisposeAsync();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}

public sealed class CrossStackLinkerMcpToolTests : IClassFixture<LinkedCrossStackGraphStore>
{
    private readonly SlnmapQueries _queries;

    public CrossStackLinkerMcpToolTests(LinkedCrossStackGraphStore fixture) => _queries = new SlnmapQueries(fixture.Store);

    [Fact]
    public async Task FindOrphanCalls_TwoGroupShape_NoMatchAndVerbMismatchAreDistinct()
    {
        string result = await _queries.FindOrphanCallsAsync(null);

        Assert.Contains("no-match (1):", result, StringComparison.Ordinal);
        Assert.Contains("verb-mismatch (1):", result, StringComparison.Ordinal);
        Assert.Contains("/vendors/reports/export/csv", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindOrphanCalls_VerbMismatchMessage_NamesTheConflictingEndpoint()
    {
        string result = await _queries.FindOrphanCallsAsync("verb-mismatch");

        Assert.Contains("no DELETE registered; GET /api/vendors exists", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindOrphanCalls_UnknownCategory_IsAValidatedFailure()
    {
        string result = await _queries.FindOrphanCallsAsync("bogus-category");

        Assert.Contains("Unknown category", result, StringComparison.Ordinal);
        Assert.Contains("no-match", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListFrontendCallSites_ShowsLiveLinkingStatusPerCallSite()
    {
        string result = await _queries.ListFrontendCallSitesAsync(null, null);

        Assert.Contains("-> GET /api/vendors", result, StringComparison.Ordinal);
        Assert.Contains("-> 3 endpoints:", result, StringComparison.Ordinal); // the notify() fan-out
        Assert.Contains("[NoSkeletonMatch]", result, StringComparison.Ordinal);
        Assert.Contains("[VerbMismatch]", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListFrontendCallSites_FilterByVerb_NarrowsResults()
    {
        string result = await _queries.ListFrontendCallSitesAsync("DELETE", null);

        Assert.Contains("1 call site(s):", result, StringComparison.Ordinal);
        Assert.Contains("DELETE", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindEndpoint_MatchedEndpointWithCallers_ListsThemByFqn()
    {
        string result = await _queries.FindEndpointAsync("/api/vendors/current", "GET");

        Assert.Contains("Called from the frontend by:", result, StringComparison.Ordinal);
        Assert.Contains("GET src/services/vendorsService.ts:27:18", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindEndpoint_MatchedEndpointWithNoCallers_OmitsTheCallerLine()
    {
        // The pre-existing archive endpoints have no frontend callers in this fixture.
        string result = await _queries.FindEndpointAsync("/api/vendors/archive", "GET");

        Assert.DoesNotContain("Called from the frontend by:", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImpactAnalysis_OnHandler_ReachesItsFrontendCaller_WithNoToolChange()
    {
        string result = await _queries.ImpactAnalysisAsync("Fixture.Web.VendorEndpoints.GetVendorContext()");

        Assert.Contains("GET /api/vendors/current", result, StringComparison.Ordinal);
        Assert.Contains("GET src/services/vendorsService.ts:27:18", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindUsages_OnEndpoint_SurfacesItsFrontendCaller()
    {
        string result = await _queries.FindUsagesAsync("GET /api/vendors/current");

        Assert.Contains("GET src/services/vendorsService.ts:27:18", result, StringComparison.Ordinal);
    }
}
