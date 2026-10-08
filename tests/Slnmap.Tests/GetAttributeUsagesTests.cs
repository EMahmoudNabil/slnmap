using Slnmap.Analysis;
using Slnmap.Core.Graph;
using Slnmap.Core.Storage;
using Slnmap.Mcp;
using Slnmap.Storage;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// v0.14.0 Phase 5: get_attribute_usages (docs/EXPANSION-SPECS.md §10; RELEASE-PLAN-v0.14.0.md).
/// Fixture: FixtureLib/AttributeUsagesFixture.cs — one attribute target per shape.
/// </summary>
public sealed class GetAttributeUsagesTests : IDisposable
{
    private const string Marker = "Fixture.Lib.Attributes.UsageProbeAttribute";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "slnmap-tests", Guid.NewGuid().ToString("N"));

    public GetAttributeUsagesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the OS temp cleaner will get it eventually.
        }
    }

    [Fact]
    public async Task Analyze_RecordsEveryTargetShape_AgainstTheRightSymbol()
    {
        var graph = await AnalyzeFixtureAsync();
        var usages = graph.AttributeUsages.Where(u => u.AttributeFqn == Marker).ToList();
        var targets = usages.Select(u => graph.TryGetNode(u.TargetId, out var n) ? n.Fqn : "?").ToList();

        Assert.Contains("Fixture.Lib.Attributes.DecoratedType", targets);                 // type
        Assert.Contains("Fixture.Lib.Attributes.DecoratedType.MultiA", targets);          // field, first declarator
        Assert.Contains("Fixture.Lib.Attributes.DecoratedType.MultiB", targets);          // ...and the second
        Assert.Contains("Fixture.Lib.Attributes.DecoratedType.Name", targets);            // property
        // Method, its parameter, and the lambda inside it all land on the method.
        Assert.Equal(3, targets.Count(t => t == "Fixture.Lib.Attributes.DecoratedType.Method(int)"));
        // The destructor is not a node: its attribute falls back to the type (never dropped).
        Assert.Equal(2, targets.Count(t => t == "Fixture.Lib.Attributes.DecoratedType"));
        Assert.Equal(8, usages.Count);

        // Assembly-level: attributed to the project.
        var assembly = Assert.Single(graph.AttributeUsages, u => u.AttributeFqn == "Fixture.Lib.Attributes.UsageAssemblyProbeAttribute");
        Assert.True(graph.TryGetNode(assembly.TargetId, out var project));
        Assert.Equal(NodeKind.Project, project.Kind);

        // Framework attributes are recorded by FQN although they are never nodes.
        Assert.Contains(graph.AttributeUsages, u => u.AttributeFqn == "System.ObsoleteAttribute");
        Assert.Contains(graph.AttributeUsages, u => u.AttributeFqn == "System.ComponentModel.DataAnnotations.RequiredAttribute");
    }

    [Theory]
    [InlineData("Obsolete")]
    [InlineData("[Obsolete]")]
    [InlineData("ObsoleteAttribute")]
    [InlineData("obsolete")]
    [InlineData("System.ObsoleteAttribute")]
    [InlineData("System.Obsolete")]
    public async Task Tool_AcceptsShortQualifiedBracketedAndSuffixedNames(string query)
    {
        string output = await QueryFixtureAsync(query);

        Assert.Contains("usage(s) of [Obsolete] (System.ObsoleteAttribute):", output, StringComparison.Ordinal);
        Assert.Contains("[Property] Fixture.Lib.Attributes.DecoratedType.WatchSwap —", output, StringComparison.Ordinal);
        Assert.Contains("AttributeUsagesFixture.cs:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_AmbiguousShortName_ListsTheCandidatesInsteadOfGuessing()
    {
        string output = await QueryFixtureAsync("UsageProbe");

        Assert.Contains("[UsageProbe] matches 2 attribute types", output, StringComparison.Ordinal);
        Assert.Contains($"{Marker} (8 usage(s))", output, StringComparison.Ordinal);
        Assert.Contains("Fixture.Lib.Attributes.Other.UsageProbeAttribute (1 usage(s))", output, StringComparison.Ordinal);

        string qualified = await QueryFixtureAsync("Fixture.Lib.Attributes.Other.UsageProbeAttribute");
        Assert.Contains("1 usage(s) of [UsageProbe]", qualified, StringComparison.Ordinal);
        Assert.Contains("DecoratedByTheOtherProbe", qualified, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_AssemblyLevelUsage_RendersAgainstTheProject()
    {
        string output = await QueryFixtureAsync("UsageAssemblyProbe");
        Assert.Contains("[assembly] FixtureLib", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_Unknown_SaysSo_AndSuggestsSimilar()
    {
        string none = await QueryFixtureAsync("NoSuchAttribute");
        Assert.Contains("No usages of [NoSuchAttribute]", none, StringComparison.Ordinal);

        string near = await QueryFixtureAsync("UsagePro");
        Assert.Contains("Similar attributes:", near, StringComparison.Ordinal);
        Assert.Contains(Marker, near, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_MissingInput_V1Graph_UnresolvedAndCap()
    {
        var graph = new CodeGraph();
        var project = SymbolNode.Create(NodeKind.Project, "Big", "Big", Path.Combine(_root, "Big", "Big.csproj"));
        graph.AddNode(project);
        string file = Path.Combine(_root, "Big", "Holder.cs");
        for (int i = 0; i < 120; i++)
        {
            var method = SymbolNode.Create(NodeKind.Method, $"M{i}", $"Big.Holder.M{i}()", file, new SourceSpan(i, i + 1), "Public");
            graph.AddNode(method);
            graph.AddAttributeUsage(new AttributeUsage(method.Id, "unresolved:Frobnicate", file, i));
        }

        await using var store = await SeedAsync(graph);
        var queries = new SlnmapQueries(store);

        string missing = await queries.GetAttributeUsagesAsync(" ");
        Assert.Contains("Provide an attribute", missing, StringComparison.Ordinal);

        string capped = await queries.GetAttributeUsagesAsync("Frobnicate");
        Assert.Contains("50+ usage(s) of [Frobnicate] (unresolved:Frobnicate) (showing first 50 of 120)", capped, StringComparison.Ordinal);
        Assert.Contains("did not resolve at analyze time", capped, StringComparison.Ordinal);
        Assert.True(capped.Length <= 12_000, $"output exceeded budget: {capped.Length} chars");

        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE meta SET value = '1' WHERE key = 'schema_version';";
            await command.ExecuteNonQueryAsync();
        }

        Assert.Contains("re-run 'slnmap analyze'", await queries.GetAttributeUsagesAsync("Frobnicate"), StringComparison.Ordinal);
    }

    // One analysis of the (read-only) fixture per test run; every test here only reads it.
    private static readonly Lazy<Task<CodeGraph>> FixtureGraph = new(async () =>
    {
        DotNet.Run($"restore \"{TestPaths.FixtureSolution}\"", TestPaths.RepoRoot);
        return (await new RoslynSolutionAnalyzer().AnalyzeAsync(TestPaths.FixtureSolution)).Graph;
    });

    private static Task<CodeGraph> AnalyzeFixtureAsync() => FixtureGraph.Value;

    private async Task<string> QueryFixtureAsync(string attribute)
    {
        await using var store = await SeedAsync(await AnalyzeFixtureAsync());
        return await new SlnmapQueries(store).GetAttributeUsagesAsync(attribute);
    }

    private async Task<SqliteGraphStore> SeedAsync(CodeGraph graph)
    {
        var store = new SqliteGraphStore(Path.Combine(_root, $"{Guid.NewGuid():N}.db"));
        await store.SaveAsync(graph, [], new Dictionary<string, string>(StringComparer.Ordinal) { [MetaKeys.LastAnalyzed] = "test" });
        return store;
    }
}
