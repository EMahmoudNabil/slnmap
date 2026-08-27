using Slnmap.Core.Graph;
using Slnmap.Core.Storage;
using Slnmap.Mcp;
using Slnmap.Storage;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// v0.12.3 fix (reports/link-noskeletonmatch-investigation-report.md): a call site whose OWN
/// literal already includes the base-path prefix (e.g. a bare `fetch('/api/orders')`, no axios
/// `baseURL` absorbing it) used to be silently double-prefixed to `/api/api/orders` and reported
/// `NoSkeletonMatch` against every endpoint, including a byte-identical `/api/orders` one. Fixed
/// by trying both the raw and the prefixed skeleton as independent candidates.
/// </summary>
public sealed class CrossStackLinkerBasePathTests
{
    private static SymbolNode Endpoint(string verb, string template) =>
        SymbolNode.Create(NodeKind.Endpoint, template, $"{verb} {template}", "Endpoints.cs", new SourceSpan(0, 1));

    private static SymbolNode CallSite(string verb, string template, string location = "src/x.ts:1:1") =>
        SymbolNode.Create(NodeKind.FrontendCallSite, template, $"{verb} {location}", "x.ts", new SourceSpan(0, 1));

    private static CallSiteLinkResult LinkSingle(SymbolNode callSite, IReadOnlyList<SymbolNode> endpoints, string? basePath = null)
    {
        var graph = new CodeGraph();
        graph.AddNode(callSite);
        foreach (var e in endpoints)
        {
            graph.AddNode(e);
        }

        var results = basePath is null ? CrossStackLinker.Link(graph) : CrossStackLinker.Link(graph, basePath);
        return Assert.Single(results);
    }

    // ---- (a) the investigation's repro, as a permanent regression test ------------------------

    [Fact]
    public void Repro_CallSiteLiteralAlreadyIncludesTheDefaultPrefix_StillLinks()
    {
        // The exact investigation repro: backend "GET /api/orders", frontend "GET /api/orders"
        // (the call site's OWN literal, not analyze-ts-invented). Before the fix: NoSkeletonMatch
        // on every such call site, unconditionally — this MUST link now.
        var endpoint = Endpoint("GET", "/api/orders");
        var callSite = CallSite("GET", "/api/orders");

        var result = LinkSingle(callSite, [endpoint]);

        Assert.Equal(CallSiteLinkOutcome.Unique, result.Outcome);
        Assert.Equal([endpoint], result.Endpoints);
        Assert.Null(result.AmbiguityReason);
    }

    [Fact]
    public void Repro_CallSiteLiteralWithPrefixAndDeeperPath_StillLinks()
    {
        var endpoint = Endpoint("POST", "/api/orders/{orderId}/ship");
        var callSite = CallSite("POST", "/api/orders/{*}/ship");

        var result = LinkSingle(callSite, [endpoint]);

        Assert.Equal(CallSiteLinkOutcome.Unique, result.Outcome);
        Assert.Equal([endpoint], result.Endpoints);
    }

    // ---- (b) the OSSUS shape: bare call-site paths + prefix must keep working -----------------

    [Fact]
    public void OssusShape_BareCallSitePath_StillResolvesViaThePrefix_NoRegression()
    {
        // OSSUS's own convention (axios baseURL absorbs "/api" at runtime, invisible to the
        // extractor): call site Name never carries the prefix. This is the shape the 716/743
        // field-trial numbers depend on — must be completely unaffected by the fix.
        var endpoint = Endpoint("GET", "/api/Vendors");
        var callSite = CallSite("GET", "/Vendors");

        var result = LinkSingle(callSite, [endpoint]);

        Assert.Equal(CallSiteLinkOutcome.Unique, result.Outcome);
        Assert.Equal([endpoint], result.Endpoints);
        Assert.Null(result.AmbiguityReason);
    }

