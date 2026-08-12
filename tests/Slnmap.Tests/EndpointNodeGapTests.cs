using Slnmap.Core.Graph;
using Xunit;
using Xunit.Sdk;

namespace Slnmap.Tests;

/// <summary>
/// Endpoint-nodes investigation (reports/endpoint-nodes-investigation.md): HTTP endpoints
/// registered via ASP.NET Core Minimal APIs are not modeled in the graph at all — the walker
/// visits the Map* invocation but drops it because the framework Map* method is not in source
/// (SymbolFacts.IsInSource), so the route template and verb never leave the syntax tree.
///
/// Unlike the Event precedent (EventNodeGapTests), NO enum value is reserved: NodeKind has no
/// Endpoint member and RelationshipKind has no HandledBy member. These tests therefore assert
/// through kind-NAME strings (Kind.ToString()) so they compile against current main and fail at
/// runtime, pinning the designed shape:
///   - node:  kind Endpoint, name = composed route template, fqn = "VERB template"
///   - edge:  Endpoint —HandledBy→ handler Method (source = Endpoint, matching Calls orientation
///            so impact_analysis on the handler surfaces the endpoint via its incoming walk)
///   - edge:  registering type —Contains→ Endpoint (viz containment; eviction file-scoping)
///
/// All Gap_* [Fact]s below are EXPECTED TO FAIL against current main. Sanity_* passes today and
/// proves the FixtureWeb project itself loads and analyzes (so Gap failures are attributable to
/// the missing feature, not a broken fixture). This file adds NO analyzer changes —
/// investigation-only, per the task's hard rule.
/// Fixture: tests/fixtures/FixtureSolution/FixtureWeb/.
/// </summary>
public sealed class EndpointNodeGapTests : IClassFixture<AnalyzedFixtureSolution>
{
    // Designed additions — referenced by NAME because the enum members do not exist yet.
    // The fix should add NodeKind.Endpoint = 14 and RelationshipKind.HandledBy = 5 (append-only:
    // viz serializes kinds positionally, so new members must take the next value, never insert).
    private const string EndpointKind = "Endpoint";
    private const string HandledByKind = "HandledBy";

    private readonly AnalyzedFixtureSolution _fixture;

    public EndpointNodeGapTests(AnalyzedFixtureSolution fixture) => _fixture = fixture;

    private CodeGraph Graph => _fixture.Graph;

    [Fact]
    public void Sanity_FixtureWebProject_IsLoadedAndAnalyzed()
    {
        // Passes on current main. Proves MSBuildWorkspace handles the Microsoft.NET.Sdk.Web
        // fixture project and its symbols land in the graph; the Gap_* failures below are then
        // the missing Endpoint model, not fixture wiring.
        GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Web.VendorEndpoints");
        GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Web.VendorEndpoints.ListVendors()");
        GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Web.VendorEndpoints.UpdateVendor(string)");
        GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Web.VendorEndpoints.ArchiveSnapshot()");
        GraphAssert.Node(Graph, NodeKind.Method, "FixtureWeb.<top-level-statements-entry-point>");
    }

