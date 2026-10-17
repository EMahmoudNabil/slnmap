using Slnmap.Mcp;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// v0.12.1: the second-machine field trial's literal-vs-literal false-ambiguity bug
/// (cross-stack-linker-v0121-fix-report.md). The truth table for every segment-pair shape,
/// pinned after the fix — plus the real fraud-summary shape and the OrderQueue fan-out guard
/// (the two shapes that must land on OPPOSITE sides of the fix).
///
/// The single rule: a hole absorbing the other side's literal is trustworthy only when it
/// happens in ONE direction across the whole route. Two segments each independently excusing a
/// mismatch in OPPOSITE directions (template-hole-absorbs-query-literal at one position AND
/// query-hole-absorbs-template-literal at another) means neither side's fixed segments actually
/// echo anything on the other side — the "overlap" is a coincidence of two unrelated literals.
/// </summary>
public sealed class RouteTemplateLiteralMismatchGapTests
{
    // --- The five-cell truth table, single differing position, everything else identical ------

    [Fact]
    public void Cell_ConcreteVsSameLiteral_Matches()
    {
        Assert.True(RouteTemplate.Matches(
            RouteTemplate.Normalize("/api/sellers/profile"),
            RouteTemplate.Normalize("/api/sellers/profile")));
    }

    [Fact]
    public void Cell_ConcreteVsDifferentLiteral_NeverMatches()
    {
        // The naive reading of the bug -- already correct before this fix, and still correct
        // after it: two different literals, neither a hole, never bind to each other.
        Assert.False(RouteTemplate.Matches(
            RouteTemplate.Normalize("/api/sellers/fraud-summary"),
            RouteTemplate.Normalize("/api/sellers/profile")));
    }

    [Fact]
    public void Cell_ConcreteVsHole_Matches()
    {
        Assert.True(RouteTemplate.Matches(
            RouteTemplate.Normalize("/api/sellers/{id}"),
            RouteTemplate.Normalize("/api/sellers/profile")));
    }

    [Fact]
    public void Cell_HoleVsHole_Matches()
    {
        Assert.True(RouteTemplate.Matches(
            RouteTemplate.Normalize("/api/sellers/{id}"),
            RouteTemplate.Normalize("/api/sellers/{*}")));
    }

    [Fact]
    public void Cell_HoleVsLiteral_Matches()
    {
        // Fan-out contract: a call-site hole must keep matching ANY single endpoint literal,
        // in isolation (one differing position only).
        Assert.True(RouteTemplate.Matches(
            RouteTemplate.Normalize("/api/sellers/fraud-summary"),
            RouteTemplate.Normalize("/api/sellers/{*}")));
    }

    // --- The real bug: two differing positions, absorption in BOTH directions -----------------

    [Fact]
    public void FraudSummaryShape_CrissCrossAbsorption_NoLongerMatches()
    {
        // call site: /Sellers/{numeric}/profile  ->  api/sellers/{x}/profile
        //   (position 2 is a genuine hole -- the interpolated id; position 3 is the author's
        //   own fixed literal, "profile")
        // endpoint:  /Sellers/fraud-summary/{analysisId} -> api/sellers/fraud-summary/{x}
        //   (position 2 is a fixed literal; position 3 is a route parameter)
        string callSiteSkeleton = RouteTemplate.Normalize("/api" + "/Sellers/{*}/profile");
        string endpointSkeleton = RouteTemplate.Normalize("/api/Sellers/fraud-summary/{analysisId}");

        Assert.Equal("api/sellers/{x}/profile", callSiteSkeleton);
        Assert.Equal("api/sellers/fraud-summary/{x}", endpointSkeleton);

        // Position 2: call site's hole absorbs the endpoint's literal ("fraud-summary").
        // Position 3: the endpoint's hole absorbs the call site's OWN literal ("profile").
        // Both directions fire in the same comparison -- ASP.NET could never route these to
        // each other (that would require the interpolated id to literally BE "fraud-summary"
        // AND the resulting path to coincidentally end in "profile") -- rejected.
        Assert.False(RouteTemplate.Matches(endpointSkeleton, callSiteSkeleton));
    }

    [Fact]
    public void FraudSummarySiblings_AllSixRejectedTheSameWay()
    {
        // The field trial reported 6+ affected sibling endpoints, all sharing the same
        // criss-cross shape with a different literal at the differing position.
        string callSiteSkeleton = RouteTemplate.Normalize("/api/Sellers/{*}/profile");
        string[] siblingLiterals = ["fraud-summary", "cart-summary", "pickup-summary", "payout-summary", "stock-summary", "tax-summary"];

        foreach (string literal in siblingLiterals)
        {
            string endpointSkeleton = RouteTemplate.Normalize($"/api/Sellers/{literal}/{{analysisId}}");
            Assert.False(
                RouteTemplate.Matches(endpointSkeleton, callSiteSkeleton),
                $"expected no match against /Sellers/{literal}/{{analysisId}}");
        }
    }

    // --- The fan-out guard: must survive completely unchanged ----------------------------------

    [Fact]
    public void OrderQueueFanOut_SingleDirectionAbsorption_StillMatchesAllThree()
    {
        // Real shape: the call site's OWN two holes are the only source of absorption; every
        // one of the call site's fixed segments ("orderqueue", "reminder") is echoed exactly by
        // each candidate. One direction only -- must remain a match for all three.
        string callSiteSkeleton = RouteTemplate.Normalize("/api/OrderQueue/{*}/{*}/reminder");
        string[] candidates =
        [
            "/api/OrderQueue/pickups/{taskId}/reminder",
            "/api/OrderQueue/carts/{taskId}/reminder",
            "/api/OrderQueue/refunds/{taskId}/reminder",
        ];

        foreach (string candidate in candidates)
        {
            Assert.True(
                RouteTemplate.Matches(RouteTemplate.Normalize(candidate), callSiteSkeleton),
                $"expected the fan-out guard to hold for {candidate}");
        }
    }

    [Fact]
    public void RowFourPrecedenceShape_SingleDirectionAbsorption_StillMatchesBoth()
    {
        // The literal call site absorbs nothing; only the parameterized endpoint's hole absorbs
        // the call site's literal. One direction only -- both candidates must still pass
        // RouteTemplate.Matches (precedence, not Matches, is what picks the winner).
        string callSiteSkeleton = RouteTemplate.Normalize("/api/AccountProfiles/current");

        Assert.True(RouteTemplate.Matches(RouteTemplate.Normalize("/api/AccountProfiles/current"), callSiteSkeleton));
        Assert.True(RouteTemplate.Matches(RouteTemplate.Normalize("/api/AccountProfiles/{id}"), callSiteSkeleton));
    }
}