    [Fact]
    public void OssusShape_BareCallSitePath_FanOutAndPrecedenceStillWorkUnchanged()
    {
        // Mirrors CrossStackLinkerTests' existing precedence coverage, re-asserted here
        // specifically under the two-candidate code path to prove it degrades to the exact same
        // single-skeleton behavior when only the prefixed candidate ever matches.
        var literalEndpoint = Endpoint("GET", "/api/UserProfiles/current");
        var paramEndpoint = Endpoint("GET", "/api/UserProfiles/{id}");
        var callSite = CallSite("GET", "/UserProfiles/current");

        var result = LinkSingle(callSite, [literalEndpoint, paramEndpoint]);

        Assert.Equal(CallSiteLinkOutcome.PrecedenceResolved, result.Outcome);
        Assert.Equal([literalEndpoint], result.Endpoints);
    }

    // ---- (c) the ambiguous case discloses, never guesses ---------------------------------------

    [Fact]
    public void PrefixAmbiguous_BothRawAndPrefixedSkeletonsMatch_BecomesDisclosedSetEdge_NeverAGuess()
    {
        // A pathological but real-possible shape: the backend happens to register BOTH the raw
        // path and the prefixed path as real endpoints. Neither is "more correct" than the other
        // from the linker's own knowledge — must disclose both, never silently prefer one.
        var rawEndpoint = Endpoint("GET", "/orders");
        var prefixedEndpoint = Endpoint("GET", "/api/orders");
        var callSite = CallSite("GET", "/orders");

        var result = LinkSingle(callSite, [rawEndpoint, prefixedEndpoint]);

        Assert.Equal(CallSiteLinkOutcome.SetEdge, result.Outcome);
        Assert.Equal(2, result.Endpoints.Count);
        Assert.Contains(rawEndpoint, result.Endpoints);
        Assert.Contains(prefixedEndpoint, result.Endpoints);
        Assert.NotNull(result.AmbiguityReason);
        Assert.Contains("prefix-ambiguous", result.AmbiguityReason, StringComparison.Ordinal);
    }

    [Fact]
    public void PrefixAmbiguous_IsDistinctFromGenuineFanOut_WhichLeavesAmbiguityReasonNull()
    {
        // Genuine fan-out (both candidates resolve from the SAME skeleton, e.g. real runtime
        // fan-out on the prefixed path alone) must NOT be mislabeled as prefix-ambiguous.
        var endpointA = Endpoint("GET", "/api/RiskRegisters/my-risks");
        var endpointB = Endpoint("GET", "/api/RiskRegisters/{id}");
        var callSite = CallSite("GET", "/RiskRegisters/{*}");

        var result = LinkSingle(callSite, [endpointA, endpointB]);

        Assert.Equal(CallSiteLinkOutcome.SetEdge, result.Outcome);
        Assert.Null(result.AmbiguityReason);
    }

    // ---- --base-path "" disables the prefix, without introducing spurious ambiguity -----------

    [Fact]
    public void EmptyBasePath_DisablesThePrefix_RawPathMatchesDirectly()
    {
        var endpoint = Endpoint("GET", "/orders");
        var callSite = CallSite("GET", "/orders");

        var result = LinkSingle(callSite, [endpoint], basePath: "");

        Assert.Equal(CallSiteLinkOutcome.Unique, result.Outcome);
        Assert.Equal([endpoint], result.Endpoints);
        Assert.Null(result.AmbiguityReason);
    }

    [Fact]
    public void EmptyBasePath_DoesNotFalselyReportAmbiguity_WhenOnlyOneRealSkeletonExists()
    {
        // With basePath = "", raw and prefixed skeletons are IDENTICAL by construction — this
        // must collapse to the single-candidate path, never double-count as "both matched".
        var endpointA = Endpoint("GET", "/RiskRegisters/my-risks");
        var endpointB = Endpoint("GET", "/RiskRegisters/{id}");
        var callSite = CallSite("GET", "/RiskRegisters/{*}");

        var result = LinkSingle(callSite, [endpointA, endpointB], basePath: "");

        Assert.Equal(CallSiteLinkOutcome.SetEdge, result.Outcome);
        Assert.Null(result.AmbiguityReason); // genuine fan-out, not prefix-ambiguity
    }

