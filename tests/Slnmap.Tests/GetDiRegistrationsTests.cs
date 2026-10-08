using Slnmap.Analysis;
using Slnmap.Core.Graph;
using Slnmap.Core.Storage;
using Slnmap.Mcp;
using Slnmap.Storage;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// v0.14.0 Phase 6: get_di_registrations (docs/EXPANSION-SPECS.md §8; RELEASE-PLAN-v0.14.0.md).
/// Fixture: FixtureWeb/DiRegistrationFixtures.cs — one registration per recognized shape.
/// </summary>
public sealed class GetDiRegistrationsTests : IDisposable
{
    private const string Clock = "Fixture.Web.Di.IClock";
    private const string SystemClock = "Fixture.Web.Di.SystemClock";
    private const string Settings = "Fixture.Web.Di.Settings";

    private static readonly Lazy<Task<CodeGraph>> FixtureGraph = new(async () =>
    {
        DotNet.Run($"restore \"{TestPaths.FixtureSolution}\"", TestPaths.RepoRoot);
        return (await new RoslynSolutionAnalyzer().AnalyzeAsync(TestPaths.FixtureSolution)).Graph;
    });

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "slnmap-tests", Guid.NewGuid().ToString("N"));

    public GetDiRegistrationsTests() => Directory.CreateDirectory(_root);

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
    public async Task Analyze_ReadsEveryRegistrationShape()
    {
        var registrations = (await FixtureGraph.Value).DiRegistrations
            .Where(r => r.FilePath.EndsWith("DiRegistrationFixtures.cs", StringComparison.Ordinal))
            .OrderBy(r => r.SpanStart)
            .Select(r => (r.ServiceFqn, r.ImplementationFqn, r.Lifetime, r.RegistrationKind))
            .ToList();

        Assert.Equal(
            [
                (Clock, SystemClock, "Scoped", DiRegistrationKinds.Generic),
                (Settings, Settings, "Singleton", DiRegistrationKinds.Generic),
                (Clock, SystemClock, "Transient", DiRegistrationKinds.Factory),
                (Settings, null, "Transient", DiRegistrationKinds.Factory),
                (Clock, SystemClock, "Singleton", DiRegistrationKinds.Instance),
                (Clock, SystemClock, "Scoped", DiRegistrationKinds.TypeOf),
                ("Fixture.Web.Di.IStore<T>", "Fixture.Web.Di.MemoryStore<T>", "Scoped", DiRegistrationKinds.OpenGeneric),
                (Clock, SystemClock, "Singleton, key \"utc\"", DiRegistrationKinds.Generic),
                (Clock, SystemClock, "Transient", DiRegistrationKinds.Generic),
                ("Microsoft.Extensions.Hosting.IHostedService", "Fixture.Web.Di.Worker", "Singleton", DiRegistrationKinds.Generic),
                ("services.AddScoped(dynamicType)", null, "Scoped", DiRegistrationKinds.Unrecognized),
                ("services.TryAddEnumerable(ServiceDescriptor.Singleton<IClock, SystemClock>())", null, "Unknown", DiRegistrationKinds.Unrecognized),
                (Clock, null, "Singleton", DiRegistrationKinds.Factory),
                (Settings, Settings, "Scoped", DiRegistrationKinds.Generic),
            ],
            registrations);

        // Every registration knows where it was made and by whom.
        var graph = await FixtureGraph.Value;
        Assert.All(
            graph.DiRegistrations.Where(r => r.FilePath.EndsWith("DiRegistrationFixtures.cs", StringComparison.Ordinal)),
            r =>
            {
                Assert.Equal("FixtureWeb", r.Project);
                Assert.True(graph.TryGetNode(r.CallerId!, out var caller));
                Assert.Equal("Fixture.Web.Di.DiRegistrations.AddFixtureServices(Microsoft.Extensions.DependencyInjection.IServiceCollection, System.Type)", caller.Fqn);
            });
    }

    [Fact]
    public async Task Tool_RendersEachShape_ListsUnrecognizedSeparately_AndStatesTheLimit()
    {
        string output = await QueryAsync("FixtureWeb", null);

        Assert.Contains($"{Clock} -> {SystemClock} [Scoped] —", output, StringComparison.Ordinal);
        Assert.Contains($"{Settings} (self) [Singleton]", output, StringComparison.Ordinal);
        Assert.Contains($"{Clock} -> {SystemClock} (factory) [Transient]", output, StringComparison.Ordinal);
        Assert.Contains($"{Settings} -> (factory) [Transient]", output, StringComparison.Ordinal);
        Assert.Contains($"{Clock} -> (instance of {SystemClock}) [Singleton]", output, StringComparison.Ordinal);
        Assert.Contains("Fixture.Web.Di.IStore<T> -> Fixture.Web.Di.MemoryStore<T> (open generic) [Scoped]", output, StringComparison.Ordinal);
        Assert.Contains("DiRegistrationFixtures.cs:", output, StringComparison.Ordinal);

        Assert.Contains("Unrecognized (2)", output, StringComparison.Ordinal);
        Assert.Contains("services.AddScoped(dynamicType) [Scoped]", output, StringComparison.Ordinal);
        Assert.Contains("are not expanded", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_TypeFilter_MatchesServiceOrImplementation()
    {
        string byImplementation = await QueryAsync("all", "MemoryStore");
        Assert.Contains("1 DI registration(s) involving 'MemoryStore'", byImplementation, StringComparison.Ordinal);

        string none = await QueryAsync("all", "NoSuchType");
        Assert.Contains("0 DI registrations involving 'NoSuchType'.", none, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_MalformedProject_V1Graph_AndCap()
    {
        var graph = new CodeGraph();
        graph.AddNode(SymbolNode.Create(NodeKind.Project, "Big", "Big", Path.Combine(_root, "Big", "Big.csproj")));
        string file = Path.Combine(_root, "Big", "Program.cs");
        for (int i = 0; i < 90; i++)
        {
            graph.AddDiRegistration(new DiRegistration($"Big.IService{i}", $"Big.Service{i}", "Scoped", DiRegistrationKinds.Generic, null, file, i, "Big"));
        }

        await using var store = await SeedAsync(graph);
        var queries = new SlnmapQueries(store);

        Assert.Contains("Unknown project 'Nope'", await queries.GetDiRegistrationsAsync("Nope", null), StringComparison.Ordinal);

        string capped = await queries.GetDiRegistrationsAsync("all", null);
        Assert.Contains("60+ DI registration(s) across 1 project(s) (showing first 60 of 90", capped, StringComparison.Ordinal);
        Assert.True(capped.Length <= 12_000, $"output exceeded budget: {capped.Length} chars");

        await using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE meta SET value = '1' WHERE key = 'schema_version';";
            await command.ExecuteNonQueryAsync();
        }

        Assert.Contains("re-run 'slnmap analyze'", await queries.GetDiRegistrationsAsync("all", null), StringComparison.Ordinal);
    }

    private async Task<string> QueryAsync(string project, string? type)
    {
        await using var store = await SeedAsync(await FixtureGraph.Value);
        return await new SlnmapQueries(store).GetDiRegistrationsAsync(project, type);
    }

    private async Task<SqliteGraphStore> SeedAsync(CodeGraph graph)
    {
        var store = new SqliteGraphStore(Path.Combine(_root, $"{Guid.NewGuid():N}.db"));
        await store.SaveAsync(graph, [], new Dictionary<string, string>(StringComparer.Ordinal) { [MetaKeys.LastAnalyzed] = "test" });
        return store;
    }
}
