using Slnmap.Core.Graph;
using Xunit;
using Xunit.Sdk;

namespace Slnmap.Tests;

/// <summary>
/// Controller-endpoints investigation (v1.1, reports/controller-endpoints-investigation.md):
/// attribute-routed controllers ([Route]/[HttpGet]/...) produce no Endpoint nodes — v0.7.0's
/// extractor triggers only on Map* invocations, and controller routes live in attributes the
/// walker never reads. Unlike the Minimal-API gap, the graph vocabulary already exists
/// (NodeKind.Endpoint, RelationshipKind.HandledBy) — the designed fix is purely a second
/// extractor over the SEMANTIC model's attribute data (ISymbol.GetAttributes; no AttributeSyntax
/// visiting), emitting the same node/edge shape:
///   fqn = "VERB template" composed per ASP.NET's own rules — class-level [Route] (+ inherited),
///   [controller]/[action] tokens substituted (Async suffix stripped), action templates appended
///   unless absolute ("/..."), verb from the Http* attribute.
///
/// All Gap_* [Fact]s below are EXPECTED TO FAIL against current main. Sanity_* passes today and
/// proves the fixture controllers compile and their symbols land in the graph. This file adds NO
/// analyzer changes — investigation-only.
/// Fixtures: tests/fixtures/FixtureSolution/FixtureWeb/{StatusController,LegacyControllers}.cs.
/// </summary>
public sealed class ControllerEndpointGapTests : IClassFixture<AnalyzedFixtureSolution>
{
    private readonly AnalyzedFixtureSolution _fixture;

    public ControllerEndpointGapTests(AnalyzedFixtureSolution fixture) => _fixture = fixture;

    private CodeGraph Graph => _fixture.Graph;

    [Fact]
    public void Sanity_ControllerFixtures_AreLoadedAndAnalyzed()
    {
        // Passes on current main: the controller classes and their action methods are ordinary
        // symbols; only the Endpoint extraction is missing.
        GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Web.StatusController");
        GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Web.StatusController.GetStatus()");
        GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Web.StatusController.GetHistory(int)");
        GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Web.ReportsController");
        GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Web.ReportsController.RebuildAsync()");
    }