    [Fact]
    public void EmptyBasePath_PreviouslyDoublePrefixedCallSite_LinksEvenMoreDirectly()
    {
        var endpoint = Endpoint("GET", "/api/orders");
        var callSite = CallSite("GET", "/api/orders");

        var result = LinkSingle(callSite, [endpoint], basePath: "");

        Assert.Equal(CallSiteLinkOutcome.Unique, result.Outcome);
    }

    // ---- a custom, non-default prefix works the same way ---------------------------------------

    [Fact]
    public void CustomBasePath_AppliesInsteadOfTheDefault()
    {
        var endpoint = Endpoint("GET", "/gateway/orders");
        var callSite = CallSite("GET", "/orders");

        var result = LinkSingle(callSite, [endpoint], basePath: "/gateway");

        Assert.Equal(CallSiteLinkOutcome.Unique, result.Outcome);
        Assert.Equal([endpoint], result.Endpoints);
    }

    // ---- neither candidate matches -> NoSkeletonMatch, exactly as before ----------------------

    [Fact]
    public void NeitherCandidateMatches_StillNoSkeletonMatch()
    {
        var endpoint = Endpoint("GET", "/api/completely-different");
        var callSite = CallSite("GET", "/orders");

        var result = LinkSingle(callSite, [endpoint]);

        Assert.Equal(CallSiteLinkOutcome.NoSkeletonMatch, result.Outcome);
        Assert.Empty(result.Endpoints);
    }

    [Fact]
    public void NeitherCandidateMatchesSameVerb_ButBothCandidatesFindAnEndpointUnderAnotherVerb_IsVerbMismatch()
    {
        var endpoint = Endpoint("POST", "/api/orders");
        var callSite = CallSite("GET", "/api/orders");

        var result = LinkSingle(callSite, [endpoint]);

        Assert.Equal(CallSiteLinkOutcome.VerbMismatch, result.Outcome);
        Assert.Contains(endpoint, result.ConflictingVerbEndpoints);
    }

    // ---- MCP live-query tools must honor a persisted, non-default base path -------------------

    [Fact]
    public async Task PersistedBasePath_IsRespectedByLiveMcpQueries_NotSilentlyRevertedToTheDefault()
    {
        string db = Path.Combine(Path.GetTempPath(), $"slnmap-basepath-persist-{Guid.NewGuid():N}.db");
        try
        {
            var graph = new CodeGraph();
            graph.AddNode(Endpoint("GET", "/gateway/orders"));
            graph.AddNode(CallSite("GET", "/orders"));

            await using (var store = new SqliteGraphStore(db))
            {
                var meta = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [MetaKeys.LastAnalyzed] = DateTimeOffset.UtcNow.ToString("O"),
                    [MetaKeys.LinkerBasePathPrefix] = "/gateway",
                };
                await store.SaveAsync(graph, [], meta);
            }

            await using var readStore = new SqliteGraphStore(db);
            var queries = new SlnmapQueries(readStore);
            string result = await queries.ListFrontendCallSitesAsync(verb: null, prefix: null);

            Assert.Contains("-> GET /gateway/orders", result, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(db);
        }
    }

    [Fact]
    public async Task NoPersistedBasePath_FallsBackToTheDefault()
    {
        string db = Path.Combine(Path.GetTempPath(), $"slnmap-basepath-default-{Guid.NewGuid():N}.db");
        try
        {
            var graph = new CodeGraph();
            graph.AddNode(Endpoint("GET", "/api/orders"));
            graph.AddNode(CallSite("GET", "/orders"));

            await using (var store = new SqliteGraphStore(db))
            {
                var meta = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [MetaKeys.LastAnalyzed] = DateTimeOffset.UtcNow.ToString("O"),
                };
                await store.SaveAsync(graph, [], meta);
            }

            await using var readStore = new SqliteGraphStore(db);
            var queries = new SlnmapQueries(readStore);
            string result = await queries.ListFrontendCallSitesAsync(verb: null, prefix: null);

            Assert.Contains("-> GET /api/orders", result, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(db);
        }
    }
}
