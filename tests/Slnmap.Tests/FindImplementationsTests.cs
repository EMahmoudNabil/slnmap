using Slnmap.Core.Graph;
using Xunit;

namespace Slnmap.Tests;

public sealed class FindImplementationsTests
{
    [Fact]
    public async Task Interface_ListsImplementersAndDerivedTypes()
    {
        await using var graph = await TestGraph.CreateAsync(Build.Shapes());

        string result = await graph.Queries.FindImplementationsAsync("Fixture.Lib.IShape");

        Assert.Contains("3 implementation(s)", result, StringComparison.Ordinal);
        Assert.Contains("Fixture.Lib.ShapeBase", result, StringComparison.Ordinal);   // direct: Implements
        Assert.Contains("Fixture.Lib.Circle", result, StringComparison.Ordinal);      // transitive: Inherits
        Assert.Contains("Fixture.Lib.Square", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InterfaceMember_ListsOverridingMembers()
    {
        await using var graph = await TestGraph.CreateAsync(Build.Shapes());

        string result = await graph.Queries.FindImplementationsAsync("Fixture.Lib.IShape.Area()");

        Assert.Contains("Fixture.Lib.ShapeBase.Area()", result, StringComparison.Ordinal);
        Assert.Contains("Fixture.Lib.Circle.Area()", result, StringComparison.Ordinal);
        Assert.Contains("Fixture.Lib.Square.Area()", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TypeWithNoImplementers_ReportsZeroGracefully()
    {
        await using var graph = await TestGraph.CreateAsync(Build.Shapes());

        string result = await graph.Queries.FindImplementationsAsync("Fixture.Lib.Circle");

        Assert.Contains("0 implementations", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownFqn_ReturnsSuggestions()
    {
        await using var graph = await TestGraph.CreateAsync(Build.Shapes());

        string result = await graph.Queries.FindImplementationsAsync("Fixture.Lib.IShap");

        Assert.Contains("No symbol with FQN", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OverCap_ReportsPlusAndRefine()
    {
        var g = new CodeGraph();
        var project = Build.Node(NodeKind.Project, "Big", "C:/repo/Big/Big.csproj");
        var ns = Build.Node(NodeKind.Namespace, "Big");
        var iface = Build.Node(NodeKind.Interface, "Big.IThing", "C:/repo/Big/IThing.cs");
        g.AddNode(project);
        g.AddNode(ns);
        g.AddNode(iface);
        Build.Edge(g, project, ns, RelationshipKind.Contains);
        Build.Edge(g, ns, iface, RelationshipKind.Contains);
        for (int i = 0; i < 150; i++) // > ImplementationsCap (100)
        {
            var impl = Build.Node(NodeKind.Class, $"Big.Impl{i:D3}", "C:/repo/Big/Impl.cs", i);
            g.AddNode(impl);
            Build.Edge(g, ns, impl, RelationshipKind.Contains);
            Build.Edge(g, impl, iface, RelationshipKind.Implements);
        }

        await using var graph = await TestGraph.CreateAsync(g);
        string result = await graph.Queries.FindImplementationsAsync("Big.IThing");

        Assert.Contains("100+ implementation(s)", result, StringComparison.Ordinal);
        Assert.Contains("refine", result, StringComparison.Ordinal);
    }
}
