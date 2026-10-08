using Microsoft.Data.Sqlite;
using Slnmap.Analysis;
using Slnmap.Core.Graph;
using Slnmap.Core.Storage;
using Slnmap.Storage;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// Schema v2 foundation (RELEASE-PLAN-v0.14.0.md, Phase 1): <c>nodes.accessibility</c> plus the
/// three file-owned fact tables. Nothing populates the fact tables yet — these tests pin the
/// plumbing every later tool depends on: storage round-trip, v1 tolerance, the schema-version
/// row, and incremental eviction scoped by the fact's owning file.
/// </summary>
public sealed class SchemaV2Tests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "slnmap-tests", Guid.NewGuid().ToString("N"));

    public SchemaV2Tests() => Directory.CreateDirectory(_root);

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
    public async Task SaveThenLoad_RoundTripsAccessibilityAndAllThreeFactKinds()
    {
        var graph = new CodeGraph();
        var type = SymbolNode.Create(NodeKind.Class, "Widget", "Demo.Widget", "Widget.cs", new SourceSpan(0, 10), "Public");
        var method = SymbolNode.Create(NodeKind.Method, "Run", "Demo.Widget.Run()", "Widget.cs", new SourceSpan(20, 30), "Private");
        var ns = SymbolNode.Create(NodeKind.Namespace, "Demo", "Demo");
        graph.AddNode(type);
        graph.AddNode(method);
        graph.AddNode(ns);
        graph.AddEdge(new RelationshipEdge(type.Id, method.Id, RelationshipKind.Contains));

        var call = new ExternalCall(method.Id, "System.Console.WriteLine(string)", "System", "System.Console", "Widget.cs", 25);
        var registration = new DiRegistration("Demo.IWidget", "Demo.Widget", "Scoped", DiRegistrationKinds.Generic, null, "Program.cs", 40, "Demo");
        var factory = new DiRegistration("Demo.IClock", null, "Singleton", DiRegistrationKinds.Factory, null, "Program.cs", 90, "Demo");
        var usage = new AttributeUsage(type.Id, "System.ObsoleteAttribute", "Widget.cs", 0);
        graph.AddExternalCall(call);
        graph.AddDiRegistration(registration);
        graph.AddDiRegistration(factory);
        graph.AddAttributeUsage(usage);

        string dbPath = Path.Combine(_root, "graph.db");
        await using (var store = new SqliteGraphStore(dbPath))
        {
            await store.SaveAsync(graph, [], new Dictionary<string, string>(StringComparer.Ordinal));
        }

        await using var reader = new SqliteGraphStore(dbPath);
        var loaded = await reader.LoadGraphAsync();

        Assert.True(loaded.TryGetNode(type.Id, out var loadedType));
        Assert.Equal("Public", loadedType.Accessibility);
        Assert.True(loaded.TryGetNode(method.Id, out var loadedMethod));
        Assert.Equal("Private", loadedMethod.Accessibility);
        Assert.True(loaded.TryGetNode(ns.Id, out var loadedNs));
        Assert.Null(loadedNs.Accessibility);

        Assert.Equal(4, loaded.FactCount);
        Assert.True(loaded.FactsEqual(graph));
    }

    [Fact]
    public async Task Save_WritesCurrentSchemaVersion_EvenWhenCallerCarriesAStaleOne()
    {
        // analyze/analyze-ts/link copy the previous meta table wholesale to keep producer state;
        // a stale schema_version inside it must never overwrite the version of the file just built.
        string dbPath = Path.Combine(_root, "graph.db");
        var staleMeta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MetaKeys.SchemaVersion] = "1",
            [MetaKeys.ToolVersion] = "0.13.1",
        };

        await using (var store = new SqliteGraphStore(dbPath))
        {
            await store.SaveAsync(new CodeGraph(), [], staleMeta);
        }

        await using var reader = new SqliteGraphStore(dbPath);
        var meta = await reader.GetMetaAsync();
        Assert.Equal(SqliteGraphStore.CurrentSchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), meta[MetaKeys.SchemaVersion]);
        Assert.Equal("0.13.1", meta[MetaKeys.ToolVersion]);
    }

    [Fact]
    public async Task LoadGraph_OnAV1Database_ReadsNodesAndEdges_WithNoAccessibilityAndNoFacts()
    {
        // A running server can be pointed at a graph an older slnmap built: exactly the v1 DDL,
        // no accessibility column, no fact tables. Loading must degrade, not throw.
        string dbPath = Path.Combine(_root, "v1.db");
        var type = SymbolNode.Create(NodeKind.Class, "Widget", "Demo.Widget", "Widget.cs", new SourceSpan(0, 10));
        var method = SymbolNode.Create(NodeKind.Method, "Run", "Demo.Widget.Run()", "Widget.cs", new SourceSpan(20, 30));
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"""
                CREATE TABLE nodes (id TEXT PRIMARY KEY, kind TEXT NOT NULL, name TEXT NOT NULL, fqn TEXT NOT NULL,
                                    file TEXT NULL, span_start INTEGER NULL, span_end INTEGER NULL);
                CREATE TABLE edges (source_id TEXT NOT NULL, target_id TEXT NOT NULL, kind TEXT NOT NULL,
                                    PRIMARY KEY (source_id, target_id, kind)) WITHOUT ROWID;
                CREATE TABLE files (path TEXT PRIMARY KEY, content_hash TEXT NOT NULL);
                CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO meta VALUES ('schema_version', '1');
                INSERT INTO nodes VALUES ('{type.Id}', 'Class', 'Widget', 'Demo.Widget', 'Widget.cs', 0, 10);
                INSERT INTO nodes VALUES ('{method.Id}', 'Method', 'Run', 'Demo.Widget.Run()', 'Widget.cs', 20, 30);
                INSERT INTO edges VALUES ('{type.Id}', '{method.Id}', 'Contains');
                """;
            await command.ExecuteNonQueryAsync();
        }

        await using var store = new SqliteGraphStore(dbPath);
        var loaded = await store.LoadGraphAsync();

        Assert.Equal(2, loaded.NodeCount);
        Assert.Equal(1, loaded.EdgeCount);
        Assert.All(loaded.Nodes, n => Assert.Null(n.Accessibility));
        Assert.Equal(0, loaded.FactCount);

        // InitializeAsync (what serve calls) must not stamp a v1 file as v2.
        await store.InitializeAsync();
        var meta = await store.GetMetaAsync();
        Assert.Equal("1", meta[MetaKeys.SchemaVersion]);
        Assert.Equal(2, (await store.LoadGraphAsync()).NodeCount);
    }

    [Fact]
    public async Task Analyze_PopulatesDeclaredAccessibility_AndLeavesNamespacesNull()
    {
        DotNet.Run($"restore \"{TestPaths.FixtureSolution}\"", TestPaths.RepoRoot);
        var snapshot = await new RoslynSolutionAnalyzer().AnalyzeAsync(TestPaths.FixtureSolution);

        var holder = Assert.Single(snapshot.Graph.Nodes, n => n.Fqn == "Fixture.Lib.FieldHolder");
        Assert.Equal("Public", holder.Accessibility);

        var knownTypes = Assert.Single(snapshot.Graph.Nodes, n => n.Fqn == "Fixture.Lib.FieldHolder.KnownTypes");
        Assert.Equal("Private", knownTypes.Accessibility);

        Assert.All(
            snapshot.Graph.Nodes.Where(n => n.Kind is NodeKind.Namespace or NodeKind.Project),
            n => Assert.Null(n.Accessibility));
    }

    [Fact]
    public async Task IncrementalRun_EvictsFactsOfRewalkedFiles_AndKeepsEveryOtherFilesFacts()
    {
        CopyDirectory(TestPaths.FixtureSolutionDirectory, _root);
        DotNet.Run("restore FixtureSolution.sln", _root);
        string solutionPath = Path.Combine(_root, "FixtureSolution.sln");
        var analyzer = new RoslynSolutionAnalyzer();
        var cold = await analyzer.AnalyzeAsync(solutionPath);

        // Nothing populates facts yet (Phase 1), so seed one per file: Fields.cs is touched and
        // re-walked; Shapes.cs is unrelated and must be carried over verbatim.
        string touchedFile = Path.Combine(_root, "FixtureLib", "Fields.cs");
        string untouchedFile = Path.Combine(_root, "FixtureLib", "Shapes.cs");
        var holder = Assert.Single(cold.Graph.Nodes, n => n.Fqn == "Fixture.Lib.FieldHolder");
        var shape = cold.Graph.Nodes.First(n => n.FilePath == untouchedFile && n.Kind != NodeKind.Namespace);

        var touchedFacts = (
            new ExternalCall(holder.Id, "System.Type.GetType(string)", "System", "System.Runtime", touchedFile, 1),
            new DiRegistration("Fixture.Lib.FieldHolder", null, "Singleton", DiRegistrationKinds.Factory, holder.Id, touchedFile, 2, "FixtureLib"),
            new AttributeUsage(holder.Id, "System.ObsoleteAttribute", touchedFile, 3));
        var keptFacts = (
            new ExternalCall(shape.Id, "System.Math.Sqrt(double)", "System", "System.Runtime", untouchedFile, 1),
            new DiRegistration("Fixture.Lib.IShape", shape.Fqn, "Scoped", DiRegistrationKinds.Generic, shape.Id, untouchedFile, 2, "FixtureLib"),
            new AttributeUsage(shape.Id, "System.SerializableAttribute", untouchedFile, 3));
        cold.Graph.AddExternalCall(touchedFacts.Item1);
        cold.Graph.AddDiRegistration(touchedFacts.Item2);
        cold.Graph.AddAttributeUsage(touchedFacts.Item3);
        cold.Graph.AddExternalCall(keptFacts.Item1);
        cold.Graph.AddDiRegistration(keptFacts.Item2);
        cold.Graph.AddAttributeUsage(keptFacts.Item3);

        File.AppendAllText(touchedFile, "\n");
        var incremental = await analyzer.AnalyzeAsync(solutionPath, cold);

        Assert.True(incremental.Stats.DocumentsSkipped > 0, "expected an incremental run, not a full rebuild");
        Assert.Contains(keptFacts.Item1, incremental.Graph.ExternalCalls);
        Assert.Contains(keptFacts.Item2, incremental.Graph.DiRegistrations);
        Assert.Contains(keptFacts.Item3, incremental.Graph.AttributeUsages);
        Assert.DoesNotContain(touchedFacts.Item1, incremental.Graph.ExternalCalls);
        Assert.DoesNotContain(touchedFacts.Item2, incremental.Graph.DiRegistrations);
        Assert.DoesNotContain(touchedFacts.Item3, incremental.Graph.AttributeUsages);
    }

    [Fact]
    public void FrontendIngestion_CarriesEveryExistingFactForward()
    {
        var graph = new CodeGraph();
        var method = SymbolNode.Create(NodeKind.Method, "Run", "Demo.Widget.Run()", "Widget.cs");
        graph.AddNode(method);
        graph.AddExternalCall(new ExternalCall(method.Id, "System.Console.WriteLine()", "System", null, "Widget.cs", 1));
        graph.AddAttributeUsage(new AttributeUsage(method.Id, "System.ObsoleteAttribute", "Widget.cs", 0));

        var merged = TsArtifactFacts.MergeIntoGraph(graph, []);

        Assert.True(merged.FactsEqual(graph));
    }

    [Fact]
    public void FactsEqual_DetectsAFactOnlyDifference()
    {
        // watch skips the save when the re-analyzed graph is unchanged; adding a framework
        // attribute changes no node or edge, so equality must see facts too.
        var a = new CodeGraph();
        var b = new CodeGraph();
        Assert.True(a.FactsEqual(b));

        b.AddAttributeUsage(new AttributeUsage("id", "System.ObsoleteAttribute", "A.cs", 0));
        Assert.False(a.FactsEqual(b));
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            if (relative.StartsWith("bin", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("obj", StringComparison.OrdinalIgnoreCase)
                || relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