    [Fact]
    public void Gap_GroupRootRoute_ComposesPrefixAndTrimsTrailingSlash()
    {
        // MapGroup("/api/vendors") + MapGet("/", ...): the effective template is the
        // concatenation, normalized to "/api/vendors" (single trailing slash trimmed; a bare
        // root route "/" stays "/").
        var endpoint = EndpointNode("GET /api/vendors");
        Assert.Equal("/api/vendors", endpoint.Name);
        AssertHandledBy(endpoint, GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Web.VendorEndpoints.ListVendors()"));
    }

    [Fact]
    public void Gap_RouteParameter_KeptVerbatimInComposedTemplate()
    {
        // Parameter placeholders are preserved as authored ({id}, {id:int}, ...) — matching
        // normalization ({*}-style skeletons) is a query-time concern, not node identity.
        var endpoint = EndpointNode("POST /api/vendors/{id}");
        AssertHandledBy(endpoint, GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Web.VendorEndpoints.UpdateVendor(string)"));
    }

    [Fact]
    public void Gap_ConstProvidedPattern_ResolvesToItsLiteralValue()
    {
        // group.MapGet(ArchivePattern, ...) with const string ArchivePattern = "/archive":
        // the semantic model's constant value resolves the template (the QuossusWebhook shape
        // in OSSUS — see the census in the report).
        EndpointNode("GET /api/vendors/archive");
    }

    [Fact]
    public void Gap_OneHandlerTwoRoutes_ProducesTwoEndpointNodesConvergingOnOneMethod()
    {
        // The duplicate-handler shape: distinct verb+template = distinct Endpoint node (identity
        // is "VERB template", not the handler); both HandledBy edges target the same Method.
        var get = EndpointNode("GET /api/vendors/archive");
        var post = EndpointNode("POST /api/vendors/archive");
        Assert.NotEqual(get.Id, post.Id);

        var handler = GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Web.VendorEndpoints.ArchiveSnapshot()");
        AssertHandledBy(get, handler);
        AssertHandledBy(post, handler);

        var incoming = Graph.IncomingEdges(handler.Id).Where(e => e.Kind.ToString() == HandledByKind).ToList();
        Assert.Equal(2, incoming.Count);
    }

    [Fact]
    public void Gap_TopLevelStatementRegistration_ProducesEndpointNode()
    {
        // app.MapGet("/health", VendorEndpoints.Ping) in Program.cs top-level statements: the
        // walker already visits this invocation (Gap-3 finding) — the endpoint must be modeled
        // there exactly as inside a method body.
        var endpoint = EndpointNode("GET /health");
        AssertHandledBy(endpoint, GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Web.VendorEndpoints.Ping()"));
    }

    [Fact]
    public void Gap_Endpoint_IsContainedByTheRegisteringType()
    {
        // Containment gives viz a real parent (otherwise Endpoint nodes land in the synthetic
        // "(unattributed)" root) and anchors the node to a file for incremental eviction.
        var endpoint = EndpointNode("GET /api/vendors");
        var registrar = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Web.VendorEndpoints");
        GraphAssert.Edge(Graph, registrar, endpoint, RelationshipKind.Contains);
    }

    private SymbolNode EndpointNode(string fqn)
    {
        var matches = Graph.Nodes
            .Where(n => n.Kind.ToString() == EndpointKind && n.Fqn == fqn)
            .ToList();
        if (matches.Count == 1)
        {
            return matches[0];
        }

        var present = Graph.Nodes
            .Where(n => n.Kind.ToString() == EndpointKind)
            .Select(n => n.Fqn)
            .OrderBy(f => f, StringComparer.Ordinal);
        throw new XunitException(
            $"Expected exactly one {EndpointKind} node with FQN '{fqn}' but found {matches.Count}. " +
            $"{EndpointKind}-kind nodes present: [{string.Join(", ", present)}] " +
            $"(none is expected on current main: NodeKind has no {EndpointKind} member yet).");
    }

    private void AssertHandledBy(SymbolNode endpoint, SymbolNode handler)
    {
        if (!Graph.OutgoingEdges(endpoint.Id).Any(e => e.Kind.ToString() == HandledByKind && e.TargetId == handler.Id))
        {
            var outgoing = Graph.OutgoingEdges(endpoint.Id)
                .Select(e => $"{e.Kind}→{(Graph.TryGetNode(e.TargetId, out var n) ? n.Fqn : e.TargetId)}");
            throw new XunitException(
                $"Expected edge {endpoint.Fqn} —{HandledByKind}→ {handler.Fqn}. " +
                $"Outgoing edges of the endpoint: [{string.Join(", ", outgoing)}].");
        }
    }
}
