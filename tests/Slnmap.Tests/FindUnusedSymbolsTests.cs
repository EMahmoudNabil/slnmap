using Slnmap.Analysis;
using Slnmap.Core.Graph;
using Slnmap.Core.Storage;
using Slnmap.Mcp;
using Slnmap.Storage;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// v0.14.0 Phase 4: find_unused_symbols (docs/EXPANSION-SPECS.md §5; RELEASE-PLAN-v0.14.0.md).
/// Fixture: FixtureLib/UnusedSymbolsFixture.cs — one symbol per rule.
/// </summary>
public sealed class FindUnusedSymbolsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "slnmap-tests", Guid.NewGuid().ToString("N"));

    public FindUnusedSymbolsTests() => Directory.CreateDirectory(_root);

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
    public async Task Fixture_ReportsTheUnusedAndExcludesEverythingReachedAnotherWay()
    {
        string output = await RunOnFixtureAsync("FixtureLib", null);

        Assert.StartsWith("CAVEAT: zero static references != dead code.", output, StringComparison.Ordinal);

        // Reported.
        Assert.Contains("[Class] Fixture.Lib.Unused.NeverReferenced —", output, StringComparison.Ordinal);
        Assert.Contains("[Method] Fixture.Lib.Unused.NeverReferenced.NeverCalled() —", output, StringComparison.Ordinal);
        Assert.Contains("[Class] Fixture.Lib.Unused.InternalNeverReferenced —", output, StringComparison.Ordinal);
        Assert.Contains("[Method] Fixture.Lib.Unused.ExtensionCaller.Call() —", output, StringComparison.Ordinal);
        // Its members only use each other: self-references do not make a type used.
        Assert.Contains("[Class] Fixture.Lib.Unused.PrivateOnly —", output, StringComparison.Ordinal);

        // Used through something it contains (the extension method is called).
        Assert.DoesNotContain("Fixture.Lib.Unused.UsedOnlyThroughExtension —", output, StringComparison.Ordinal);
        Assert.DoesNotContain("UsedOnlyThroughExtension.Twice(int)", output, StringComparison.Ordinal);

        // Reached through a base type / interface — excluded and counted.
        Assert.DoesNotContain("UnusedDerived.Hook()", output, StringComparison.Ordinal);
        Assert.DoesNotContain("ExternalInterfaceImplementor.Dispose()", output, StringComparison.Ordinal);
        Assert.Contains("override(s)", output, StringComparison.Ordinal);
        Assert.Contains("interface implementation(s)", output, StringComparison.Ordinal);

        // Framework-shaped type: listed, but in the lower-confidence section, never hidden.
        int lowerConfidence = output.IndexOf("Lower confidence", StringComparison.Ordinal);
        int implementor = output.IndexOf("Fixture.Lib.Unused.ExternalInterfaceImplementor —", StringComparison.Ordinal);
        Assert.True(lowerConfidence > 0 && implementor > lowerConfidence, "framework-shaped type must be listed under 'Lower confidence'");

        // A record is not "framework-derived" just for its synthesized IEquatable<itself>.
        Assert.True(
            output.IndexOf("[Record] Fixture.Lib.Unused.UnusedRecord —", StringComparison.Ordinal) is > 0 and var record
                && (lowerConfidence < 0 || record < lowerConfidence),
            "UnusedRecord must be in the main list, not the lower-confidence section");

        // Private members are never searched.
        Assert.DoesNotContain("PrivateOnly.Hidden()", output, StringComparison.Ordinal);
        Assert.Contains("private and protected members are not", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KindFilter_NarrowsTheResult()
    {
        string output = await RunOnFixtureAsync("FixtureLib", "Method");

        Assert.Contains("NeverReferenced.NeverCalled()", output, StringComparison.Ordinal);
        Assert.DoesNotContain("[Class]", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedInput_GetsSelfCorrectingMessages()
    {
        await using var store = await SeedAsync(new CodeGraph());
        var queries = new SlnmapQueries(store);

        string badKind = await queries.FindUnusedSymbolsAsync("all", "Widget");
        Assert.Contains("Unknown kind 'Widget'", badKind, StringComparison.Ordinal);
        Assert.Contains("Method", badKind, StringComparison.Ordinal);

        // Constructors are never edge targets, so they cannot be judged (QA finding 2).
        Assert.Contains("Unknown kind 'Constructor'", await queries.FindUnusedSymbolsAsync("all", "Constructor"), StringComparison.Ordinal);

        string badScope = await queries.FindUnusedSymbolsAsync("NoSuchProject", null);
        Assert.Contains("Unknown project 'NoSuchProject'", badScope, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyGraph_SaysZero_StillLeadsWithTheCaveat()
    {
        await using var store = await SeedAsync(new CodeGraph());
        string output = await new SlnmapQueries(store).FindUnusedSymbolsAsync("all", null);

        Assert.StartsWith("CAVEAT:", output, StringComparison.Ordinal);
        Assert.Contains("0 public/internal types and members with no incoming references (scope=all).", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task V1Graph_AsksForARebuildInsteadOfAnsweringWrong()
    {
        await using var store = await SeedAsync(new CodeGraph());
        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE meta SET value = '1' WHERE key = 'schema_version';";
            await command.ExecuteNonQueryAsync();
        }

        string output = await new SlnmapQueries(store).FindUnusedSymbolsAsync("all", null);
        Assert.Contains("re-run 'slnmap analyze'", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ForcedCap_IsDisclosed_AndStaysWithinTheOutputBudget()
    {
        var graph = new CodeGraph();
        var project = SymbolNode.Create(NodeKind.Project, "Big", "Big", Path.Combine(_root, "Big", "Big.csproj"));
        graph.AddNode(project);
        for (int i = 0; i < 300; i++)
        {
            graph.AddNode(SymbolNode.Create(
                NodeKind.Method, $"M{i}", $"Big.Holder.M{i}()", Path.Combine(_root, "Big", "Holder.cs"), new SourceSpan(i, i + 1), "Public"));
        }

        await using var store = await SeedAsync(graph);
        string output = await new SlnmapQueries(store).FindUnusedSymbolsAsync("all", null);

        Assert.Contains("50+ public/internal types and members", output, StringComparison.Ordinal);
        Assert.Contains("showing first 50 of 300", output, StringComparison.Ordinal);
        Assert.True(output.Length <= 12_000, $"output exceeded budget: {output.Length} chars");
    }

    private async Task<string> RunOnFixtureAsync(string scope, string? kind)
    {
        DotNet.Run($"restore \"{TestPaths.FixtureSolution}\"", TestPaths.RepoRoot);
        var snapshot = await new RoslynSolutionAnalyzer().AnalyzeAsync(TestPaths.FixtureSolution);

        // The fixture solution is a test bed full of deliberately unreferenced code, far past the
        // tool's cap; isolate this rule set's file (plus projects/namespaces for attribution and
        // containment), keeping the analyzer's real nodes, flags and edges.
        var isolated = new CodeGraph();
        foreach (var node in snapshot.Graph.Nodes.Where(n =>
            n.Kind is NodeKind.Project or NodeKind.Namespace
            || (n.FilePath?.EndsWith("UnusedSymbolsFixture.cs", StringComparison.Ordinal) ?? false)))
        {
            isolated.AddNode(node);
        }

        foreach (var edge in snapshot.Graph.Edges.Where(e => isolated.ContainsNode(e.SourceId) && isolated.ContainsNode(e.TargetId)))
        {
            isolated.AddEdge(edge);
        }

        await using var store = await SeedAsync(isolated);
        return await new SlnmapQueries(store).FindUnusedSymbolsAsync(scope, kind);
    }

    private async Task<SqliteGraphStore> SeedAsync(CodeGraph graph)
    {
        string dbPath = Path.Combine(_root, $"{Guid.NewGuid():N}.db");
        var store = new SqliteGraphStore(dbPath);
        await store.SaveAsync(graph, [], new Dictionary<string, string>(StringComparer.Ordinal) { [MetaKeys.LastAnalyzed] = "test" });
        return store;
    }
}
