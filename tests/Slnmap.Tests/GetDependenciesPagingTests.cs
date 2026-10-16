using Slnmap.Core.Graph;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// v0.16.0: get_dependencies pages 50 at a time through an <c>offset</c> instead of truncating
/// with a flat note, so a type with 200 callers can be read in full, four calls long.
/// </summary>
public sealed class GetDependenciesPagingTests
{
    private const string HubFqn = "Fixture.Lib.Hub.Process()";

    private static CodeGraph GraphWithCallers(int callers)
    {
        var graph = new CodeGraph();
        var project = Build.Node(NodeKind.Project, "Lib", "C:/repo/Lib/Lib.csproj");
        var ns = Build.Node(NodeKind.Namespace, "Fixture.Lib");
        var hubType = Build.Node(NodeKind.Class, "Fixture.Lib.Hub", "C:/repo/Lib/Hub.cs");
        var hub = Build.Node(NodeKind.Method, HubFqn, "C:/repo/Lib/Hub.cs", 1);
        foreach (var node in new[] { project, ns, hubType, hub })
        {
            graph.AddNode(node);
        }

        Build.Edge(graph, project, ns, RelationshipKind.Contains);
        Build.Edge(graph, ns, hubType, RelationshipKind.Contains);
        Build.Edge(graph, hubType, hub, RelationshipKind.Contains);

        for (int i = 0; i < callers; i++)
        {
            var caller = Build.Node(NodeKind.Method, $"Fixture.Lib.Caller{i:D3}.Run()", $"C:/repo/Lib/Caller{i:D3}.cs", 1);
            graph.AddNode(caller);
            Build.Edge(graph, ns, caller, RelationshipKind.Contains);
            Build.Edge(graph, caller, hub, RelationshipKind.Calls);
        }

        return graph;
    }

    [Fact]
    public async Task UnderTheCap_NoPagingNoteAtAll()
    {
        await using var graph = await TestGraph.CreateAsync(GraphWithCallers(3));

        string result = await graph.Queries.GetDependenciesAsync(HubFqn, "incoming", 1);

        Assert.Contains("Calls (3):", result, StringComparison.Ordinal);
        Assert.DoesNotContain("showing", result, StringComparison.Ordinal);
        Assert.DoesNotContain("offset", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OverTheCap_FirstPageSaysHowToGetTheNext()
    {
        await using var graph = await TestGraph.CreateAsync(GraphWithCallers(60));

        string result = await graph.Queries.GetDependenciesAsync(HubFqn, "incoming", 1);

        Assert.Contains("(showing 1-50 of 60):", result, StringComparison.Ordinal);
        Assert.Contains("Calls (60):", result, StringComparison.Ordinal);
        Assert.Contains("Fixture.Lib.Caller000.Run()", result, StringComparison.Ordinal);
        Assert.Contains("Fixture.Lib.Caller049.Run()", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Fixture.Lib.Caller050.Run()", result, StringComparison.Ordinal);
        Assert.Contains("...10 more; pass offset=50 for the next page.", result, StringComparison.Ordinal);
        Assert.DoesNotContain("truncated", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OverTheCap_SecondPageListsTheRest_AndKeepsTheGroupHeader()
    {
        await using var graph = await TestGraph.CreateAsync(GraphWithCallers(60));

        string result = await graph.Queries.GetDependenciesAsync(HubFqn, "incoming", 1, offset: 50);

        Assert.Contains("(showing 51-60 of 60):", result, StringComparison.Ordinal);
        Assert.Contains("Calls (60):", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Fixture.Lib.Caller049.Run()", result, StringComparison.Ordinal);
        Assert.Contains("Fixture.Lib.Caller050.Run()", result, StringComparison.Ordinal);
        Assert.Contains("Fixture.Lib.Caller059.Run()", result, StringComparison.Ordinal);
        Assert.DoesNotContain("more; pass offset", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OffsetPastTheEnd_SaysSoAndNamesTheLastPage()
    {
        await using var graph = await TestGraph.CreateAsync(GraphWithCallers(60));

        string result = await graph.Queries.GetDependenciesAsync(HubFqn, "incoming", 1, offset: 120);

        Assert.Contains("offset 120 is past the end: 60 item(s) in total; the last page starts at offset 50.", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Caller", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NegativeOffset_IsTreatedAsZero()
    {
        await using var graph = await TestGraph.CreateAsync(GraphWithCallers(3));

        string result = await graph.Queries.GetDependenciesAsync(HubFqn, "incoming", 1, offset: -7);

        Assert.Contains("Fixture.Lib.Caller000.Run()", result, StringComparison.Ordinal);
        Assert.DoesNotContain("past the end", result, StringComparison.Ordinal);
    }
}
