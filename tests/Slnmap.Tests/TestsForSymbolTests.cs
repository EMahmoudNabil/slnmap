using Xunit;

namespace Slnmap.Tests;

public sealed class TestsForSymbolTests
{
    [Fact]
    public async Task ReportsTestMemberReachingTheSymbol()
    {
        await using var graph = await TestGraph.CreateAsync(Build.ProductWithTests());

        string result = await graph.Queries.FindTestsForSymbolAsync("Foo.Bar.Do()");

        Assert.Contains("1 test member(s) across 1 test project(s)", result, StringComparison.Ordinal);
        Assert.Contains("FooTests.BarTests.Do_Works()", result, StringComparison.Ordinal);
        Assert.Contains("heuristic", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoTestProjects_SaysSoInsteadOfEmpty()
    {
        await using var graph = await TestGraph.CreateAsync(Build.WebAndCore()); // no project named *Test*

        string result = await graph.Queries.FindTestsForSymbolAsync("Core.B.N()");

        Assert.Contains("No test projects detected", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SymbolNoTestReaches_ReportsNoTestsFound()
    {
        await using var graph = await TestGraph.CreateAsync(Build.ProductWithTests());

        // The test method itself is reached by nothing in a test project.
        string result = await graph.Queries.FindTestsForSymbolAsync("FooTests.BarTests.Do_Works()");

        Assert.Contains("No tests found that reach", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownFqn_ReturnsNotFound()
    {
        await using var graph = await TestGraph.CreateAsync(Build.ProductWithTests());

        string result = await graph.Queries.FindTestsForSymbolAsync("Foo.Bar.Missing()");

        Assert.Contains("No symbol with FQN", result, StringComparison.Ordinal);
    }
}
