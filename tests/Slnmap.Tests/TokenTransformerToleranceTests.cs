using Slnmap.Analysis;
using Slnmap.Core.Graph;
using Slnmap.Core.Storage;
using Slnmap.Mcp;
using Slnmap.Storage;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// v0.14.0 Phase 3 (B4; reports/v0140-gate3-token-tolerance.md): when a
/// <c>RouteTokenTransformerConvention</c> is registered, matching tolerates '-'/'_' in the
/// segments substituted from [controller]/[action]/[area] — and nowhere else — always marked as
/// inferred, and only as a fallback after every literal candidate.
/// </summary>
public sealed class TokenTransformerToleranceTests : IDisposable
{
    private const string TransformerConvention =
        "Microsoft.AspNetCore.Mvc.ApplicationModels.RouteTokenTransformerConvention (IActionModelConvention)";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "slnmap-tests", Guid.NewGuid().ToString("N"));

    public TokenTransformerToleranceTests() => Directory.CreateDirectory(_root);

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

    [Theory]
    [InlineData("manage/changepassword", "manage/change-password", "0,1", true)]
    [InlineData("manage/changepassword", "manage/change_password", "0,1", true)]
    [InlineData("order/myorders/{x}", "order/my-orders/42", "0,1", true)]
    // A literal segment is never tolerated: the transformer does not touch it.
    [InlineData("api/orders/changepassword", "api/orders/change-password", "1", false)]
    // Plain mismatches stay mismatches.
    [InlineData("manage/changepassword", "manage/change-email", "0,1", false)]
    [InlineData("manage/changepassword", "manage/change-password/extra", "0,1", false)]
    public void MatchesWithTokenTolerance_OnlyRelaxesTokenSegments(string template, string query, string tokenIndexes, bool expected)
    {
        var segments = tokenIndexes.Split(',').Select(int.Parse).ToHashSet();
        Assert.Equal(expected, RouteTemplate.MatchesWithTokenTolerance(template, query, segments));
    }

    [Fact]
    public void BuildTokenTolerance_IsOffUnlessTheTransformerConventionIsRegistered()
    {
        var segments = new[] { TokenSegments("GET /Manage/ChangePassword", 0, 1) };
        var otherConvention = new[] { RouteConvention("Demo.ApiPrefixConvention (IApplicationModelConvention)") };

        Assert.Null(CrossStackLinker.BuildTokenTolerance([], segments));
        Assert.Null(CrossStackLinker.BuildTokenTolerance(otherConvention, segments));

        var on = CrossStackLinker.BuildTokenTolerance([RouteConvention(TransformerConvention)], segments);
        Assert.NotNull(on);
        Assert.Equal([0, 1], on[SymbolNode.CreateId(NodeKind.Endpoint, "GET /Manage/ChangePassword")].Order());
    }

    [Fact]
    public void Linker_FallsBackToTokenTolerance_MarksIt_AndNeverPrefersItOverALiteralMatch()
    {
        var graph = new CodeGraph();
        var changePassword = SymbolNode.Create(NodeKind.Endpoint, "/Manage/ChangePassword", "GET /Manage/ChangePassword");
        var literal = SymbolNode.Create(NodeKind.Endpoint, "/manage/change-email", "GET /manage/change-email");
        var slugCall = SymbolNode.Create(NodeKind.FrontendCallSite, "/manage/change-password", "GET src/a.ts:1:1");
        var literalCall = SymbolNode.Create(NodeKind.FrontendCallSite, "/manage/change-email", "GET src/a.ts:2:1");
        var literalSegmentCall = SymbolNode.Create(NodeKind.FrontendCallSite, "/manage/changeemail", "GET src/a.ts:3:1");
        foreach (var node in new[] { changePassword, literal, slugCall, literalCall, literalSegmentCall })
        {
            graph.AddNode(node);
        }

        var tolerance = CrossStackLinker.BuildTokenTolerance(
            [RouteConvention(TransformerConvention)],
            [TokenSegments(changePassword.Fqn, 0, 1)]);

        var results = CrossStackLinker.Link(graph, basePathPrefix: string.Empty, tolerance).ToDictionary(r => r.CallSite.Id);

        Assert.Equal(CallSiteLinkOutcome.Unique, results[slugCall.Id].Outcome);
        Assert.Equal(changePassword.Id, Assert.Single(results[slugCall.Id].Endpoints).Id);
        Assert.True(results[slugCall.Id].ViaTokenTransformerTolerance);

        Assert.Equal(literal.Id, Assert.Single(results[literalCall.Id].Endpoints).Id);
        Assert.False(results[literalCall.Id].ViaTokenTransformerTolerance);

        // "change-email" is a LITERAL endpoint segment (no token provenance): not tolerated.
        Assert.Equal(CallSiteLinkOutcome.NoSkeletonMatch, results[literalSegmentCall.Id].Outcome);

        // And with tolerance off, the slug call site is an honest orphan, exactly as before.
        var off = CrossStackLinker.Link(graph, basePathPrefix: string.Empty).Single(r => r.CallSite.Id == slugCall.Id);
        Assert.Equal(CallSiteLinkOutcome.NoSkeletonMatch, off.Outcome);
        Assert.False(off.ViaTokenTransformerTolerance);
    }

    [Fact]
    public async Task FindEndpoint_UsesTheToleranceOnlyAsAMarkedFallback()
    {
        string dbPath = Path.Combine(_root, "graph.db");
        var graph = new CodeGraph();
        var endpoint = SymbolNode.Create(NodeKind.Endpoint, "/Manage/ChangePassword", "GET /Manage/ChangePassword");
        graph.AddNode(endpoint);
        graph.AddDisclosure(RouteConvention(TransformerConvention));
        graph.AddDisclosure(TokenSegments(endpoint.Fqn, 0, 1));
        await using (var seed = new SqliteGraphStore(dbPath))
        {
            await seed.SaveAsync(graph, [], new Dictionary<string, string>(StringComparer.Ordinal) { [MetaKeys.LastAnalyzed] = "test" });
        }

        await using var store = new SqliteGraphStore(dbPath);
        var queries = new SlnmapQueries(store);

        string slug = await queries.FindEndpointAsync("/manage/change-password", null);
        Assert.Contains("1 endpoint(s) match '/manage/change-password' via token-transformer-tolerant match", slug, StringComparison.Ordinal);
        Assert.Contains("GET /Manage/ChangePassword", slug, StringComparison.Ordinal);

        string literal = await queries.FindEndpointAsync("/Manage/ChangePassword", null);
        Assert.Contains("1 endpoint(s) match '/Manage/ChangePassword':", literal, StringComparison.Ordinal);
        Assert.DoesNotContain("tolerant", literal.Split('\n')[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Analyze_RecordsTokenSegmentProvenance_ShiftedByTheRoutePrefix()
    {
        DotNet.Run($"restore \"{TestPaths.FixtureSolution}\"", TestPaths.RepoRoot);

        // FixtureWeb/LegacyControllers.cs: [Route("[controller]/[action]")] on ReportsController.
        var plain = await new RoslynSolutionAnalyzer().AnalyzeAsync(TestPaths.FixtureSolution);
        Assert.Contains(
            DisclosureKinds.TokenSegmentsDetail("GET /Reports/Monthly", [0, 1]),
            plain.Graph.Disclosures.Where(d => d.Kind == DisclosureKinds.TokenSegments).Select(d => d.Detail));
        // [Route("api/[controller]")]: only the second segment is a token.
        Assert.Contains(
            DisclosureKinds.TokenSegmentsDetail("GET /api/Status", [1]),
            plain.Graph.Disclosures.Where(d => d.Kind == DisclosureKinds.TokenSegments).Select(d => d.Detail));

        var prefixed = await new RoslynSolutionAnalyzer(options: new Slnmap.Core.Analysis.AnalysisOptions("api/v2"))
            .AnalyzeAsync(TestPaths.FixtureSolution);
        Assert.Contains(
            DisclosureKinds.TokenSegmentsDetail("GET /api/v2/Reports/Monthly", [2, 3]),
            prefixed.Graph.Disclosures.Where(d => d.Kind == DisclosureKinds.TokenSegments).Select(d => d.Detail));
    }

    private static Disclosure RouteConvention(string detail) => new(DisclosureKinds.RouteConvention, detail, "Program.cs", 0);

    private static Disclosure TokenSegments(string fqn, params int[] indexes) =>
        new(DisclosureKinds.TokenSegments, DisclosureKinds.TokenSegmentsDetail(fqn, indexes), "Controller.cs", 0);
}
