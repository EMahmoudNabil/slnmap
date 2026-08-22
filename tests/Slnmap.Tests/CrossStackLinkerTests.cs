using Slnmap.Core.Graph;
using Slnmap.Mcp;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// Direct unit tests against <see cref="CrossStackLinker"/> (cross-stack-linker-implementation.md
/// Part 4): small hand-built graphs, no full solution analysis, exercising the §Q2.2 precedence
/// shapes and the six-outcome taxonomy's edge cases the fixture-based
/// <c>CrossStackLinkerGapTests</c> don't specifically isolate.
/// </summary>
public sealed class CrossStackLinkerTests
{
    private static SymbolNode Endpoint(string verb, string template) =>
        SymbolNode.Create(NodeKind.Endpoint, template, $"{verb} {template}", "Endpoints.cs", new SourceSpan(0, 1));

    private static SymbolNode CallSite(string verb, string template, string location = "src/x.ts:1:1") =>
        SymbolNode.Create(NodeKind.FrontendCallSite, template, $"{verb} {location}", "x.ts", new SourceSpan(0, 1));

    private static CallSiteLinkResult LinkSingle(SymbolNode callSite, params SymbolNode[] endpoints)
    {
        var graph = new CodeGraph();
        graph.AddNode(callSite);
        foreach (var e in endpoints)
        {
            graph.AddNode(e);
        }

        return Assert.Single(CrossStackLinker.Link(graph));
    }

    [Fact]
    public void Precedence_LiteralCallSite_LiteralVsParamSiblings_ResolvesToTheLiteral()
    {
        var literalEndpoint = Endpoint("GET", "/api/UserProfiles/current");
        var paramEndpoint = Endpoint("GET", "/api/UserProfiles/{id}");
        var callSite = CallSite("GET", "/UserProfiles/current");

        var result = LinkSingle(callSite, literalEndpoint, paramEndpoint);

        Assert.Equal(CallSiteLinkOutcome.PrecedenceResolved, result.Outcome);
        Assert.Equal([literalEndpoint], result.Endpoints);
    }

    [Fact]
    public void Precedence_LiteralCallSite_ConstrainedParamSibling_LiteralStillWins()
    {
        // Mirrors real OSSUS data: /RecurrentControls/stats/summary (literal) vs
        // /RecurrentControls/{month:int}/{year:int} (2 constrained holes) -- constraints don't
        // change the outcome; a hole is a hole for this scoped rule.
        var literalEndpoint = Endpoint("GET", "/api/RecurrentControls/stats/summary");
        var constrainedParamEndpoint = Endpoint("GET", "/api/RecurrentControls/{month:int}/{year:int}");
        var callSite = CallSite("GET", "/RecurrentControls/stats/summary");

        var result = LinkSingle(callSite, literalEndpoint, constrainedParamEndpoint);

        Assert.Equal(CallSiteLinkOutcome.PrecedenceResolved, result.Outcome);
        Assert.Equal([literalEndpoint], result.Endpoints);
    }

    [Fact]
    public void Precedence_CallSiteHoleAtTheOnlyDifferingPosition_CannotResolve_BecomesSetEdge()
    {
        // The call site itself has NO known runtime value at the differing position -- the same
        // shape as real fan-out, and the linker deliberately does not distinguish the two.
        var literalEndpoint = Endpoint("GET", "/api/RiskRegisters/my-risks");
        var paramEndpoint = Endpoint("GET", "/api/RiskRegisters/{id}");
        var callSite = CallSite("GET", "/RiskRegisters/{*}");

        var result = LinkSingle(callSite, literalEndpoint, paramEndpoint);

        Assert.Equal(CallSiteLinkOutcome.SetEdge, result.Outcome);
        Assert.Equal(2, result.Endpoints.Count);
        Assert.Contains(literalEndpoint, result.Endpoints);
        Assert.Contains(paramEndpoint, result.Endpoints);
    }

