using Microsoft.Data.Sqlite;
using Slnmap.Core.Graph;
using Slnmap.Core.Storage;
using Slnmap.Storage;
using Xunit;

namespace Slnmap.Tests;

/// <summary>Storage-layer tests: round-trip persistence, queries, the recursive traversal, and crash safety.</summary>
public sealed class SqliteGraphStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "slnmap-store-tests", Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_directory, "graph.db");

    private static readonly IReadOnlyDictionary<string, string> NoMeta = new Dictionary<string, string>();

    private SqliteGraphStore NewStore() => new(DbPath);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the OS temp cleaner will get it eventually.
        }
    }

    private static SymbolNode Type(string name, NodeKind kind = NodeKind.Class) =>
        SymbolNode.Create(kind, name, $"N.{name}", $"{name}.cs", new SourceSpan(0, 1));

    [Fact]
    public async Task Initialize_CreatesEmptyGraphWithSchemaVersion()
    {
        await using var store = NewStore();
        await store.InitializeAsync();

        var stats = await store.GetStatisticsAsync();
        Assert.Equal(0, stats.NodeCount);
        Assert.Equal(0, stats.EdgeCount);

        var meta = await store.GetMetaAsync();
        Assert.Equal("1", meta[MetaKeys.SchemaVersion]);
    }

    [Fact]
    public async Task Save_RoundTripsNodesEdgesFilesAndMeta()
    {
        var graph = new CodeGraph();
        var contract = SymbolNode.Create(NodeKind.Interface, "IShape", "Lib.IShape", "Shapes.cs", new SourceSpan(10, 40));
        var circle = SymbolNode.Create(NodeKind.Class, "Circle", "Lib.Circle", "Shapes.cs", new SourceSpan(50, 120));
        var ns = SymbolNode.Create(NodeKind.Namespace, "Lib", "Lib"); // no file / span
        graph.AddNode(contract);
        graph.AddNode(circle);
        graph.AddNode(ns);
        graph.AddEdge(new RelationshipEdge(circle.Id, contract.Id, RelationshipKind.Implements));
        graph.AddEdge(new RelationshipEdge(ns.Id, circle.Id, RelationshipKind.Contains));

        var meta = new Dictionary<string, string> { [MetaKeys.SolutionPath] = "X.sln" };

        await using var store = NewStore();
        await store.SaveAsync(graph, [new FileRecord("Shapes.cs", "hash123")], meta);

        var loaded = await store.LoadGraphAsync();
        Assert.Equal(3, loaded.NodeCount);
        Assert.Equal(2, loaded.EdgeCount);

        Assert.True(loaded.TryGetNode(contract.Id, out var loadedContract));
        Assert.Equal(NodeKind.Interface, loadedContract!.Kind);
        Assert.Equal("Lib.IShape", loadedContract.Fqn);
        Assert.Equal("Shapes.cs", loadedContract.FilePath);
        Assert.Equal(new SourceSpan(10, 40), loadedContract.Span);

        // Symbols without a single location (namespaces) round-trip with null file/span.
        Assert.True(loaded.TryGetNode(ns.Id, out var loadedNs));
        Assert.Null(loadedNs!.FilePath);
        Assert.Null(loadedNs.Span);

        var hashes = await store.GetFileHashesAsync();
        Assert.Equal("hash123", hashes["Shapes.cs"]);

        var readMeta = await store.GetMetaAsync();
        Assert.Equal("X.sln", readMeta[MetaKeys.SolutionPath]);
        Assert.Equal("1", readMeta[MetaKeys.SchemaVersion]);
    }

    [Fact]
    public async Task UnknownKindNames_DegradeToUnknown_InsteadOfCrashing()
    {
        // Forward compatibility (endpoint-nodes design §2.1): a database written by a NEWER slnmap
        // may contain kind names this binary's enums lack — Endpoint/HandledBy were the first ever.
        // Every read path must degrade to the Unknown member, not crash in Enum.Parse.
        var a = Type("A");
        var b = Type("B");
        var graph = new CodeGraph();
        graph.AddNode(a);
        graph.AddNode(b);
        graph.AddEdge(new RelationshipEdge(a.Id, b.Id, RelationshipKind.Calls));

        await using var store = NewStore();
        await store.SaveAsync(graph, [], NoMeta);

        // Simulate the newer writer: rewrite one node kind and one edge kind to future names.
        var connectionString = new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE nodes SET kind = 'HologramProjector' WHERE id = $a;
                UPDATE edges SET kind = 'Teleports';
                """;
            command.Parameters.AddWithValue("$a", a.Id);
            await command.ExecuteNonQueryAsync();
        }

        var loaded = await store.LoadGraphAsync();
        Assert.True(loaded.TryGetNode(a.Id, out var futureNode));
        Assert.Equal(NodeKind.Unknown, futureNode!.Kind);
        Assert.True(loaded.TryGetNode(b.Id, out var intactNode));
        Assert.Equal(NodeKind.Class, intactNode!.Kind);
        Assert.Equal(RelationshipKind.Unknown, Assert.Single(loaded.Edges).Kind);

        // The aggregating paths must survive too.
        var stats = await store.GetStatisticsAsync();
        Assert.Equal(1, stats.NodesByKind[NodeKind.Unknown]);
        Assert.Equal(1, stats.EdgesByKind[RelationshipKind.Unknown]);

        var byIds = await store.GetNodesByIdsAsync([a.Id]);
        Assert.Equal(NodeKind.Unknown, Assert.Single(byIds).Kind);
    }

    [Fact]
    public async Task FindNodes_MatchesNameOrFqn_AndRespectsLimit()
    {
        var graph = new CodeGraph();
        for (int i = 0; i < 5; i++)
        {
            graph.AddNode(Type($"Widget{i}"));
        }

        graph.AddNode(Type("Gadget"));

        await using var store = NewStore();
        await store.SaveAsync(graph, [], NoMeta);

        var widgets = await store.FindNodesAsync("Widget%");
        Assert.Equal(5, widgets.Count);
        Assert.All(widgets, n => Assert.StartsWith("Widget", n.Name));

        var limited = await store.FindNodesAsync("%", limit: 2);
        Assert.Equal(2, limited.Count);

        // No wildcards: matches the fully qualified name exactly (name alone is just "Gadget").
        var byFqn = await store.FindNodesAsync("N.Gadget");
        Assert.Single(byFqn);
        Assert.Equal("Gadget", byFqn[0].Name);
    }

    [Fact]
    public async Task GetEdges_FiltersByDirectionAndKind()
    {
        var a = Type("A");
        var b = Type("B");
        var c = Type("I", NodeKind.Interface);
        var graph = new CodeGraph();
        graph.AddNode(a);
        graph.AddNode(b);
        graph.AddNode(c);
        graph.AddEdge(new RelationshipEdge(a.Id, b.Id, RelationshipKind.Calls));
        graph.AddEdge(new RelationshipEdge(a.Id, c.Id, RelationshipKind.Implements));
        graph.AddEdge(new RelationshipEdge(b.Id, a.Id, RelationshipKind.Calls));

        await using var store = NewStore();
        await store.SaveAsync(graph, [], NoMeta);

        Assert.Equal(2, (await store.GetEdgesAsync(a.Id, EdgeDirection.Outgoing)).Count);

        var incoming = await store.GetEdgesAsync(a.Id, EdgeDirection.Incoming);
        Assert.Single(incoming);
        Assert.Equal(b.Id, incoming[0].SourceId);

        var calls = await store.GetEdgesAsync(a.Id, EdgeDirection.Outgoing, RelationshipKind.Calls);
        Assert.Single(calls);
        Assert.Equal(b.Id, calls[0].TargetId);

        Assert.Equal(3, (await store.GetEdgesAsync(a.Id, EdgeDirection.Both)).Count);
    }

    [Fact]
    public async Task Traverse_TagsDepth_RespectsCaps_ExcludesContainment()
    {
        // Call chain M0 -> M1 -> ... -> M10 (each calls the next).
        var graph = new CodeGraph();
        var chain = new List<SymbolNode>();
        for (int i = 0; i <= 10; i++)
        {
            var node = SymbolNode.Create(NodeKind.Method, $"M{i}", $"N.M{i:D2}()", "m.cs", new SourceSpan(i, i + 1));
            chain.Add(node);
            graph.AddNode(node);
        }

        for (int i = 0; i < 10; i++)
        {
            graph.AddEdge(new RelationshipEdge(chain[i].Id, chain[i + 1].Id, RelationshipKind.Calls));
        }

        // A containment edge into the tail must NOT be traversed.
        var host = SymbolNode.Create(NodeKind.Class, "Host", "N.Host", "h.cs", new SourceSpan(0, 1));
        graph.AddNode(host);
        graph.AddEdge(new RelationshipEdge(host.Id, chain[10].Id, RelationshipKind.Contains));

        await using var store = NewStore();
        await store.SaveAsync(graph, [], NoMeta);

        // Dependents of the tail, depth-capped at 5: M9(d1), M8(d2), M7(d3), M6(d4), M5(d5).
        var dependents = await store.TraverseAsync(chain[10].Id, EdgeDirection.Incoming, maxDepth: 5, maxResults: 500);
        Assert.Equal(5, dependents.Count);
        Assert.Equal(chain[9].Id, dependents[0].Node.Id);
        Assert.Equal(1, dependents[0].Depth);
        Assert.Equal(5, dependents[^1].Depth);
        Assert.DoesNotContain(dependents, r => r.Node.Id == host.Id);

        // Result cap is honored independently of depth.
        var capped = await store.TraverseAsync(chain[10].Id, EdgeDirection.Incoming, maxDepth: 100, maxResults: 3);
        Assert.Equal(3, capped.Count);

        // Outgoing dependencies from the head, depth-capped at 4: M1..M4.
        var dependencies = await store.TraverseAsync(chain[0].Id, EdgeDirection.Outgoing, maxDepth: 4, maxResults: 500);
        Assert.Equal(4, dependencies.Count);
        Assert.Equal(chain[1].Id, dependencies[0].Node.Id);
    }

    [Fact]
    public async Task Save_FailingMidWrite_LeavesPreviousGraphIntact()
    {
        var keep = Type("Keep");
        var v1 = new CodeGraph();
        v1.AddNode(keep);

        await using var store = NewStore();
        await store.SaveAsync(v1, [], NoMeta);

        // The file records are enumerated during the write transaction, after nodes and edges have
        // gone into the temp database but before it is committed and swapped in. Throwing partway
        // through deterministically interrupts the write mid-flight — no timing races.
        var v2 = new CodeGraph();
        v2.AddNode(Type("New"));
        string tempPath = store.DatabasePath + ".tmp";

        await Assert.ThrowsAsync<IOException>(() => store.SaveAsync(v2, FailPartwayThrough(), NoMeta));

        // The live graph is untouched and the partial temp database was cleaned up.
        var loaded = await store.LoadGraphAsync();
        Assert.Equal(1, loaded.NodeCount);
        Assert.True(loaded.ContainsNode(keep.Id));
        Assert.False(File.Exists(tempPath));

        static IEnumerable<FileRecord> FailPartwayThrough()
        {
            yield return new FileRecord("first.cs", "hash");
            throw new IOException("simulated failure mid-write");
        }
    }

    [Fact]
    public async Task Save_ReplacesLeftoverTempFromCrashedRun_WithoutReadingIt()
    {
        var keep = Type("Keep");
        var v1 = new CodeGraph();
        v1.AddNode(keep);

        await using var store = NewStore();
        await store.SaveAsync(v1, [], NoMeta);

        // Simulate a crashed prior run: a stale temp file left next to the live database.
        await File.WriteAllTextAsync(store.DatabasePath + ".tmp", "garbage-not-a-database");

        // The live graph is authoritative regardless of the stale temp...
        Assert.Equal(1, (await store.LoadGraphAsync()).NodeCount);

        // ...and the next save cleans the temp up and succeeds.
        var v2 = new CodeGraph();
        v2.AddNode(keep);
        v2.AddNode(Type("Add"));
        await store.SaveAsync(v2, [], NoMeta);

        Assert.Equal(2, (await store.LoadGraphAsync()).NodeCount);
        Assert.False(File.Exists(store.DatabasePath + ".tmp"));
    }

    [Fact]
    public async Task Save_ReflectsIncrementalChanges_AddedRemovedPreserved()
    {
        var keep = Type("Keep");
        var drop = Type("Drop");
        var v1 = new CodeGraph();
        v1.AddNode(keep);
        v1.AddNode(drop);

        await using var store = NewStore();
        await store.SaveAsync(v1, [new FileRecord("Keep.cs", "h1"), new FileRecord("Drop.cs", "h1")], NoMeta);

        // Incremental result: Drop removed, Add introduced, Keep preserved.
        var add = Type("Add");
        var v2 = new CodeGraph();
        v2.AddNode(keep);
        v2.AddNode(add);
        await store.SaveAsync(v2, [new FileRecord("Keep.cs", "h1"), new FileRecord("Add.cs", "h2")], NoMeta);

        var loaded = await store.LoadGraphAsync();
        Assert.True(loaded.ContainsNode(keep.Id));
        Assert.True(loaded.ContainsNode(add.Id));
        Assert.False(loaded.ContainsNode(drop.Id));

        var hashes = await store.GetFileHashesAsync();
        Assert.Equal(new[] { "Add.cs", "Keep.cs" }, hashes.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("h2", hashes["Add.cs"]);
    }

    [Fact]
    public async Task WalMode_AllowsConcurrentReadDuringWrite()
    {
        var graph = new CodeGraph();
        graph.AddNode(Type("A"));

        await using var store = NewStore();
        await store.SaveAsync(graph, [], NoMeta);

        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = store.DatabasePath,
            Pooling = false,
        }.ToString();

        await using var writer = new SqliteConnection(connectionString);
        await writer.OpenAsync();

        await using (var pragma = writer.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode;";
            string mode = (string)(await pragma.ExecuteScalarAsync())!;
            Assert.Equal("wal", mode.ToLowerInvariant());
        }

        await using var transaction = (SqliteTransaction)await writer.BeginTransactionAsync();
        await using (var insert = writer.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = "INSERT OR REPLACE INTO meta(key, value) VALUES('probe', '1');";
            await insert.ExecuteNonQueryAsync();
        }

        // A separate reader queries the committed snapshot while the write is open — WAL means no lock error.
        await using var reader = new SqliteConnection(connectionString);
        await reader.OpenAsync();
        await using var read = reader.CreateCommand();
        read.CommandText = "SELECT COUNT(*) FROM nodes;";
        Assert.Equal(1, Convert.ToInt32(await read.ExecuteScalarAsync()));

        await transaction.RollbackAsync();
    }
}
