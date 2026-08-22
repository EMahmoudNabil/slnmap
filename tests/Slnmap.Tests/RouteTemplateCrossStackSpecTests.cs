using Slnmap.Mcp;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// `slnmap-ts` frontend extractor investigation (reports/ts-extractor-investigation.md §Q4): the
/// headline salvage finding is that Phase 3's cross-stack join needs NO new normalizer.
/// `Slnmap.Mcp.RouteTemplate` — already shipped for backend-only query-time matching
/// (RouteTemplateTests.cs) — already produces byte-identical skeletons for frontend and backend
/// templates, PROVIDED the frontend side composes its stored template with the same literal
/// base-path prefix the backend already bakes in (EndpointFacts.ComposeTemplate's convention).
///
/// These tests exercise the EXISTING, unmodified RouteTemplate against the 5 real OSSUS-shaped
/// pairs from frontend-feasibility-spike.md §2, using the exact template strings
/// EndpointFacts.ComposeTemplate would produce on the backend side and this investigation's
/// fixture (tests/fixtures-ts/frontend-fixture/) on the frontend side. All PASS today — this is
/// verified evidence for the design decision, not a pinned gap (there is nothing gap-shaped to
/// pin: the normalizer already exists and already works). This file adds no production code.
/// </summary>
public sealed class RouteTemplateCrossStackSpecTests
{
    // Phase 3's configured base-path prefix (design decision, §Q4): the frontend side stores the
    // call-site's literal path with NO invented prefix; the linker prepends this before
    // normalizing. Never invented per-call-site — a single, explicit, project-level convention,
    // the same category of fact EndpointFacts already models for the backend's own MapGroup("/api").
    private const string BasePathPrefix = "/api";

    private static string FrontendSkeleton(string rawCallSiteTemplate) =>
        RouteTemplate.Normalize(BasePathPrefix + rawCallSiteTemplate);

    [Fact]
    public void Pair1_CaseA_PlainLiteral_MatchesExactly()
    {
        // FE: tests/fixtures-ts/frontend-fixture/src/hooks/useUserTaskCenter.ts:17
        string frontend = FrontendSkeleton("/UserTasks/assigned-tasks-with-summary");
        // BE: EndpointFacts.ComposeTemplate("UserTasks", "assigned-tasks-with-summary") under
        // the project's top-level MapGroup("/api").
        string backend = RouteTemplate.Normalize("/api/UserTasks/assigned-tasks-with-summary");

        Assert.Equal("api/usertasks/assigned-tasks-with-summary", frontend);
        Assert.Equal(frontend, backend);
        Assert.True(RouteTemplate.Matches(backend, frontend));
    }

    [Fact]
    public void Pair2_CaseC_ConstThroughBarrelAndServiceObject_MatchesExactly()
    {
        // FE: tests/fixtures-ts/frontend-fixture/src/services/boardMeetingsService.ts:9
        // (three-hop: hook -> barrel -> service object -> const COMMITTEES -> apiClient.get)
        string frontend = FrontendSkeleton("/Committees");
        string backend = RouteTemplate.Normalize("/api/Committees");

        Assert.Equal("api/committees", frontend);
        Assert.Equal(frontend, backend);
        Assert.True(RouteTemplate.Matches(backend, frontend));
    }

    [Fact]
    public void Pair3_CaseB_FanOut_MatchesAllThreeCandidates_ViaExistingHoleRule()
    {
        // FE: tests/fixtures-ts/frontend-fixture/src/hooks/useUserTaskCenter.ts:24 -- both
        // interpolation holes are genuinely runtime-chosen, stored as anonymous {*} tokens.
        string frontend = FrontendSkeleton("/TaskCenter/{*}/{*}/reminder");
        Assert.Equal("api/taskcenter/{x}/{x}/reminder", frontend);

        // BE: three distinct Endpoint nodes (the true fan-out the spike measured).
        string[] backendCandidates =
        [
            RouteTemplate.Normalize("/api/TaskCenter/compliances/{taskId}/reminder"),
            RouteTemplate.Normalize("/api/TaskCenter/risks/{taskId}/reminder"),
            RouteTemplate.Normalize("/api/TaskCenter/governances/{taskId}/reminder"),
        ];

        // RouteTemplate.Matches's existing hole-matches-concrete-segment rule (RouteTemplate.cs
        // line 74) resolves the fan-out with NO new matching logic in Phase 3.
        Assert.All(backendCandidates, candidate => Assert.True(RouteTemplate.Matches(candidate, frontend)));
    }

    [Fact]
    public void Pair4_ParamVsLiteralSiblings_MatchesBothCandidates_AmbiguityIsReal()
    {
        // FE: tests/fixtures-ts/frontend-fixture/src/hooks/useUserProfile.ts:9 -- a plain
        // literal call site.
        string frontend = FrontendSkeleton("/UserProfiles/current");
        Assert.Equal("api/userprofiles/current", frontend);

        string literalCandidate = RouteTemplate.Normalize("/api/UserProfiles/current");
        string paramCandidate = RouteTemplate.Normalize("/api/UserProfiles/{id}");

        // Both match at the skeleton level -- this is the one real, correctly-identified Phase 3
        // gap this audit found: RouteTemplate.Matches alone cannot disambiguate; a route
        // precedence rule (literal beats parameter, ASP.NET's own documented semantics) still
        // needs to be layered on top. Asserting BOTH match here documents that gap precisely
        // instead of hand-waving it.
        Assert.True(RouteTemplate.Matches(literalCandidate, frontend));
        Assert.True(RouteTemplate.Matches(paramCandidate, frontend));
    }

    [Fact]
    public void Pair5_DanglingBug_CorrectlyNoMatch_AndCaseFoldIsProvenReal()
    {
        // FE: tests/fixtures-ts/frontend-fixture/src/hooks/useOrganizationUsers.ts:10 -- the
        // spike's real production bug, written in lowercase exactly as OSSUS_Frontend wrote it.
        string frontend = FrontendSkeleton("/organizationusers");
        Assert.Equal("api/organizationusers", frontend);

        // No POST /api/OrganizationUsers registration exists on the backend for this case. Prove
        // the miss is real (no registration), not an artifact of casing drift, by first showing
        // the case-fold works: differently-cased inputs still produce the same skeleton.
        string sameRouteDifferentCase = RouteTemplate.Normalize("/api/OrganizationUsers");
        Assert.Equal(frontend, sameRouteDifferentCase);
        Assert.True(RouteTemplate.Matches(sameRouteDifferentCase, frontend));

        // But the actual backend route table for this group has no matching template at all --
        // simulated here by an unrelated sibling endpoint on the same resource group.
        string unrelatedSibling = RouteTemplate.Normalize("/api/OrganizationUsers/{id}");
        Assert.False(RouteTemplate.Matches(unrelatedSibling, frontend));
    }
}
