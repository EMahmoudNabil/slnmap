using Slnmap.Analysis;
using Slnmap.Core.Graph;
using Slnmap.Mcp;
using Xunit;
using Xunit.Sdk;

namespace Slnmap.Tests;

/// <summary>
/// Cross-stack linker (reports/cross-stack-linker-investigation.md,
/// reports/cross-stack-linker-implementation.md): originally written during the investigation
/// as failing "Gap_*" pins against current main (no <c>RelationshipKind.CallsEndpoint</c>, no
/// linker) — kept as-named now that the design shipped, since a passing "Gap_*" test still
/// documents exactly which design decision it pins and is a real regression guard against a
/// future change that breaks it. The constructor now runs the real, shipped
/// <see cref="CrossStackLinker"/> and materializes its edges into <c>_graph</c>, so every
/// assertion below exercises the actual production linking logic, not a hand-simulated shape.
///
/// Fixtures: tests/fixtures/FixtureSolution/FixtureWeb/VendorEndpoints.cs (backend, analyzed for
/// real via <see cref="AnalyzedFixtureSolution"/>) + tests/fixtures-ts/cross-stack-fixture/src/
/// services/vendorsService.ts (frontend — a REAL source file in its OWN fixture directory,
/// deliberately separate from tests/fixtures-ts/frontend-fixture/, a shared golden fixture with
/// its own hardcoded call-site counts that a dropped-in extra file would silently inflate). The
/// frontend nodes below are hand-built from exactly what the shipped, unmodified `slnmap-ts`
/// extractor produces for that file (confirmed against the real extractor while authoring this
/// file) — no subprocess/npm dependency here, in-memory only, line-ending-agnostic.
/// </summary>
public sealed class CrossStackLinkerGapTests : IClassFixture<AnalyzedFixtureSolution>
{
    private readonly CodeGraph _graph;
    private readonly IReadOnlyList<CallSiteLinkResult> _linkResults;

    public CrossStackLinkerGapTests(AnalyzedFixtureSolution fixture)
    {
        string frontendRoot = Path.Combine(TestPaths.RepoRoot, "tests", "fixtures-ts", "cross-stack-fixture");
        var frontendArtifact = new TsArtifact(
            SchemaVersion: 2,
            Producer: "slnmap-ts",
            ProducerVersion: "0.2.1",
            Stats: new TsArtifactStats(6, 0, 100.0, new Dictionary<string, int>()),
            CallSites:
            [
                // list() -- unique link: literal call site, literal endpoint.
                new TsArtifactCallSite(Kind: "FrontendCallSite", Verb: "GET", Template: "/vendors",
                    ResolutionTier: "literal", Category: null, Reason: null,
                    File: "src/services/vendorsService.ts", Line: 10, Column: 15, SpanStart: 0, SpanEnd: 1),

                // update() -- unique link: LITERAL call site ("42"), single parameterized
                // endpoint. Deliberately not a template interpolation (see vendorsService.ts's
                // own comment): a hole here would also match the pre-existing sibling
                // POST /api/vendors/archive, which is a genuine ambiguity this fixture's
                // original design missed until the real linker caught it during implementation.
                new TsArtifactCallSite(Kind: "FrontendCallSite", Verb: "POST", Template: "/vendors/42",
                    ResolutionTier: "literal", Category: null, Reason: null,
                    File: "src/services/vendorsService.ts", Line: 20, Column: 30, SpanStart: 2, SpanEnd: 3),

                // current() -- ambiguous at the skeleton level (row 4): call site's own segment
                // is a concrete literal, so route precedence must resolve it to one edge.
                new TsArtifactCallSite(Kind: "FrontendCallSite", Verb: "GET", Template: "/vendors/current",
                    ResolutionTier: "literal", Category: null, Reason: null,
                    File: "src/services/vendorsService.ts", Line: 27, Column: 18, SpanStart: 4, SpanEnd: 5),

                // notify(channel) -- true fan-out: call site's own segment is a hole, so all
                // three sibling endpoints are truthfully reachable.
                new TsArtifactCallSite(Kind: "FrontendCallSite", Verb: "POST", Template: "/vendors/notify/{*}",
                    ResolutionTier: "template-param-holes", Category: null, Reason: null,
                    File: "src/services/vendorsService.ts", Line: 33, Column: 50, SpanStart: 6, SpanEnd: 7),

                // bulkImport() -- deliberate orphan: no endpoint of this shape exists at all.
                new TsArtifactCallSite(Kind: "FrontendCallSite", Verb: "POST", Template: "/vendors/reports/export/csv",
                    ResolutionTier: "literal", Category: null, Reason: null,
                    File: "src/services/vendorsService.ts", Line: 37, Column: 37, SpanStart: 8, SpanEnd: 9),

                // removeAll() -- deliberate verb mismatch: skeleton matches GET /api/vendors,
                // but this call site is a DELETE (mirrors the real, still-live OSSUS_Frontend
                // organizationUsers.ts:98 bug this investigation re-confirmed).
                new TsArtifactCallSite(Kind: "FrontendCallSite", Verb: "DELETE", Template: "/vendors",
                    ResolutionTier: "literal", Category: null, Reason: null,
                    File: "src/services/vendorsService.ts", Line: 42, Column: 20, SpanStart: 10, SpanEnd: 11),
            ]);

        var frontendNodes = TsArtifactFacts.BuildNodes(frontendArtifact, frontendRoot);
        _graph = TsArtifactFacts.MergeIntoGraph(fixture.Graph, frontendNodes);

        _linkResults = CrossStackLinker.Link(_graph);
        foreach (var edge in CrossStackLinker.ToEdges(_linkResults))
        {
            _graph.AddEdge(edge);
        }
    }

