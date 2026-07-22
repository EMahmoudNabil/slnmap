using Xunit;

namespace Slnmap.Tests;

public sealed class CircularDependenciesTests
{
    [Fact]
    public async Task MutualProjects_ReportsCycle()
    {
        await using var graph = await TestGraph.CreateAsync(Build.MutualProjects());

        string result = await graph.Queries.FindCircularDependenciesAsync("project");

        Assert.Contains("1 project-level dependency cycle(s) found", result, StringComparison.Ordinal);
        Assert.Contains("Alpha", result, StringComparison.Ordinal);
        Assert.Contains("Beta", result, StringComparison.Ordinal);
        Assert.Contains("hops", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcyclicSolution_ReportsZeroCyclesAsARealAnswer()
    {
        await using var graph = await TestGraph.CreateAsync(Build.WebAndCore());

        string result = await graph.Queries.FindCircularDependenciesAsync("project");

        Assert.Contains("0 project-level dependency cycle(s) found", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidScope_ReturnsActionableMessage()
    {
        await using var graph = await TestGraph.CreateAsync(Build.WebAndCore());

        string result = await graph.Queries.FindCircularDependenciesAsync("module");

        Assert.Contains("scope must be", result, StringComparison.Ordinal);
    }
}
