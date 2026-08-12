using System.Globalization;
using Microsoft.Data.Sqlite;
using Slnmap.Core.Graph;
using Slnmap.Core.Storage;

namespace Slnmap.Storage;

/// <summary>
/// SQLite-backed <see cref="IGraphStore"/> using Microsoft.Data.Sqlite and raw SQL (no ORM).
/// See <see cref="SqliteSchema"/> for the schema.
/// </summary>
/// <remarks>
/// Connections are opened per operation with pooling disabled, so no handle lingers to block the
/// atomic file swap in <see cref="SaveAsync"/>. Analysis is run-and-exit, so the per-open cost is
/// negligible; the served read path issues few, small queries.
/// </remarks>
public sealed class SqliteGraphStore : IGraphStore
{
    private readonly string _databasePath;

    public SqliteGraphStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = Path.GetFullPath(databasePath);
    }

    public string DatabasePath => _databasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        EnsureDirectory(_databasePath);
        await using var connection = await OpenAsync(_databasePath, cancellationToken).ConfigureAwait(false);
        await ApplySchemaAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveAsync(
        CodeGraph graph,
        IEnumerable<FileRecord> files,
        IReadOnlyDictionary<string, string> meta,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(meta);

        EnsureDirectory(_databasePath);
        string tempPath = _databasePath + ".tmp";
        DeleteDatabaseFiles(tempPath);
        try
        {
            await using (var connection = await OpenAsync(tempPath, cancellationToken).ConfigureAwait(false))
            {
                await ApplySchemaAsync(connection, cancellationToken).ConfigureAwait(false);
                await BulkInsertAsync(connection, graph, files, meta, cancellationToken).ConfigureAwait(false);

                // Fold the WAL back into the main file so the temp database is a single,
                // self-contained file that can be moved without its sidecars.
                await ExecuteAsync(connection, "PRAGMA wal_checkpoint(TRUNCATE);", cancellationToken).ConfigureAwait(false);
            }

            // The connection is closed and pooling is off, so nothing holds the temp file open.
            // The live database is untouched until this move — an interrupted build above only
            // leaves an orphaned temp file, never a corrupt graph.
            ReplaceDatabase(tempPath, _databasePath);
        }
        catch
        {
            DeleteDatabaseFiles(tempPath);
            throw;
        }
    }

    public async Task<CodeGraph> LoadGraphAsync(CancellationToken cancellationToken = default)
    {
        var graph = new CodeGraph();
        await using var connection = await OpenAsync(_databasePath, cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, kind, name, fqn, file, span_start, span_end FROM nodes;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                graph.AddNode(ReadNode(reader));
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT source_id, target_id, kind FROM edges;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                graph.AddEdge(new RelationshipEdge(
                    reader.GetString(0),
                    reader.GetString(1),
                    ParseEnum<RelationshipKind>(reader.GetString(2))));
            }
        }

        return graph;
    }

    public async Task<IReadOnlyList<SymbolNode>> GetNodesByFqnAsync(string fqn, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(fqn);

        await using var connection = await OpenAsync(_databasePath, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, kind, name, fqn, file, span_start, span_end FROM nodes WHERE fqn = $fqn;";
        command.Parameters.AddWithValue("$fqn", fqn);

        var results = new List<SymbolNode>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadNode(reader));
        }

        return results;
    }

    public async Task<IReadOnlyList<SymbolNode>> GetNodesByIdsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var distinct = ids.Distinct(StringComparer.Ordinal).ToList();
        var results = new List<SymbolNode>(distinct.Count);
        if (distinct.Count == 0)
        {
            return results;
        }

        string placeholders = string.Join(",", distinct.Select((_, i) => $"$p{i}"));
        await using var connection = await OpenAsync(_databasePath, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id, kind, name, fqn, file, span_start, span_end FROM nodes WHERE id IN ({placeholders});";
        for (int i = 0; i < distinct.Count; i++)
        {
            command.Parameters.AddWithValue($"$p{i}", distinct[i]);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadNode(reader));
        }

        return results;
    }

    public async Task<IReadOnlyList<SymbolNode>> GetNodesByKindAsync(NodeKind kind, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(_databasePath, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, kind, name, fqn, file, span_start, span_end FROM nodes WHERE kind = $kind;";
        command.Parameters.AddWithValue("$kind", kind.ToString());

        var results = new List<SymbolNode>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadNode(reader));
        }

        return results;
    }

    public async Task<IReadOnlyList<SymbolNode>> FindNodesAsync(
        string pattern,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(pattern);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        await using var connection = await OpenAsync(_databasePath, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, kind, name, fqn, file, span_start, span_end
            FROM nodes
            WHERE name LIKE $pattern OR fqn LIKE $pattern
            ORDER BY length(fqn), fqn
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$pattern", pattern);
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<SymbolNode>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(ReadNode(reader));
        }

        return results;
    }

    public async Task<IReadOnlyList<RelationshipEdge>> GetEdgesAsync(
        string nodeId,
        EdgeDirection direction,
        RelationshipKind? kind = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);

        string predicate = direction switch
        {
            EdgeDirection.Outgoing => "source_id = $id",
            EdgeDirection.Incoming => "target_id = $id",
            EdgeDirection.Both => "source_id = $id OR target_id = $id",
            _ => throw new ArgumentOutOfRangeException(nameof(direction)),
        };

        await using var connection = await OpenAsync(_databasePath, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT source_id, target_id, kind FROM edges WHERE ({predicate})"
            + (kind is null ? ";" : " AND kind = $kind;");
        command.Parameters.AddWithValue("$id", nodeId);
        if (kind is { } k)
        {
            command.Parameters.AddWithValue("$kind", k.ToString());
        }

        var results = new List<RelationshipEdge>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new RelationshipEdge(
                reader.GetString(0),
                reader.GetString(1),
                ParseEnum<RelationshipKind>(reader.GetString(2))));
        }

        return results;
    }

    public async Task<IReadOnlyList<ReachableNode>> TraverseAsync(
        string startId,
        EdgeDirection direction,
        int maxDepth = 5,
        int maxResults = 500,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startId);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResults);

        // Incoming = dependents (walk edges from target back to source); Outgoing = dependencies.
        // `next` is the column we step to; `prev` is the one we match the current frontier against.
        // Structural containment is excluded so traversal follows only real dependency edges.
        var (next, prev) = direction switch
        {
            EdgeDirection.Incoming => ("source_id", "target_id"),
            EdgeDirection.Outgoing => ("target_id", "source_id"),
            _ => throw new ArgumentOutOfRangeException(
                nameof(direction), "Traversal supports Incoming (dependents) or Outgoing (dependencies)."),
        };
        string containment = RelationshipKind.Contains.ToString();

        await using var connection = await OpenAsync(_databasePath, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            WITH RECURSIVE reach(id, depth) AS (
                SELECT {next}, 1
                FROM edges
                WHERE {prev} = $start AND kind <> $containment
                UNION
                SELECT e.{next}, r.depth + 1
                FROM edges e
                JOIN reach r ON e.{prev} = r.id
                WHERE e.kind <> $containment AND r.depth < $maxDepth
            )
            SELECT n.id, n.kind, n.name, n.fqn, n.file, n.span_start, n.span_end, MIN(r.depth) AS depth
            FROM reach r
            JOIN nodes n ON n.id = r.id
            WHERE n.id <> $start
            GROUP BY n.id
            ORDER BY depth, n.fqn
            LIMIT $maxResults;
            """;
        command.Parameters.AddWithValue("$start", startId);
        command.Parameters.AddWithValue("$containment", containment);
        command.Parameters.AddWithValue("$maxDepth", maxDepth);
        command.Parameters.AddWithValue("$maxResults", maxResults);

        var results = new List<ReachableNode>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new ReachableNode(ReadNode(reader), reader.GetInt32(7)));
        }

        return results;
    }

    public async Task<GraphStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(_databasePath, cancellationToken).ConfigureAwait(false);

        var nodesByKind = new Dictionary<NodeKind, int>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT kind, COUNT(*) FROM nodes GROUP BY kind;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                nodesByKind[ParseEnum<NodeKind>(reader.GetString(0))] = reader.GetInt32(1);
            }
        }

        var edgesByKind = new Dictionary<RelationshipKind, int>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT kind, COUNT(*) FROM edges GROUP BY kind;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                edgesByKind[ParseEnum<RelationshipKind>(reader.GetString(0))] = reader.GetInt32(1);
            }
        }

        var projects = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM nodes WHERE kind = $kind ORDER BY name;";
            command.Parameters.AddWithValue("$kind", NodeKind.Project.ToString());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                projects.Add(reader.GetString(0));
            }
        }

        return new GraphStatistics(
            nodesByKind.Values.Sum(),
            edgesByKind.Values.Sum(),
            nodesByKind,
            edgesByKind,
            projects);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetMetaAsync(CancellationToken cancellationToken = default) =>
        await ReadPairsAsync("SELECT key, value FROM meta;", cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyDictionary<string, string>> GetFileHashesAsync(CancellationToken cancellationToken = default) =>
        await ReadPairsAsync("SELECT path, content_hash FROM files;", cancellationToken).ConfigureAwait(false);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<IReadOnlyDictionary<string, string>> ReadPairsAsync(string sql, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var connection = await OpenAsync(_databasePath, cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result[reader.GetString(0)] = reader.GetString(1);
        }

        return result;
    }

    private static async Task BulkInsertAsync(
        SqliteConnection connection,
        CodeGraph graph,
        IEnumerable<FileRecord> files,
        IReadOnlyDictionary<string, string> meta,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO nodes (id, kind, name, fqn, file, span_start, span_end)
                VALUES ($id, $kind, $name, $fqn, $file, $start, $end);
                """;
            var id = command.Parameters.Add("$id", SqliteType.Text);
            var kind = command.Parameters.Add("$kind", SqliteType.Text);
            var name = command.Parameters.Add("$name", SqliteType.Text);
            var fqn = command.Parameters.Add("$fqn", SqliteType.Text);
            var file = command.Parameters.Add("$file", SqliteType.Text);
            var start = command.Parameters.Add("$start", SqliteType.Integer);
            var end = command.Parameters.Add("$end", SqliteType.Integer);
            await command.PrepareAsync(cancellationToken).ConfigureAwait(false);

            foreach (var node in graph.Nodes)
            {
                id.Value = node.Id;
                kind.Value = node.Kind.ToString();
                name.Value = node.Name;
                fqn.Value = node.Fqn;
                file.Value = (object?)node.FilePath ?? DBNull.Value;
                start.Value = node.Span is { } span ? span.Start : DBNull.Value;
                end.Value = node.Span is { } span2 ? span2.End : DBNull.Value;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO edges (source_id, target_id, kind)
                VALUES ($source, $target, $kind);
                """;
            var source = command.Parameters.Add("$source", SqliteType.Text);
            var target = command.Parameters.Add("$target", SqliteType.Text);
            var kind = command.Parameters.Add("$kind", SqliteType.Text);
            await command.PrepareAsync(cancellationToken).ConfigureAwait(false);

            foreach (var edge in graph.Edges)
            {
                source.Value = edge.SourceId;
                target.Value = edge.TargetId;
                kind.Value = edge.Kind.ToString();
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT OR IGNORE INTO files (path, content_hash) VALUES ($path, $hash);";
            var path = command.Parameters.Add("$path", SqliteType.Text);
            var hash = command.Parameters.Add("$hash", SqliteType.Text);
            await command.PrepareAsync(cancellationToken).ConfigureAwait(false);

            foreach (var record in files)
            {
                path.Value = record.Path;
                hash.Value = record.ContentHash;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ($key, $value);";
            var key = command.Parameters.Add("$key", SqliteType.Text);
            var value = command.Parameters.Add("$value", SqliteType.Text);
            await command.PrepareAsync(cancellationToken).ConfigureAwait(false);

            foreach (var (metaKey, metaValue) in meta)
            {
                key.Value = metaKey;
                value.Value = metaValue;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ApplySchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);
        await ExecuteAsync(connection, SqliteSchema.Ddl, cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO meta (key, value) VALUES ($key, $value);";
        command.Parameters.AddWithValue("$key", MetaKeys.SchemaVersion);
        command.Parameters.AddWithValue("$value", SqliteSchema.Version.ToString(CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<SqliteConnection> OpenAsync(string path, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            Cache = SqliteCacheMode.Private,
        }.ToString();

        // A concurrent analysis replaces the database file with an atomic swap. A reader that opens
        // during that instant can transiently fail (file briefly missing / locked); reopen a few
        // times so a running server survives a re-analysis between (or during) queries. Lock waits
        // once open are absorbed by busy_timeout below.
        const int maxAttempts = 5;
        for (int attempt = 1; ; attempt++)
        {
            var connection = new SqliteConnection(connectionString);
            try
            {
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection, "PRAGMA busy_timeout = 5000;", cancellationToken).ConfigureAwait(false);
                return connection;
            }
            catch (Exception e) when (attempt < maxAttempts && IsTransient(e))
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMilliseconds(30 * attempt), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <summary>Errors that a database file swap can cause transiently: SQLite BUSY/LOCKED/CANTOPEN, or an OS file race.</summary>
    private static bool IsTransient(Exception e) =>
        e is SqliteException { SqliteErrorCode: 5 or 6 or 14 } or IOException or FileNotFoundException;

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SymbolNode ReadNode(SqliteDataReader reader)
    {
        string? file = reader.IsDBNull(4) ? null : reader.GetString(4);
        SourceSpan? span = reader.IsDBNull(5) || reader.IsDBNull(6)
            ? null
            : new SourceSpan(reader.GetInt32(5), reader.GetInt32(6));
        return new SymbolNode(
            reader.GetString(0),
            ParseEnum<NodeKind>(reader.GetString(1)),
            reader.GetString(2),
            reader.GetString(3),
            file,
            span);
    }

    /// <summary>
    /// Kind names are written by whichever slnmap version built the database; a NEWER version may
    /// have appended members this binary does not know (Endpoint was the first, v0.7.0). A bare
    /// Enum.Parse would crash every read path on such rows — map them onto the enum's Unknown
    /// member instead (NodeKind.Unknown / RelationshipKind.Unknown) and warn once per unknown name,
    /// so an older binary (or a long-running older MCP server) degrades gracefully.
    /// </summary>
    private static TEnum ParseEnum<TEnum>(string value) where TEnum : struct, Enum
    {
        if (Enum.TryParse(value, ignoreCase: false, out TEnum parsed))
        {
            return parsed;
        }

        if (WarnedUnknownKinds.TryAdd($"{typeof(TEnum).Name}:{value}", true))
        {
            Console.Error.WriteLine(
                $"warning: unknown {typeof(TEnum).Name} '{value}' in the graph database — written by a newer slnmap version? " +
                "Treating it as Unknown; re-run 'slnmap analyze' with this version to rebuild.");
        }

        return Enum.TryParse("Unknown", ignoreCase: false, out TEnum unknown) ? unknown : default;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> WarnedUnknownKinds = new(StringComparer.Ordinal);

    private static void EnsureDirectory(string databasePath)
    {
        string? directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    private static void ReplaceDatabase(string tempPath, string mainPath)
    {
        // Sidecars belonging to the OLD main database must not survive next to the NEW file,
        // or the next open would read a WAL that no longer matches.
        DeleteSidecars(mainPath);
        File.Move(tempPath, mainPath, overwrite: true);
        DeleteSidecars(tempPath);
    }

    private static void DeleteDatabaseFiles(string path)
    {
        TryDelete(path);
        DeleteSidecars(path);
    }

    private static void DeleteSidecars(string path)
    {
        TryDelete(path + "-wal");
        TryDelete(path + "-shm");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of a temp/sidecar file; never mask the real error behind it.
        }
    }
}