    [Fact]
    public void Sanity_BothPopulationsArePresentBeforeLinking()
    {
        GraphAssert.Node(_graph, NodeKind.Endpoint, "GET /api/vendors");
        GraphAssert.Node(_graph, NodeKind.Endpoint, "POST /api/vendors/{id}");
        GraphAssert.Node(_graph, NodeKind.Endpoint, "GET /api/vendors/current");
        GraphAssert.Node(_graph, NodeKind.Endpoint, "GET /api/vendors/{vendorId}");
        GraphAssert.Node(_graph, NodeKind.Endpoint, "POST /api/vendors/notify/email");
        GraphAssert.Node(_graph, NodeKind.Endpoint, "POST /api/vendors/notify/sms");
        GraphAssert.Node(_graph, NodeKind.Endpoint, "POST /api/vendors/notify/push");
        Assert.Equal(6, _graph.Nodes.Count(n => n.Kind == NodeKind.FrontendCallSite));
    }

    [Fact]
    public void Gap_UniqueLink_LiteralCallSite_LiteralEndpoint()
    {
        var callSite = FrontendNode("GET", "/vendors");
        AssertCallsEndpoint(callSite, EndpointNode("GET /api/vendors"));
        Assert.Equal(CallSiteLinkOutcome.Unique, OutcomeFor(callSite));
    }

    [Fact]
    public void Gap_UniqueLink_LiteralCallSite_ParameterizedEndpoint()
    {
        var callSite = FrontendNode("POST", "/vendors/42");
        AssertCallsEndpoint(callSite, EndpointNode("POST /api/vendors/{id}"));
        Assert.Equal(CallSiteLinkOutcome.Unique, OutcomeFor(callSite));
    }

    [Fact]
    public void Gap_Ambiguous_RowFour_ResolvedByRoutePrecedence_LiteralWins()
    {
        // The call site's own segment ("current") is concrete, so the linker's route-precedence
        // rule (literal beats parameter) must resolve this to exactly the literal endpoint --
        // not both, and not the parameterized sibling.
        var callSite = FrontendNode("GET", "/vendors/current");
        AssertCallsEndpoint(callSite, EndpointNode("GET /api/vendors/current"));
        Assert.Equal(CallSiteLinkOutcome.PrecedenceResolved, OutcomeFor(callSite));

        var edges = _graph.OutgoingEdges(callSite.Id, RelationshipKind.CallsEndpoint).ToList();
        Assert.DoesNotContain(edges, e => e.TargetId == EndpointNode("GET /api/vendors/{vendorId}").Id);
        Assert.Single(edges);
    }

    [Fact]
    public void Gap_FanOut_HoleAtDifferingSegment_ProducesOneEdgePerCandidate_NoMoreNoFewer()
    {
        var callSite = FrontendNode("POST", "/vendors/notify/{*}");
        SymbolNode[] targets =
        [
            EndpointNode("POST /api/vendors/notify/email"),
            EndpointNode("POST /api/vendors/notify/sms"),
            EndpointNode("POST /api/vendors/notify/push"),
        ];
        foreach (var target in targets)
        {
            AssertCallsEndpoint(callSite, target);
        }

        Assert.Equal(CallSiteLinkOutcome.SetEdge, OutcomeFor(callSite));
        var edges = _graph.OutgoingEdges(callSite.Id, RelationshipKind.CallsEndpoint).ToList();
        Assert.Equal(3, edges.Count);
    }

    [Fact]
    public void Gap_Orphan_NoSkeletonMatchAtAnyVerb_IsQueryableAndHasNoEdge()
    {
        var callSite = FrontendNode("POST", "/vendors/reports/export/csv");
        Assert.Empty(_graph.OutgoingEdges(callSite.Id, RelationshipKind.CallsEndpoint));
        Assert.Equal(CallSiteLinkOutcome.NoSkeletonMatch, OutcomeFor(callSite));
        Assert.Contains(callSite, ComputeOrphans());
    }

