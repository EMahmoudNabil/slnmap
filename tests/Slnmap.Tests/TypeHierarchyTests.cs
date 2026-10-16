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

    [Fact]
    public async Task Up_ExternalBaseOnly_DistinguishesFromNoBaseAtAll()
    {
        // A type with member_flags="external-base" (e.g. `: IPipelineBehavior<TRequest,TResponse>`)
        // DOES have a base — just not one the graph has a node for. Reporting the same flat
        // "(no base types or interfaces)" as a type with genuinely no base misleads a reader into
        // thinking the type is a root type, when the real story is "check the source, there's an
        // external base here."
        await using var graph = await TestGraph.CreateAsync(Build.TypeWithExternalBaseOnly());

        string result = await graph.Queries.GetTypeHierarchyAsync("Fixture.Lib.Behaviour", "up", 5);

        Assert.DoesNotContain("(no base types or interfaces)", result, StringComparison.Ordinal);
        Assert.Contains("external", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Up_NoBaseAtAll_StillReportsThePlainMessage()
    {
        // Regression guard: a type with neither a modeled base NOR the external-base flag (IShape
        // in the Shapes fixture has no Inherits/Implements edges of its own and no member_flags)
        // must keep the original, unqualified message — only the external-base case gets the more
        // specific one.
        await using var graph = await TestGraph.CreateAsync(Build.Shapes());

        string result = await graph.Queries.GetTypeHierarchyAsync("Fixture.Lib.IShape", "up", 5);

        Assert.Contains("(no base types or interfaces)", result, StringComparison.Ordinal);
    }
}
