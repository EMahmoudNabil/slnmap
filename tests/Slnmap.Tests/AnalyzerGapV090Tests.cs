using Slnmap.Core.Graph;
using Xunit;
using Xunit.Sdk;

namespace Slnmap.Tests;

/// <summary>
/// The v0.9.0 analyzer-gap bundle, pinned by failing tests before implementation (the house
/// pattern): #13 enum members as first-class nodes (a NEW NodeKind, referenced by string below
/// because the enum member does not exist yet), #8 event subscription/raising usage edges,
/// #9 generic type arguments to EXTERNAL generic methods (the issue's own repro is flagged as
/// reconstructed-and-unverified — its test doubles as the verification probe), and #11 typeof()
/// inside assembly-level attributes attributed to the PROJECT node (the assembly IS the project).
/// Fixtures: VendorActivity.cs (existing enum), EventSubscribers.cs, FixtureMiddleware.cs +
/// FixtureWeb/Program.cs, AssemblyMarker.cs.
/// </summary>
public sealed class AnalyzerGapV090Tests : IClassFixture<AnalyzedFixtureSolution>
{
    private readonly AnalyzedFixtureSolution _fixture;

    public AnalyzerGapV090Tests(AnalyzedFixtureSolution fixture) => _fixture = fixture;

    private CodeGraph Graph => _fixture.Graph;

    // ---- #13: enum members as nodes ---------------------------------------------------------

    [Fact]
    public void Gap13_EveryEnumMember_HasANode_ReferencedOrNot()
    {
        // The v0.6.1 experiment's exact failure was census inconsistency: only REFERENCED members
        // materialized. Active is deliberately never referenced anywhere in the fixture.
        EnumMemberNode("Fixture.Lib.VendorState.Active");
        EnumMemberNode("Fixture.Lib.VendorState.Deactivated");
    }

    [Fact]
    public void Gap13_EnumMemberUsage_ProducesAReferencesEdge_ToTheMemberItself()
    {
        var source = GraphAssert.Node(Graph, NodeKind.Property, "Fixture.Lib.VendorStateReader.Current");
        var member = EnumMemberNode("Fixture.Lib.VendorState.Deactivated");
        GraphAssert.Edge(Graph, source, member, RelationshipKind.References);
    }

    [Fact]
    public void Gap13_EnumMembers_AreContainedByTheirEnumType()
    {
        var enumType = GraphAssert.Node(Graph, NodeKind.Enum, "Fixture.Lib.VendorState");
        var member = EnumMemberNode("Fixture.Lib.VendorState.Active");
        GraphAssert.Edge(Graph, enumType, member, RelationshipKind.Contains);
    }

    [Fact]
    public void Gap13_EnumMembers_AreNotMislabeledAsFields()
    {
        // The kind decision: a dedicated EnumMember kind — enumerants are values of a closed set,
        // not mutable state; labeling them Field would pollute every Field census.
        Assert.DoesNotContain(
            Graph.Nodes,
            n => n.Kind == NodeKind.Field && n.Fqn.StartsWith("Fixture.Lib.VendorState.", StringComparison.Ordinal));
    }

    // ---- #8: event usage edges --------------------------------------------------------------

    [Fact]
    public void Gap8_EventSubscription_FromAnotherClass_ProducesUsageEdges()
    {
        var changed = GraphAssert.Node(Graph, NodeKind.Event, "Fixture.Lib.EventFieldHolder.Changed");

        // += inside the constructor, -= inside Detach() — both are usage (the eShopOnWeb shape).
        var subscriber = GraphAssert.Node(Graph, NodeKind.Constructor, "Fixture.Lib.EventSubscriber.EventSubscriber(Fixture.Lib.EventFieldHolder)");
        GraphAssert.Edge(Graph, subscriber, changed, RelationshipKind.References);

        var detach = GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Lib.EventSubscriber.Detach()");
        GraphAssert.Edge(Graph, detach, changed, RelationshipKind.References);
    }

    [Fact]
    public void Gap8_EventRaising_ProducesAUsageEdge()
    {
        // Changed?.Invoke(...) inside the declaring class's Raise(): the event name is a real
        // reference (the .Invoke itself stays unmodeled — DelegateInvoke is deliberately skipped).
        var changed = GraphAssert.Node(Graph, NodeKind.Event, "Fixture.Lib.EventFieldHolder.Changed");
        var raiser = GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Lib.EventFieldHolder.Raise(Fixture.Lib.EventArgsPayload)");
        GraphAssert.Edge(Graph, raiser, changed, RelationshipKind.References);
    }

    [Fact]
    public void Gap8_MultiDeclaratorEvents_EachGetTheirOwnRaisingEdge()
    {
        var raiser = GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Lib.MultiDeclaratorEventFields.RaiseBoth()");
        GraphAssert.Edge(Graph, raiser, GraphAssert.Node(Graph, NodeKind.Event, "Fixture.Lib.MultiDeclaratorEventFields.First"), RelationshipKind.References);
        GraphAssert.Edge(Graph, raiser, GraphAssert.Node(Graph, NodeKind.Event, "Fixture.Lib.MultiDeclaratorEventFields.Second"), RelationshipKind.References);
    }

    // ---- #9: generic type argument to an EXTERNAL generic method (verification probe) --------

    [Fact]
    public void Gap9_GenericArgumentToExternalMethod_ProducesAUsageEdge()
    {
        // app.UseMiddleware<FixtureMiddleware>() in FixtureWeb's top-level statements: the
        // framework method is external, the type argument is first-party.
        var middleware = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Web.FixtureMiddleware");
        var entryPoint = GraphAssert.Node(Graph, NodeKind.Method, "FixtureWeb.<top-level-statements-entry-point>");
        GraphAssert.Edge(Graph, entryPoint, middleware, RelationshipKind.References);
    }

    // ---- #11: assembly-level attribute typeof() ----------------------------------------------

    [Fact]
    public void Gap11_AssemblyAttributeTypeof_AttributesToTheProjectNode()
    {
        // [assembly: AssemblyMarker(typeof(Fixture.Lib.Circle))] has no containing member — the
        // assembly IS the project, so the References edge sources from the project node.
        var circle = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.Circle");
        var project = GraphAssert.Node(Graph, NodeKind.Project, "FixtureLib");
        GraphAssert.Edge(Graph, project, circle, RelationshipKind.References);
    }

    private SymbolNode EnumMemberNode(string fqn)
    {
        var matches = Graph.Nodes
            .Where(n => n.Kind.ToString() == "EnumMember" && n.Fqn == fqn)
            .ToList();
        if (matches.Count == 1)
        {
            return matches[0];
        }

        var present = Graph.Nodes
            .Where(n => n.Kind.ToString() == "EnumMember")
            .Select(n => n.Fqn)
            .OrderBy(f => f, StringComparer.Ordinal);
        throw new XunitException(
            $"Expected exactly one EnumMember node with FQN '{fqn}' but found {matches.Count}. " +
            $"EnumMember-kind nodes present: [{string.Join(", ", present)}] " +
            "(none is expected on current main: NodeKind has no EnumMember member yet).");
    }
}
