using Slnmap.Analysis;
using Slnmap.Core.Graph;
using Slnmap.Core.Storage;
using Slnmap.Mcp;
using Slnmap.Storage;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// v0.14.0 Phase 7: find_callers_of_external (docs/EXPANSION-SPECS.md §7; RELEASE-PLAN-v0.14.0.md;
/// reports/v0140-gate7a-external-calls-budget.md for the one-fact-per-(caller, target) storage).
/// Fixture: FixtureLib/ExternalCallsFixture.cs.
/// </summary>
public sealed class FindCallersOfExternalTests : IDisposable
{
    private const string Build = "Fixture.Lib.External.ExternalCaller.Build()";

    private static readonly Lazy<Task<CodeGraph>> FixtureGraph = new(async () =>
    {
        DotNet.Run($"restore \"{TestPaths.FixtureSolution}\"", TestPaths.RepoRoot);
        return (await new RoslynSolutionAnalyzer().AnalyzeAsync(TestPaths.FixtureSolution)).Graph;
    });

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "slnmap-tests", Guid.NewGuid().ToString("N"));

    public FindCallersOfExternalTests() => Directory.CreateDirectory(_root);

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
    public async Task Analyze_RecordsOneFactPerCallerAndTarget_WithTheCountAndFirstSite()
    {
        var graph = await FixtureGraph.Value;
        var calls = graph.ExternalCalls.Where(c => c.FilePath.EndsWith("ExternalCallsFixture.cs", StringComparison.Ordinal)).ToList();
        string CallerOf(ExternalCall c) => graph.TryGetNode(c.CallerId, out var n) ? n.Fqn : "?";

        var build = calls.Where(c => CallerOf(c) == Build).ToDictionary(c => c.TargetFqn);
        Assert.Equal(2, build["System.Text.StringBuilder.Append(string)"].CallCount);
        Assert.Equal(1, build["System.Text.StringBuilder.Append(char)"].CallCount);
        Assert.Contains("System.Text.StringBuilder.StringBuilder()", build.Keys);   // constructor
        Assert.Contains("System.Text.StringBuilder.ToString()", build.Keys);
        Assert.All(build.Values, c => Assert.Equal("System.Text", c.TargetNamespace));

        // The two Append(string) calls are one fact carrying the FIRST site.
        string text = await File.ReadAllTextAsync(build["System.Text.StringBuilder.Append(string)"].FilePath);
        Assert.Equal("builder.Append(\"a\")", text.Substring(build["System.Text.StringBuilder.Append(string)"].SpanStart, "builder.Append(\"a\")".Length));

        var serialize = Assert.Single(calls, c => CallerOf(c) == "Fixture.Lib.External.ExternalCaller.Serialize(object)");
        Assert.Equal("System.Text.Json", serialize.TargetNamespace);
        Assert.Equal("System.Text.Json", serialize.TargetAssembly);

        // A field initializer is attributed to the field (fields are nodes).
        Assert.Contains(calls, c => CallerOf(c) == "Fixture.Lib.External.ExternalCaller.Shared" && c.TargetFqn == "System.Text.StringBuilder.StringBuilder()");

        // In-source calls are graph edges, never external calls.
        Assert.DoesNotContain(calls, c => CallerOf(c) == "Fixture.Lib.External.ExternalCaller.Local()");
    }

    [Fact]
    public async Task Tool_NamespacePrefix_CountsFirst_ThenCallersWithWhatTheyCall()
    {
        string output = await QueryAsync("System.Text", "FixtureLib");

        // Prefix "System.Text" covers System.Text and System.Text.Json (segment-aware).
        Assert.Contains("call site(s) into 'System.Text' from", output, StringComparison.Ordinal);
        Assert.Contains("Most-called targets:", output, StringComparison.Ordinal);
        Assert.Contains($"[Method] {Build} —", output, StringComparison.Ordinal);
        Assert.Contains("StringBuilder.Append ×2", output, StringComparison.Ordinal);
        Assert.Contains("ExternalCaller.Serialize(object) —", output, StringComparison.Ordinal);
        Assert.Contains("ExternalCallsFixture.cs:", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_AssemblyName_And_SegmentAwarePrefix()
    {
        string byAssembly = await QueryAsync("System.Text.Json", "FixtureLib");
        Assert.Contains("ExternalCaller.Serialize(object)", byAssembly, StringComparison.Ordinal);
        Assert.DoesNotContain(Build, byAssembly, StringComparison.Ordinal);

        // "System.Te" is not a namespace segment boundary: no partial-word matches.
        string partial = await QueryAsync("System.Te", "FixtureLib");
        Assert.Contains("0 calls into 'System.Te'", partial, StringComparison.Ordinal);
        Assert.Contains("Similar namespaces:", partial, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_MissingInput_UnknownProject_V1Graph_AndCap()
    {
        var graph = new CodeGraph();
        graph.AddNode(SymbolNode.Create(NodeKind.Project, "Big", "Big", Path.Combine(_root, "Big", "Big.csproj")));
        string file = Path.Combine(_root, "Big", "Callers.cs");
        for (int i = 0; i < 100; i++)
        {
            var caller = SymbolNode.Create(NodeKind.Method, $"M{i}", $"Big.Callers.M{i}()", file, new SourceSpan(i, i + 1), "Public");
            graph.AddNode(caller);
            graph.AddExternalCall(new ExternalCall(caller.Id, "Vendor.Sdk.Client.Send(string)", "Vendor.Sdk", "Vendor.Sdk", file, i, CallCount: 3));
        }

        // A generated file's calls are counted in the header, never listed.
        string designer = Path.Combine(_root, "Big", "Migrations", "20260101_Init.Designer.cs");
        var generatedCaller = SymbolNode.Create(NodeKind.Method, "BuildTargetModel", "Big.Init.BuildTargetModel()", designer, new SourceSpan(0, 1), "Protected");
        graph.AddNode(generatedCaller);
        graph.AddExternalCall(new ExternalCall(generatedCaller.Id, "Vendor.Sdk.Client.Send(string)", "Vendor.Sdk", "Vendor.Sdk", designer, 0, CallCount: 5000));

        await using var store = await SeedAsync(graph);
        var queries = new SlnmapQueries(store);

        Assert.Contains("Provide a namespace prefix", await queries.FindCallersOfExternalAsync(" ", null), StringComparison.Ordinal);
        Assert.Contains("Unknown project 'Nope'", await queries.FindCallersOfExternalAsync("Vendor", "Nope"), StringComparison.Ordinal);

        string capped = await queries.FindCallersOfExternalAsync("Vendor", "all");
        Assert.Contains("300 call site(s) into 'Vendor' from 100 member(s) across 1 project(s)", capped, StringComparison.Ordinal);
        Assert.Contains("Callers (showing first 30 of 100", capped, StringComparison.Ordinal);
        Assert.Contains("(+ 5000 more call site(s) in 1 generated file(s)", capped, StringComparison.Ordinal);
        Assert.DoesNotContain("BuildTargetModel", capped, StringComparison.Ordinal);
        Assert.Contains("Client.Send ×3", capped, StringComparison.Ordinal);
        Assert.True(capped.Length <= 12_000, $"output exceeded budget: {capped.Length} chars");

        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE meta SET value = '1' WHERE key = 'schema_version';";
            await command.ExecuteNonQueryAsync();
        }

        Assert.Contains("re-run 'slnmap analyze'", await queries.FindCallersOfExternalAsync("Vendor", null), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Storage_RoundTripsTheNormalizedTargetsAndCounts()
    {
        var graph = new CodeGraph();
        var a = SymbolNode.Create(NodeKind.Method, "A", "N.T.A()", "T.cs");
        var b = SymbolNode.Create(NodeKind.Method, "B", "N.T.B()", "T.cs");
        graph.AddNode(a);
        graph.AddNode(b);
        // Two callers of one target: the target is stored once, both calls keep their own counts.
        graph.AddExternalCall(new ExternalCall(a.Id, "X.Y.Z()", "X", "XAsm", "T.cs", 1, CallCount: 4));
        graph.AddExternalCall(new ExternalCall(b.Id, "X.Y.Z()", "X", "XAsm", "T.cs", 9));
        graph.AddExternalCall(new ExternalCall(b.Id, "Q.R()", string.Empty, null, "T.cs", 12));

        await using var store = await SeedAsync(graph);
        var loaded = await store.LoadGraphAsync();
        Assert.True(loaded.FactsEqual(graph));

        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM external_targets;";
        Assert.Equal(2L, (long)(await command.ExecuteScalarAsync())!);
    }

    private async Task<string> QueryAsync(string target, string? project)
    {
        await using var store = await SeedAsync(await FixtureGraph.Value);
        return await new SlnmapQueries(store).FindCallersOfExternalAsync(target, project);
    }

    private async Task<SqliteGraphStore> SeedAsync(CodeGraph graph)
    {
        var store = new SqliteGraphStore(Path.Combine(_root, $"{Guid.NewGuid():N}.db"));
        await store.SaveAsync(graph, [], new Dictionary<string, string>(StringComparer.Ordinal) { [MetaKeys.LastAnalyzed] = "test" });
        return store;
    }
}
