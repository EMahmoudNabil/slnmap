using Xunit;

namespace Slnmap.Tests;

public sealed class TypeHierarchyTests
{
    [Fact]
    public async Task Down_ShowsDerivedTypes()
    {
        await using var graph = await TestGraph.CreateAsync(Build.Shapes());

        string result = await graph.Queries.GetTypeHierarchyAsync("Fixture.Lib.ShapeBase", "down", 5);

        Assert.Contains("(down, depth<=5)", result, StringComparison.Ordinal);
        Assert.Contains("Circle", result, StringComparison.Ordinal);
        Assert.Contains("Square", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Up_ShowsBaseTypeAndInterfaceTransitively()
    {
        await using var graph = await TestGraph.CreateAsync(Build.Shapes());

        string result = await graph.Queries.GetTypeHierarchyAsync("Fixture.Lib.Circle", "up", 5);

        Assert.Contains("ShapeBase", result, StringComparison.Ordinal); // direct base
        Assert.Contains("IShape", result, StringComparison.Ordinal);    // transitive interface of the base
    }

    [Fact]
    public async Task InvalidDirection_ReturnsActionableMessage()
    {
        await using var graph = await TestGraph.CreateAsync(Build.Shapes());

        string result = await graph.Queries.GetTypeHierarchyAsync("Fixture.Lib.Circle", "sideways", 5);

        Assert.Contains("direction must be", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeafType_Down_ReportsNoDerivedTypes()
    {
        await using var graph = await TestGraph.CreateAsync(Build.Shapes());

        string result = await graph.Queries.GetTypeHierarchyAsync("Fixture.Lib.Circle", "down", 5);

        Assert.Contains("no derived types or implementers", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownFqn_ReturnsNotFound()
    {
        await using var graph = await TestGraph.CreateAsync(Build.Shapes());

        string result = await graph.Queries.GetTypeHierarchyAsync("Fixture.Lib.Nope", "both", 5);

        Assert.Contains("No symbol with FQN", result, StringComparison.Ordinal);
    }
}
