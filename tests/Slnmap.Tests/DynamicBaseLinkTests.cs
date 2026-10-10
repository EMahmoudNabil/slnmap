using Slnmap.Core.Graph;
using Slnmap.Mcp;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// v0.15.0: a call site whose template starts with an unresolved hole (<c>{*}/api/VendorPortal</c>,
/// from <c>`${apiUrl}/api/VendorPortal`</c> with a runtime-computed <c>apiUrl</c>) links by its
/// remainder, marked as inferred. Found on OSSUS, where it was an orphan.
/// </summary>
public sealed class DynamicBaseLinkTests
{
    private static SymbolNode Endpoint(string verb, string template) =>
        SymbolNode.Create(NodeKind.Endpoint, template, $"{verb} {template}", "Endpoints.cs", new SourceSpan(0, 1));

    private static SymbolNode CallSite(string verb, string template) =>
        SymbolNode.Create(NodeKind.FrontendCallSite, template, $"{verb} src/x.ts:1:1", "x.ts", new SourceSpan(0, 1));

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

    [Theory]
    [InlineData("{*}/api/VendorPortal", true, "/api/VendorPortal")]
    [InlineData("{*}/VendorPortal/{*}", true, "/VendorPortal/{*}")]
    // Holes only: the remainder would match every parameterized endpoint.
    [InlineData("{*}/{*}", false, "{*}/{*}")]
    [InlineData("{*}", false, "{*}")]
    // Not a whole leading segment.
    [InlineData("{*}users", false, "{*}users")]
    [InlineData("/api/{*}/x", false, "/api/{*}/x")]
    [InlineData("https://host/api/{*}", false, "https://host/api/{*}")]
    public void TrySplitDynamicBase_SplitsOnlyAWholeLeadingHoleWithALiteralAfterIt(string template, bool expected, string expectedRemainder)
    {
        Assert.Equal(expected, CrossStackLinker.TrySplitDynamicBase(template, out string remainder));
        Assert.Equal(expectedRemainder, remainder);
    }

    [Fact]
    public void LeadingHole_RemainderMatchesAsAuthored_LinksAndIsMarked()
    {
        // The OSSUS shape: the backend route carries /api, the remainder does too.
        var result = LinkSingle(CallSite("GET", "{*}/api/VendorPortal"), Endpoint("GET", "/api/VendorPortal"));

        Assert.Equal(CallSiteLinkOutcome.Unique, result.Outcome);
        Assert.True(result.ViaDynamicBase);
        Assert.False(result.ViaPrefixStripped);
        Assert.Equal(" via dynamic-base path", result.InferredMarker);
    }

    [Fact]
    public void LeadingHole_RemainderMatchesWithTheBasePath_LinksAndIsMarked()
    {
        // The hole was the host and the base path is absorbed elsewhere: the ordinary relative flow.
        var result = LinkSingle(CallSite("GET", "{*}/VendorPortal"), Endpoint("GET", "/api/VendorPortal"));

        Assert.Equal(CallSiteLinkOutcome.Unique, result.Outcome);
        Assert.True(result.ViaDynamicBase);
    }

    [Fact]
    public void LeadingHole_NoMatch_StaysAnHonestOrphan_Unmarked()
    {
        var result = LinkSingle(CallSite("GET", "{*}/api/Nothing"), Endpoint("GET", "/api/VendorPortal"));

        Assert.Equal(CallSiteLinkOutcome.NoSkeletonMatch, result.Outcome);
        Assert.False(result.ViaDynamicBase);
        Assert.Equal(string.Empty, result.InferredMarker);
    }

    [Fact]
    public void LeadingHole_VerbMismatch_NamesTheConflictingEndpoint()
    {
        var result = LinkSingle(CallSite("POST", "{*}/api/VendorPortal"), Endpoint("GET", "/api/VendorPortal"));

        Assert.Equal(CallSiteLinkOutcome.VerbMismatch, result.Outcome);
        Assert.False(result.ViaDynamicBase);
        Assert.Equal("GET /api/VendorPortal", Assert.Single(result.ConflictingVerbEndpoints).Fqn);
    }

    [Fact]
    public void HolesOnly_IsNotTreatedAsADynamicBase()
    {
        // `{*}/{*}` links exactly as it did before (a hole-only skeleton fans out to every
        // two-segment endpoint, a pre-existing rule of RouteTemplate.Matches); what must not
        // happen is the leading hole being dropped, which would turn it into a one-segment query.
        var result = LinkSingle(CallSite("GET", "{*}/{*}"), Endpoint("GET", "/api/{id}"), Endpoint("GET", "/api/VendorPortal"), Endpoint("GET", "/api"));

        Assert.False(result.ViaDynamicBase);
        Assert.DoesNotContain(result.Endpoints, e => e.Fqn == "GET /api");
    }

    [Fact]
    public void ALiteralRelativeCallSite_IsNeverMarked()
    {
        var result = LinkSingle(CallSite("GET", "/VendorPortal"), Endpoint("GET", "/api/VendorPortal"));

        Assert.Equal(CallSiteLinkOutcome.Unique, result.Outcome);
        Assert.False(result.ViaDynamicBase);
        Assert.Equal(string.Empty, result.InferredMarker);
    }
}
