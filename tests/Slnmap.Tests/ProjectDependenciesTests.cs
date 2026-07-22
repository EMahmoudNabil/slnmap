using Xunit;

namespace Slnmap.Tests;

public sealed class ProjectDependenciesTests
{
    [Fact]
    public async Task All_ShowsCrossProjectEdgesAndHotspot()
    {
        await using var graph = await TestGraph.CreateAsync(Build.WebAndCore());

        string result = await graph.Queries.GetProjectDependenciesAsync("all");

        Assert.Contains("Web -> Core (2)", result, StringComparison.Ordinal); // Calls + References
        Assert.Contains("Hotspot: Web -> Core", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopedToProject_ShowsOnlyItsEdges()
    {
        await using var graph = await TestGraph.CreateAsync(Build.WebAndCore());

        string result = await graph.Queries.GetProjectDependenciesAsync("Core");

        Assert.Contains("for project 'Core'", result, StringComparison.Ordinal);
        Assert.Contains("Web -> Core", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownProject_ListsValidProjects()
    {
        await using var graph = await TestGraph.CreateAsync(Build.WebAndCore());

        string result = await graph.Queries.GetProjectDependenciesAsync("Nope");

        Assert.Contains("Unknown project", result, StringComparison.Ordinal);
        Assert.Contains("Core", result, StringComparison.Ordinal);
        Assert.Contains("Web", result, StringComparison.Ordinal);
    }
}