    [Fact]
    public void Precedence_TwoParamEndpointsWithDifferentNames_AreIndistinguishable_BecomesSetEdge()
    {
        // {id} and {vendorId} both normalize to the identical "{x}" skeleton segment -- there is
        // no differing NORMALIZED position between them at all, so precedence has nothing to
        // compare (this is not the same code path as "call site has a hole"; here even a
        // literal call site can't disambiguate two candidates the normalizer can't tell apart).
        var paramEndpointA = Endpoint("GET", "/api/Widgets/{id}");
        var paramEndpointB = Endpoint("GET", "/api/Widgets/{widgetId}");
        var callSite = CallSite("GET", "/Widgets/42");

        var result = LinkSingle(callSite, paramEndpointA, paramEndpointB);

        Assert.Equal(CallSiteLinkOutcome.SetEdge, result.Outcome);
        Assert.Equal(2, result.Endpoints.Count);
    }

    [Fact]
    public void FanOut_ThreeLiteralSiblings_CallSiteHole_LinksToAllThreeAndOnlyThose()
    {
        // The real TaskCenter shape: exactly 3 registered endpoints, one call site with a hole.
        var compliances = Endpoint("POST", "/api/TaskCenter/compliances/{taskId}/reminder");
        var risks = Endpoint("POST", "/api/TaskCenter/risks/{taskId}/reminder");
        var governances = Endpoint("POST", "/api/TaskCenter/governances/{taskId}/reminder");
        var callSite = CallSite("POST", "/TaskCenter/{*}/{*}/reminder");

        var result = LinkSingle(callSite, compliances, risks, governances);

        Assert.Equal(CallSiteLinkOutcome.SetEdge, result.Outcome);
        Assert.Equal(3, result.Endpoints.Count);
        Assert.Contains(compliances, result.Endpoints);
        Assert.Contains(risks, result.Endpoints);
        Assert.Contains(governances, result.Endpoints);
    }

    [Fact]
    public void VerbGate_UnknownVerb_IsCountedNotLinked_EvenWithAPerfectSkeletonMatch()
    {
        // A bare fetch(url, { method: computeMethod() }) -- the verb is genuinely unknown, never
        // guessed, even when exactly one endpoint would otherwise skeleton-match uniquely.
        var endpoint = Endpoint("GET", "/api/Vendors");
        var callSite = CallSite("UNKNOWN", "/Vendors");

        var result = LinkSingle(callSite, endpoint);

        Assert.Equal(CallSiteLinkOutcome.UnknownVerb, result.Outcome);
        Assert.Empty(result.Endpoints);
    }

    [Fact]
    public void VerbGate_ExactCaseSensitiveEquality_DifferentVerbNeverLinks()
    {
        var getEndpoint = Endpoint("GET", "/api/Vendors");
        var deleteCallSite = CallSite("DELETE", "/Vendors");

        var result = LinkSingle(deleteCallSite, getEndpoint);

        Assert.Equal(CallSiteLinkOutcome.VerbMismatch, result.Outcome);
        Assert.Empty(result.Endpoints);
    }

    [Fact]
    public void NoSkeletonMatch_DifferentSegmentCount_IsOrphanNotVerbMismatch()
    {
        var endpoint = Endpoint("GET", "/api/Vendors");
        var callSite = CallSite("GET", "/Vendors/Reports/Export");

        var result = LinkSingle(callSite, endpoint);

        Assert.Equal(CallSiteLinkOutcome.NoSkeletonMatch, result.Outcome);
        Assert.Empty(result.Endpoints);
    }

    [Fact]
    public void Link_IsIdempotent_SameGraphTwiceProducesIdenticalResults()
    {
        var literalEndpoint = Endpoint("GET", "/api/UserProfiles/current");
        var paramEndpoint = Endpoint("GET", "/api/UserProfiles/{id}");
        var callSite = CallSite("GET", "/UserProfiles/current");
        var graph = new CodeGraph();
        graph.AddNode(callSite);
        graph.AddNode(literalEndpoint);
        graph.AddNode(paramEndpoint);

        var first = CrossStackLinker.Link(graph);
        var second = CrossStackLinker.Link(graph);

        Assert.Equal(CrossStackLinker.ToEdges(first).ToHashSet(), CrossStackLinker.ToEdges(second).ToHashSet());
    }
}
