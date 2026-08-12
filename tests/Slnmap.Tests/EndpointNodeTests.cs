using Slnmap.Core.Graph;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// Tier-2 endpoint-extraction tests (the OSSUS-convention shapes the gap fixture deliberately left
/// out — reports/endpoint-nodes-investigation.md §3.1): the guarded GetType().Name prefix fold, the
/// reversed-argument in-source forwarder, omitted and string.Empty patterns, route constraints kept
/// verbatim, multi-line chained registrations, nested groups — and the count-never-guess side:
/// every refused registration produces no node and increments the unresolved counter.
/// Fixtures: ConventionInfrastructure.cs / ReminderEndpoints.cs / UnresolvedRegistrations.cs.
/// </summary>
public sealed class EndpointNodeTests : IClassFixture<AnalyzedFixtureSolution>
{
    private readonly AnalyzedFixtureSolution _fixture;

    public EndpointNodeTests(AnalyzedFixtureSolution fixture) => _fixture = fixture;

    private CodeGraph Graph => _fixture.Graph;

    [Fact]
    public void ConventionPrefix_LeafGroup_FoldsToDeclaredClassName()
    {
        // app.MapGroup(this) → $"/api/{group.GetType().Name}" with a sealed receiver: the fold is
        // sound and the omitted-pattern registration composes to the bare group root.
        var endpoint = GraphAssert.Node(Graph, NodeKind.Endpoint, "GET /api/Reminders");
        Assert.Equal("/api/Reminders", endpoint.Name);
        GraphAssert.Edge(
            Graph,
            endpoint,
            GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Web.Reminders.GetAll()"),
            RelationshipKind.HandledBy);
    }

    [Fact]
    public void StringEmptyPattern_TreatedAsEmptyString()
    {
        // string.Empty is a static readonly field, not a compile-time constant — the well-known
        // member is special-cased rather than sent through GetConstantValue.
        var endpoint = GraphAssert.Node(Graph, NodeKind.Endpoint, "POST /api/Reminders");
        GraphAssert.Edge(
            Graph,
            endpoint,
            GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Web.Reminders.Create()"),
            RelationshipKind.HandledBy);
    }

    [Fact]
    public void ReversedArgumentOrder_ConstraintKeptVerbatim()
    {
        // Custom MapGet(handler, pattern): overload resolution assigns the roles; ":int" stays as
        // authored — normalization is a query concern, never identity.
        var endpoint = GraphAssert.Node(Graph, NodeKind.Endpoint, "GET /api/Reminders/{id:int}");
        GraphAssert.Edge(
            Graph,
            endpoint,
            GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Web.Reminders.GetById(int)"),
            RelationshipKind.HandledBy);
    }

    [Fact]
    public void MultiLineChainedRegistration_Extracts()
    {
        GraphAssert.Node(Graph, NodeKind.Endpoint, "PUT /api/Reminders/{id:int}/details");
    }

    [Fact]
    public void NestedGroup_RecursesThroughTheConventionFold()
    {
        GraphAssert.Node(Graph, NodeKind.Endpoint, "DELETE /api/Reminders/archive/{id:int}");
    }

    [Fact]
    public void ConventionEndpoints_AreContainedByTheRegisteringGroupClass()
    {
        var registrar = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Web.Reminders");
        GraphAssert.Edge(Graph, registrar, GraphAssert.Node(Graph, NodeKind.Endpoint, "GET /api/Reminders"), RelationshipKind.Contains);
        GraphAssert.Edge(Graph, registrar, GraphAssert.Node(Graph, NodeKind.Endpoint, "DELETE /api/Reminders/archive/{id:int}"), RelationshipKind.Contains);
    }

    [Fact]
    public void NonLeafGroup_IsRefused_NoNodeEmitted()
    {
        // NonLeafHooks has an in-solution subclass: GetType().Name may not equal the static name,
        // so the guard refuses. No node under any name it could have taken — and no guessed one.
        Assert.DoesNotContain(
            Graph.Nodes,
            n => n.Kind == NodeKind.Endpoint && (n.Fqn.Contains("ping", StringComparison.OrdinalIgnoreCase)
                || n.Fqn.Contains("Hooks", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public void NonConstantPattern_IsRefused_NoNodeEmitted()
    {
        // static readonly (not const) EchoPattern: GetConstantValue fails by design.
        Assert.DoesNotContain(
            Graph.Nodes,
            n => n.Kind == NodeKind.Endpoint && n.Fqn.Contains("echo", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnresolvedRegistrations_AreCounted_NotGuessed()
    {
        // Exactly the three designed refusals: NonLeafHooks' "ping" (non-leaf GetType().Name),
        // EchoPattern (non-constant), and RegisterPing's body (pattern is a wrapper's parameter).
        Assert.Equal(3, _fixture.Snapshot.Stats.UnresolvedEndpoints);
    }

    [Fact]
    public void LambdaHandler_EndpointEmittedWithoutHandledBy()
    {
        var endpoint = GraphAssert.Node(Graph, NodeKind.Endpoint, "GET /inline");
        Assert.Empty(Graph.OutgoingEdges(endpoint.Id, RelationshipKind.HandledBy));
    }

    [Fact]
    public void ForwarderBodies_AreNotRegistrations()
    {
        // The custom Map* extensions' own bodies call the framework Map* with their parameters —
        // those are folded at their call sites, never modeled (or counted) as registrations.
        Assert.DoesNotContain(
            Graph.Nodes,
            n => n.Kind == NodeKind.Endpoint && n.FilePath is { } file
                && file.EndsWith("ConventionInfrastructure.cs", StringComparison.Ordinal));
    }

    [Fact]
    public void EndpointNode_CarriesTheRegistrationFileAndSpan()
    {
        // Proviso 1 of the incremental design: a null FilePath would make the node immortal
        // (never evicted). Every endpoint must carry its registration site.
        var endpoints = Graph.Nodes.Where(n => n.Kind == NodeKind.Endpoint).ToList();
        Assert.NotEmpty(endpoints);
        Assert.All(endpoints, e =>
        {
            Assert.NotNull(e.FilePath);
            Assert.NotNull(e.Span);
        });
    }

    [Fact]
    public void DuplicateRegistration_KeepsFirstSeenCallSite()
    {
        // Two registrations of GET+POST /api/vendors/archive share one handler; identity is
        // "VERB template", so the two verbs stay distinct nodes (pinned by the gap tests) and
        // each node's span points at a real registration, inside the declaring file.
        var get = GraphAssert.Node(Graph, NodeKind.Endpoint, "GET /api/vendors/archive");
        Assert.EndsWith("VendorEndpoints.cs", get.FilePath!, StringComparison.Ordinal);
    }
}