    [Fact]
    public void Gap_VerbMismatch_SkeletonMatchesButVerbDiffers_IsNoEdgeNotAGuess()
    {
        var callSite = FrontendNode("DELETE", "/vendors");
        Assert.Empty(_graph.OutgoingEdges(callSite.Id, RelationshipKind.CallsEndpoint));
        Assert.Equal(CallSiteLinkOutcome.VerbMismatch, OutcomeFor(callSite));
        Assert.Contains(callSite, ComputeOrphans());
    }

    [Fact]
    public void Gap_LinkedCallSites_AreNeverAlsoReportedAsOrphans()
    {
        var orphans = ComputeOrphans();
        Assert.DoesNotContain(FrontendNode("GET", "/vendors"), orphans);
        Assert.DoesNotContain(FrontendNode("POST", "/vendors/42"), orphans);
        Assert.DoesNotContain(FrontendNode("GET", "/vendors/current"), orphans);
        Assert.DoesNotContain(FrontendNode("POST", "/vendors/notify/{*}"), orphans);
    }

    [Fact]
    public void Gap_ImpactAnalysisChain_HandlerToEndpointToFrontendCaller_WalksWithNoSpecialCasing()
    {
        // The full chain impact_analysis needs: Method <--HandledBy-- Endpoint <--CallsEndpoint--
        // FrontendCallSite. Both edges point "the specific thing at the general thing it depends
        // on" (mirroring HandledBy's own convention), so a plain incoming-edge walk from the
        // Method reaches the FrontendCallSite at depth 2 with zero traversal special-casing --
        // proven here directly against the graph (impact_analysis itself needs no change once
        // the edges exist; that is the point being pinned).
        var handler = GraphAssert.Node(_graph, NodeKind.Method, "Fixture.Web.VendorEndpoints.GetVendorContext()");
        var endpoint = EndpointNode("GET /api/vendors/current");
        GraphAssert.Edge(_graph, endpoint, handler, RelationshipKind.HandledBy);

        var callSite = FrontendNode("GET", "/vendors/current");
        GraphAssert.Edge(_graph, callSite, endpoint, RelationshipKind.CallsEndpoint);
    }

    [Fact]
    public void SixOutcomeTaxonomy_EveryCallSiteLandsInExactlyOneCategory()
    {
        // Every FrontendCallSite in the fixture must be classified, and the six outcomes as a
        // set must be fully reachable on this one fixture -- the taxonomy itself is exhaustive.
        Assert.Equal(6, _linkResults.Count);
        Assert.All(_linkResults, r => Assert.True(Enum.IsDefined(r.Outcome)));

        var byOutcome = _linkResults.ToLookup(r => r.Outcome);
        Assert.Single(byOutcome[CallSiteLinkOutcome.PrecedenceResolved]); // current()
        Assert.Single(byOutcome[CallSiteLinkOutcome.SetEdge]);      // notify()
        Assert.Single(byOutcome[CallSiteLinkOutcome.NoSkeletonMatch]); // bulkImport()
        Assert.Single(byOutcome[CallSiteLinkOutcome.VerbMismatch]); // removeAll()

        // update(id) is also Unique (a single parameterized endpoint) -- confirms Unique fires
        // for both literal and parameterized single-match endpoints, not just literal ones.
        Assert.Equal(2, byOutcome[CallSiteLinkOutcome.Unique].Count());
    }

    private SymbolNode FrontendNode(string verb, string template)
    {
        var matches = _graph.Nodes
            .Where(n => n.Kind == NodeKind.FrontendCallSite && n.Name == template
                && n.Fqn.StartsWith(verb + " ", StringComparison.Ordinal))
            .ToList();
        if (matches.Count == 1)
        {
            return matches[0];
        }

        throw new XunitException(
            $"Expected exactly one FrontendCallSite node for '{verb} {template}' but found {matches.Count}. "
            + $"FrontendCallSite fqns present: [{string.Join(", ", _graph.Nodes.Where(n => n.Kind == NodeKind.FrontendCallSite).Select(n => n.Fqn).OrderBy(f => f, StringComparer.Ordinal))}]");
    }

    private SymbolNode EndpointNode(string fqn) => GraphAssert.Node(_graph, NodeKind.Endpoint, fqn);

    private void AssertCallsEndpoint(SymbolNode callSite, SymbolNode endpoint) =>
        GraphAssert.Edge(_graph, callSite, endpoint, RelationshipKind.CallsEndpoint);

    private CallSiteLinkOutcome OutcomeFor(SymbolNode callSite) =>
        _linkResults.Single(r => r.CallSite.Id == callSite.Id).Outcome;

    /// <summary>Mirrors the designed find_orphan_calls query (§Q4): FrontendCallSite minus
    /// anything with an outgoing CallsEndpoint edge.</summary>
    private List<SymbolNode> ComputeOrphans() =>
        _graph.Nodes
            .Where(n => n.Kind == NodeKind.FrontendCallSite)
            .Where(n => !_graph.OutgoingEdges(n.Id, RelationshipKind.CallsEndpoint).Any())
            .ToList();
}
