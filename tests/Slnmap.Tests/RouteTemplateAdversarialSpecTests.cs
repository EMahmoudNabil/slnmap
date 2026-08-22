using Slnmap.Mcp;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// Cross-stack linker investigation (reports/cross-stack-linker-investigation.md §Q2.3/§Q2.4):
/// characterizes what the EXISTING, unmodified <see cref="RouteTemplate"/> actually does with
/// adversarial inputs the linker will feed it at scale — hole-vs-hole, case, trailing slashes,
/// duplicate slashes — verified by running it, not by reading its source. All of these PASS
/// today: there is no gap in <see cref="RouteTemplate"/> itself for any of these shapes, only in
/// the linker that has yet to call it. This file adds NO production code.
/// </summary>
public sealed class RouteTemplateAdversarialSpecTests
{
    [Fact]
    public void HoleVsHole_DifferentParamNames_MatchTrivially()
    {
        // /Tools/{*} (frontend, anonymous) vs /Tools/{id:int} (backend, named+constrained): both
        // normalize their hole to the same "{x}" token regardless of name or constraint, so the
        // skeletons are textually identical and the fast-path equality check in Matches short-
        // circuits to true before the segment loop even runs.
        string frontend = RouteTemplate.Normalize("/api/Tools/{*}");
        string backend = RouteTemplate.Normalize("/api/Tools/{id:int}");

        Assert.Equal("api/tools/{x}", frontend);
        Assert.Equal(frontend, backend);
        Assert.True(RouteTemplate.Matches(backend, frontend));
    }

    [Fact]
    public void HoleVsHole_DifferentSegmentCounts_DoNotMatch()
    {
        // /Tools/{*}/{*} (2 holes) vs /Tools/{id} (1 hole): segment counts differ, so this is a
        // clean rejection, not a partial/fuzzy match — confirms the design doesn't need extra
        // handling here; the existing segment-count guard already covers it.
        string frontend = RouteTemplate.Normalize("/api/Tools/{*}/{*}");
        string backend = RouteTemplate.Normalize("/api/Tools/{id}");

        Assert.False(RouteTemplate.Matches(backend, frontend));
    }

    [Fact]
    public void CaseSensitivity_FrontendLowercase_BackendPascalCase_MatchAfterNormalization()
    {
        // The dominant real-world shape (OSSUS_Frontend writes lowercase; OSSUS_Backend composes
        // PascalCase class-name-derived segments) -- ASP.NET matches case-insensitively, so
        // case-folded comparison is the framework's own semantics, not a linker heuristic.
        string frontend = RouteTemplate.Normalize("/api/OrganizationUsers");
        string backend = RouteTemplate.Normalize("/API/organizationusers");

        Assert.Equal(frontend, backend);
        Assert.True(RouteTemplate.Matches(backend, frontend));
    }

    [Fact]
    public void TrailingSlash_FrontendWithBackendWithout_MatchAfterTrimming()
    {
        string withTrailingSlash = RouteTemplate.Normalize("/api/vendors/");
        string withoutTrailingSlash = RouteTemplate.Normalize("/api/vendors");

        Assert.Equal(withTrailingSlash, withoutTrailingSlash);
        Assert.True(RouteTemplate.Matches(withoutTrailingSlash, withTrailingSlash));
    }

    [Fact]
    public void DuplicateSlashes_FromNaiveStringConcatenation_CollapseBeforeComparison()
    {
        // A common real cause: a BFF base URL ending in "/" concatenated with a call-site path
        // starting with "/" (e.g. `${baseUrl}/${path}` where both already carry a slash).
        string doubled = RouteTemplate.Normalize("/api//vendors///current");
        string clean = RouteTemplate.Normalize("/api/vendors/current");

        Assert.Equal(clean, doubled);
        Assert.True(RouteTemplate.Matches(clean, doubled));
    }

    [Fact]
    public void QueryStringAndHashFragment_AreStrippedBeforeComparison()
    {
        string withQueryAndHash = RouteTemplate.Normalize("/api/vendors/current?tab=details#section");
        string clean = RouteTemplate.Normalize("/api/vendors/current");

        Assert.Equal(clean, withQueryAndHash);
    }

    [Fact]
    public void RootRoute_NormalizesToEmptyString_OnBothSides()
    {
        // A MapGroup-only route (group.MapGet("/", Handler)) normalizes to "" per RouteTemplate's
        // own documented contract; a frontend call site of a bare base path must agree.
        Assert.Equal(string.Empty, RouteTemplate.Normalize("/"));
        Assert.Equal(string.Empty, RouteTemplate.Normalize(""));
        Assert.True(RouteTemplate.Matches(RouteTemplate.Normalize("/"), RouteTemplate.Normalize("")));
    }
}