    [Fact]
    public void Gap_BareVerbAttribute_UsesTheClassTemplate()
    {
        // [Route("api/[controller]")] + [HttpGet] with no template: the class template alone,
        // [controller] = class name minus the Controller suffix.
        var endpoint = EndpointNode("GET /api/Status");
        AssertHandledBy(endpoint, GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Web.StatusController.GetStatus()"));
    }

    [Fact]
    public void Gap_VerbAttributeTemplate_AppendsToTheClassTemplate()
    {
        // [HttpGet("history/{days:int}")]: appended, constraint kept verbatim (identity is the
        // authored declaration, exactly as for Minimal-API endpoints).
        var endpoint = EndpointNode("GET /api/Status/history/{days:int}");
        AssertHandledBy(endpoint, GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Web.StatusController.GetHistory(int)"));
    }

    [Fact]
    public void Gap_RoutePlusVerbAttributes_CombineOnOneAction()
    {
        // [Route("reset")] supplies the template, [HttpPost] the verb (the eShopOnWeb
        // UserController.Logout shape).
        var endpoint = EndpointNode("POST /api/Status/reset");
        AssertHandledBy(endpoint, GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Web.StatusController.Reset()"));
    }

    [Fact]
    public void Gap_AbsoluteActionTemplate_OverridesTheClassTemplate()
    {
        // A leading "/" makes the action template absolute — ASP.NET ignores the class prefix.
        var endpoint = EndpointNode("DELETE /maintenance/purge");
        AssertHandledBy(endpoint, GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Web.StatusController.Purge()"));
    }

    [Fact]
    public void Gap_ControllerAndActionTokens_SubstituteAndStripAsyncSuffix()
    {
        // [Route("[controller]/[action]")] (the dominant eShopOnWeb shape): [controller] strips
        // the Controller suffix, [action] is the method name with a trailing Async stripped —
        // MVC's own action-name convention.
        var monthly = EndpointNode("GET /Reports/Monthly");
        AssertHandledBy(monthly, GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Web.ReportsController.Monthly()"));

        var rebuild = EndpointNode("POST /Reports/Rebuild");
        AssertHandledBy(rebuild, GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Web.ReportsController.RebuildAsync()"));
    }

    [Fact]
    public void Gap_InheritedClassRoute_ResolvesTokensAgainstTheDerivedType()
    {
        // InheritedRouteController declares no [Route]; it inherits the abstract base's
        // [Route("api/[controller]")] and the [controller] token binds to the DERIVED class name.
        var endpoint = EndpointNode("GET /api/InheritedRoute/own");
        AssertHandledBy(endpoint, GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Web.InheritedRouteController.Own()"));
    }

    [Fact]
    public void Gap_ControllerEndpoint_IsContainedByItsControllerType()
    {
        var endpoint = EndpointNode("GET /api/Status");
        var controller = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Web.StatusController");
        GraphAssert.Edge(Graph, controller, endpoint, RelationshipKind.Contains);
    }

    [Fact]
    public void Gap_RefusalShapes_ProduceNoEndpointNodes()
    {
        // The deterministic-or-declared side, pinned so the implementation cannot silently guess:
        // [NonAction] is not an action; a verb-less [Route] action matches every verb (counted,
        // not guessed); an abstract controller's declared action routes depend on the runtime
        // type (counted); a conventionally-routed controller (no route attributes) is a different
        // routing system (ignored, out of scope).
        Assert.DoesNotContain(
            Graph.Nodes,
            n => n.Kind == NodeKind.Endpoint
                && (n.Fqn.Contains("Helper", StringComparison.OrdinalIgnoreCase)
                    || n.Fqn.Contains("ping", StringComparison.OrdinalIgnoreCase)
                    || n.Fqn.Contains("SharedBase", StringComparison.OrdinalIgnoreCase)
                    || n.Fqn.Contains("LegacyPages", StringComparison.OrdinalIgnoreCase)));

        // The abstract base's action surfaces only through the derived type's route (asserted in
        // Gap_InheritedClassRoute...), never as "GET /api/SharedBase/shared".
    }

    private SymbolNode EndpointNode(string fqn)
    {
        var matches = Graph.Nodes
            .Where(n => n.Kind == NodeKind.Endpoint && n.Fqn == fqn)
            .ToList();
        if (matches.Count == 1)
        {
            return matches[0];
        }

        var present = Graph.Nodes
            .Where(n => n.Kind == NodeKind.Endpoint)
            .Select(n => n.Fqn)
            .OrderBy(f => f, StringComparer.Ordinal);
        throw new XunitException(
            $"Expected exactly one Endpoint node with FQN '{fqn}' but found {matches.Count}. " +
            $"Endpoint-kind nodes present: [{string.Join(", ", present)}] " +
            "(on current main only Minimal-API endpoints exist: controller attributes are never read).");
    }

    private void AssertHandledBy(SymbolNode endpoint, SymbolNode handler)
    {
        if (!Graph.OutgoingEdges(endpoint.Id).Any(e => e.Kind == RelationshipKind.HandledBy && e.TargetId == handler.Id))
        {
            var outgoing = Graph.OutgoingEdges(endpoint.Id)
                .Select(e => $"{e.Kind}→{(Graph.TryGetNode(e.TargetId, out var n) ? n.Fqn : e.TargetId)}");
            throw new XunitException(
                $"Expected edge {endpoint.Fqn} —HandledBy→ {handler.Fqn}. " +
                $"Outgoing edges of the endpoint: [{string.Join(", ", outgoing)}].");
        }
    }
}
