using Slnmap.Core.Graph;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// Issue #5: events (both `EventFieldDeclarationSyntax` and `EventDeclarationSyntax`) are not
/// modeled as graph nodes at all -- a distinct symbol kind (IEventSymbol) from fields, but
/// mirroring the exact pre-fix state of NodeKind.Field before Gap 2. NodeKind.Event (value 13)
/// is already reserved in the enum; nothing currently maps to it. See
/// reports/issue-5-investigation.md and tests/fixtures/FixtureSolution/FixtureLib/Events.cs.
///
/// All [Fact]s below are EXPECTED TO FAIL against current main -- they assert nodes/edges that
/// should exist once the fix lands, capturing the gap (Step 3 of the investigation). This file
/// adds NO analyzer changes -- it is investigation-only, per the task's hard rule.
/// </summary>
public sealed class EventNodeGapTests : IClassFixture<AnalyzedFixtureSolution>
{
    private readonly AnalyzedFixtureSolution _fixture;

    public EventNodeGapTests(AnalyzedFixtureSolution fixture) => _fixture = fixture;

    private CodeGraph Graph => _fixture.Graph;

    [Fact]
    public void Gap_FieldStyleEvent_ShouldBeModeledAsEventNodeContainedByItsType()
    {
        var @event = GraphAssert.Node(Graph, NodeKind.Event, "Fixture.Lib.EventFieldHolder.Changed");
        var holder = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.EventFieldHolder");
        GraphAssert.Edge(Graph, holder, @event, RelationshipKind.Contains);
    }

    [Fact]
    public void Gap_MultiDeclaratorEventField_ShouldProduceTwoDistinctEventNodes()
    {
        var first = GraphAssert.Node(Graph, NodeKind.Event, "Fixture.Lib.MultiDeclaratorEventFields.First");
        var second = GraphAssert.Node(Graph, NodeKind.Event, "Fixture.Lib.MultiDeclaratorEventFields.Second");
        Assert.NotEqual(first.Id, second.Id);

        var owner = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.MultiDeclaratorEventFields");
        GraphAssert.Edge(Graph, owner, first, RelationshipKind.Contains);
        GraphAssert.Edge(Graph, owner, second, RelationshipKind.Contains);
    }

    [Fact]
    public void Gap_PropertyStyleEvent_ShouldBeModeledAsEventNodeContainedByItsType()
    {
        var @event = GraphAssert.Node(Graph, NodeKind.Event, "Fixture.Lib.PropertyStyleEventHolder.Notify");
        var holder = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.PropertyStyleEventHolder");
        GraphAssert.Edge(Graph, holder, @event, RelationshipKind.Contains);
    }

    [Fact]
    public void Gap_PropertyStyleEventAddAccessor_TypeofReference_ShouldAttributeToTheEventItself()
    {
        // Mirrors Gap 2's Field_TypeofInitializerEntries_CreateReferenceEdgesFromTheFieldItself:
        // a reference made from inside the `add` accessor body must attribute to the event node,
        // not fall back to the containing type (the concern GetEnclosingMemberNode's IFieldSymbol
        // arm addressed for fields; IEventSymbol needs the equivalent).
        var @event = GraphAssert.Node(Graph, NodeKind.Event, "Fixture.Lib.PropertyStyleEventHolder.Notify");
        var target = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.EventReferenceTargetA");
        GraphAssert.Edge(Graph, @event, target, RelationshipKind.References);

        var holder = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.PropertyStyleEventHolder");
        Assert.DoesNotContain(Graph.OutgoingEdges(holder.Id, RelationshipKind.References), e => e.TargetId == target.Id);
    }

    [Fact]
    public void Gap_PropertyStyleEventRemoveAccessor_TypeofReference_ShouldAttributeToTheEventItself()
    {
        var @event = GraphAssert.Node(Graph, NodeKind.Event, "Fixture.Lib.PropertyStyleEventHolder.Notify");
        var target = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.EventReferenceTargetB");
        GraphAssert.Edge(Graph, @event, target, RelationshipKind.References);
    }
}
