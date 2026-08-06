using Slnmap.Analysis;
using Slnmap.Core.Storage;
using Slnmap.Mcp;
using Slnmap.Storage;
using Xunit;

namespace Slnmap.Tests;

/// <summary>Analyzes the fixture solution once and persists it to a temp database for the MCP tool tests.</summary>
public sealed class AnalyzedFixtureGraphStore : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "slnmap-mcp-tests", Guid.NewGuid().ToString("N"));

    public SqliteGraphStore Store { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        DotNet.Run($"restore \"{TestPaths.FixtureSolution}\"", TestPaths.RepoRoot);
        var snapshot = await new RoslynSolutionAnalyzer().AnalyzeAsync(TestPaths.FixtureSolution);
        Store = new SqliteGraphStore(Path.Combine(_directory, "graph.db"));
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MetaKeys.SolutionPath] = TestPaths.FixtureSolution,
            [MetaKeys.LastAnalyzed] = "test",
        };
        await Store.SaveAsync(snapshot.Graph, snapshot.Files, meta);
    }

    public async Task DisposeAsync()
    {
        await Store.DisposeAsync();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}

public sealed class McpToolTests : IClassFixture<AnalyzedFixtureGraphStore>
{
    private const string InterfaceAreaFqn = "Fixture.Lib.IShape.Area()";
    private const string TotalAreaFqn =
        "Fixture.Lib.Geometry.TotalArea(System.Collections.Generic.IEnumerable<Fixture.Lib.IShape>)";

    private readonly SlnmapQueries _queries;

    public McpToolTests(AnalyzedFixtureGraphStore fixture) => _queries = new SlnmapQueries(fixture.Store);

    [Fact]
    public async Task FindSymbol_MatchesByName()
    {
        string result = await _queries.FindSymbolAsync("IShape", null);
        Assert.Contains("Fixture.Lib.IShape", result, StringComparison.Ordinal);
        Assert.Contains("[Interface]", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindSymbol_KindFilter_RestrictsResults()
    {
        string result = await _queries.FindSymbolAsync("Area", "Method");
        Assert.Contains("Fixture.Lib.IShape.Area()", result, StringComparison.Ordinal);
        Assert.DoesNotContain("[Interface]", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetDependencies_Outgoing_ListsCallEdge()
    {
        string result = await _queries.GetDependenciesAsync(TotalAreaFqn, "outgoing", 1);
        Assert.Contains("Calls", result, StringComparison.Ordinal);
        Assert.Contains("Fixture.Lib.IShape.Area()", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetDependencies_Incoming_FindsCaller()
    {
        string result = await _queries.GetDependenciesAsync(InterfaceAreaFqn, "incoming", 1);
        Assert.Contains("TotalArea", result, StringComparison.Ordinal);
    }

    // The defining requirement: impact of an interface member must reach the concrete
    // implementations/overrides (Implements + Inherits), not just direct callers of the interface.
    [Fact]
    public async Task ImpactAnalysis_InterfaceMember_IncludesImplementations()
    {
        string result = await _queries.ImpactAnalysisAsync(InterfaceAreaFqn);

        Assert.Contains("Fixture.Lib.Circle.Area()", result, StringComparison.Ordinal);
        Assert.Contains("Fixture.Lib.Square.Area()", result, StringComparison.Ordinal);
        // ...and the call-site chain through the interface.
        Assert.Contains("Fixture.Lib.Geometry.TotalArea", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArchitectureOverview_ListsProjectsAndKinds()
    {
        string result = await _queries.GetArchitectureOverviewAsync();
        Assert.Contains("FixtureLib", result, StringComparison.Ordinal);
        Assert.Contains("FixtureApp", result, StringComparison.Ordinal);
        Assert.Contains("Interface", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindUsages_ReportsReferencingMemberAndFile()
    {
        // The entry point does `new Circle(...)`, producing a References edge into Circle.
        string result = await _queries.FindUsagesAsync("Fixture.Lib.Circle");
        Assert.Contains("Program.cs", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownFqn_ReturnsDidYouMeanSuggestions()
    {
        // "Are" is a typo of "Area"; suggestions should surface the real members.
        string result = await _queries.FindUsagesAsync("Fixture.Lib.Circle.Are()");
        Assert.Contains("Did you mean", result, StringComparison.Ordinal);
        Assert.Contains("Area", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindUsages_GenericTypeArgumentOnlyReference_FindsIt()
    {
        // The false-"safe-to-delete" case: a class referenced only via
        // Registrar.Register<GenericMethodArgOnly>() must not report "no usages found".
        string result = await _queries.FindUsagesAsync("Fixture.Lib.GenericMethodArgOnly");
        Assert.Contains("Fixture.Lib.GenericRefs.UseAll()", result, StringComparison.Ordinal);
        Assert.DoesNotContain("No usages found", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImpactAnalysis_GenericTypeArgumentOnlyReference_ReportsReferencingMember()
    {
        string result = await _queries.ImpactAnalysisAsync("Fixture.Lib.GenericMethodArgOnly");
        Assert.Contains("Fixture.Lib.GenericRefs.UseAll()", result, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing else in the graph depends on it", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindUsages_BareTypeofOnlyReference_FindsIt()
    {
        string result = await _queries.FindUsagesAsync("Fixture.Lib.TypeofOnly");
        Assert.Contains("Fixture.Lib.GenericRefs.UseAll()", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindUsages_AttributeArgumentOnlyReference_FindsIt()
    {
        string result = await _queries.FindUsagesAsync("Fixture.Lib.AttributeArgOnly");
        Assert.Contains("Fixture.Lib.Marked", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindSymbol_FieldByName_ReturnsFieldKind()
    {
        string result = await _queries.FindSymbolAsync("KnownTypes", null);
        Assert.Contains("[Field]", result, StringComparison.Ordinal);
        Assert.Contains("Fixture.Lib.FieldHolder.KnownTypes", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindSymbol_FieldKindFilter_RestrictsResults()
    {
        string result = await _queries.FindSymbolAsync("_a", "Field");
        Assert.Contains("Fixture.Lib.MultiDeclaratorFields._a", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotAnalyzedDatabase_ReturnsActionableMessage()
    {
        string directory = Path.Combine(Path.GetTempPath(), "slnmap-mcp-empty", Guid.NewGuid().ToString("N"));
        await using var store = new SqliteGraphStore(Path.Combine(directory, "empty.db"));
        await store.InitializeAsync();
        try
        {
            string result = await new SlnmapQueries(store).FindSymbolAsync("anything", null);
            Assert.Contains("slnmap analyze", result, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }
}
