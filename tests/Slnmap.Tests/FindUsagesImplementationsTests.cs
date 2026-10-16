using Slnmap.Core.Graph;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// v0.16.0: find_usages gains an opt-in <c>includeImplementations</c>. Off, it answers "who uses
/// this symbol" exactly as before; on, it also counts usages of what implements or overrides the
/// symbol, and says which implementation each such usage came through.
/// </summary>
public sealed class FindUsagesImplementationsTests
{
    /// <summary>Shapes, plus a consumer that calls the interface member and one that calls Circle's override.</summary>
    private static CodeGraph ShapesWithConsumers()
    {
        var graph = Build.Shapes();
        var ns = graph.Nodes.Single(n => n.Fqn == "Fixture.Lib");
        var ifaceArea = graph.Nodes.Single(n => n.Fqn == "Fixture.Lib.IShape.Area()");
        var circleArea = graph.Nodes.Single(n => n.Fqn == "Fixture.Lib.Circle.Area()");
        var circle = graph.Nodes.Single(n => n.Fqn == "Fixture.Lib.Circle");

        var consumer = Build.Node(NodeKind.Class, "Fixture.Lib.Consumer", "C:/repo/Lib/Consumer.cs");
        var viaInterface = Build.Node(NodeKind.Method, "Fixture.Lib.Consumer.ViaInterface()", "C:/repo/Lib/Consumer.cs", 1);
        var viaCircle = Build.Node(NodeKind.Method, "Fixture.Lib.Consumer.ViaCircle()", "C:/repo/Lib/Consumer.cs", 2);
        var holdsCircle = Build.Node(NodeKind.Field, "Fixture.Lib.Consumer._circle", "C:/repo/Lib/Consumer.cs", 3);
        foreach (var node in new[] { consumer, viaInterface, viaCircle, holdsCircle })
        {
            graph.AddNode(node);
        }

        Build.Edge(graph, ns, consumer, RelationshipKind.Contains);
        Build.Edge(graph, consumer, viaInterface, RelationshipKind.Contains);
        Build.Edge(graph, consumer, viaCircle, RelationshipKind.Contains);
        Build.Edge(graph, consumer, holdsCircle, RelationshipKind.Contains);
        Build.Edge(graph, viaInterface, ifaceArea, RelationshipKind.Calls);
        Build.Edge(graph, viaCircle, circleArea, RelationshipKind.Calls);
        Build.Edge(graph, holdsCircle, circle, RelationshipKind.References);
        return graph;
    }

    [Fact]
    public async Task Default_CountsOnlyDirectUsages_AsBefore()
    {
        await using var graph = await TestGraph.CreateAsync(ShapesWithConsumers());

        string result = await graph.Queries.FindUsagesAsync("Fixture.Lib.IShape.Area()");

        Assert.StartsWith("1 usage(s) of Fixture.Lib.IShape.Area() (by containing member, up to 50):", result, StringComparison.Ordinal);
        Assert.Contains("Fixture.Lib.Consumer.ViaInterface()", result, StringComparison.Ordinal);
        Assert.DoesNotContain("ViaCircle", result, StringComparison.Ordinal);
        Assert.DoesNotContain("implementation", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InterfaceMember_WithImplementations_AddsCallersOfTheOverrides_AndSaysThroughWhich()
    {
        await using var graph = await TestGraph.CreateAsync(ShapesWithConsumers());

        string result = await graph.Queries.FindUsagesAsync("Fixture.Lib.IShape.Area()", includeImplementations: true);

        // ShapeBase.Area(), Circle.Area() and Square.Area() all match IShape.Area() through the closure.
        Assert.Contains("2 usage(s) of Fixture.Lib.IShape.Area()", result, StringComparison.Ordinal);
        Assert.Contains("including 3 implementation(s):", result, StringComparison.Ordinal);
        Assert.Contains("Fixture.Lib.Circle.Area()", result, StringComparison.Ordinal);
        string viaCircle = Line(result, "Fixture.Lib.Consumer.ViaCircle()");
        Assert.EndsWith(" via Fixture.Lib.Circle.Area()", viaCircle, StringComparison.Ordinal);
        // A direct usage is not attributed to an implementation.
        string viaInterface = Line(result, "Fixture.Lib.Consumer.ViaInterface()");
        Assert.DoesNotContain(" via ", viaInterface, StringComparison.Ordinal);
    }

    private static string Line(string text, string containing) =>
        Assert.Single(text.Split('\n').Select(static l => l.TrimEnd('\r')), l => l.Contains(containing, StringComparison.Ordinal));

    [Fact]
    public async Task InterfaceType_WithImplementations_AddsReferencesToTheImplementingTypes()
    {
        await using var graph = await TestGraph.CreateAsync(ShapesWithConsumers());

        string withoutImplementations = await graph.Queries.FindUsagesAsync("Fixture.Lib.IShape");
        string withImplementations = await graph.Queries.FindUsagesAsync("Fixture.Lib.IShape", includeImplementations: true);

        Assert.StartsWith("No usages found for Fixture.Lib.IShape.", withoutImplementations, StringComparison.Ordinal);
        Assert.Contains("1 usage(s) of Fixture.Lib.IShape", withImplementations, StringComparison.Ordinal);
        Assert.Contains("including 3 implementation(s): ", withImplementations, StringComparison.Ordinal);
        Assert.EndsWith(" via Fixture.Lib.Circle", Line(withImplementations, "Fixture.Lib.Consumer._circle"), StringComparison.Ordinal);
        // The implementing types themselves (ShapeBase implements IShape) are not usages.
        Assert.DoesNotContain("[Class] Fixture.Lib.ShapeBase", withImplementations, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SymbolWithNothingImplementingIt_SaysSo()
    {
        await using var graph = await TestGraph.CreateAsync(ShapesWithConsumers());

        string result = await graph.Queries.FindUsagesAsync("Fixture.Lib.Circle.Area()", includeImplementations: true);

        Assert.Contains("1 usage(s) of Fixture.Lib.Circle.Area()", result, StringComparison.Ordinal);
        Assert.Contains("; no implementations found):", result, StringComparison.Ordinal);
        Assert.Contains("Fixture.Lib.Consumer.ViaCircle()", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoUsagesEvenWithImplementations_NamesThemInTheMessage()
    {
        await using var graph = await TestGraph.CreateAsync(Build.Shapes());

        string result = await graph.Queries.FindUsagesAsync("Fixture.Lib.IShape.Area()", includeImplementations: true);

        Assert.StartsWith("No usages found for Fixture.Lib.IShape.Area(); including 3 implementation(s):", result, StringComparison.Ordinal);
    }
}
