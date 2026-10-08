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

    /// <summary>The schema version this binary writes (stored in <c>meta('schema_version')</c>).</summary>
    public static int CurrentSchemaVersion => SqliteSchema.Version;

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

        // A v1 database (built by an older slnmap, not yet rebuilt) has neither the accessibility
        // column nor the fact tables; read what is there rather than failing the whole load.
        bool hasV2Columns = await HasColumnAsync(connection, "nodes", "member_flags", cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = hasV2Columns
                ? "SELECT id, kind, name, fqn, file, span_start, span_end, accessibility, member_flags FROM nodes;"
                : "SELECT id, kind, name, fqn, file, span_start, span_end FROM nodes;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var node = ReadNode(reader);
                if (hasV2Columns)
                {
                    node = node with
                    {
                        Accessibility = reader.IsDBNull(7) ? null : reader.GetString(7),
                        MemberFlags = reader.IsDBNull(8) ? null : reader.GetString(8),
                    };
                }

                graph.AddNode(node);
            }
        }

        await LoadFactsAsync(connection, graph, cancellationToken).ConfigureAwait(false);

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
                INSERT OR IGNORE INTO nodes (id, kind, name, fqn, file, span_start, span_end, accessibility, member_flags)
                VALUES ($id, $kind, $name, $fqn, $file, $start, $end, $accessibility, $memberFlags);
                """;
            var id = command.Parameters.Add("$id", SqliteType.Text);
            var kind = command.Parameters.Add("$kind", SqliteType.Text);
            var name = command.Parameters.Add("$name", SqliteType.Text);
            var fqn = command.Parameters.Add("$fqn", SqliteType.Text);
            var file = command.Parameters.Add("$file", SqliteType.Text);
            var start = command.Parameters.Add("$start", SqliteType.Integer);
            var end = command.Parameters.Add("$end", SqliteType.Integer);
            var accessibility = command.Parameters.Add("$accessibility", SqliteType.Text);
            var memberFlags = command.Parameters.Add("$memberFlags", SqliteType.Text);
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
                accessibility.Value = (object?)node.Accessibility ?? DBNull.Value;
                memberFlags.Value = (object?)node.MemberFlags ?? DBNull.Value;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await InsertFactsAsync(connection, transaction, graph, cancellationToken).ConfigureAwait(false);

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
                // The schema version describes the file this store just built, not anything a
                // caller carried forward: callers copy the previous meta table wholesale to keep
                // producer state, which would otherwise overwrite the fresh version with the old one.
                if (metaKey == MetaKeys.SchemaVersion)
                {
                    continue;
                }

                key.Value = metaKey;
                value.Value = metaValue;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task InsertFactsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CodeGraph graph,
        CancellationToken cancellationToken)
    {
        // External targets are stored once each and referenced by id: the same framework method
        // is called from hundreds of places, and its FQN/namespace/assembly strings dominated the
        // per-call row size (reports/v0140-gate7a-external-calls-budget.md).
        var targetIds = new Dictionary<string, long>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO external_targets (id, fqn, namespace, assembly) VALUES ($id, $fqn, $namespace, $assembly);";
            var id = command.Parameters.Add("$id", SqliteType.Integer);
            var fqn = command.Parameters.Add("$fqn", SqliteType.Text);
            var ns = command.Parameters.Add("$namespace", SqliteType.Text);
            var assembly = command.Parameters.Add("$assembly", SqliteType.Text);
            await command.PrepareAsync(cancellationToken).ConfigureAwait(false);

            foreach (var call in graph.ExternalCalls)
            {
                if (targetIds.ContainsKey(call.TargetFqn))
                {
                    continue;
                }

                long next = targetIds.Count + 1;
                targetIds.Add(call.TargetFqn, next);
                id.Value = next;
                fqn.Value = call.TargetFqn;
                ns.Value = call.TargetNamespace;
                assembly.Value = (object?)call.TargetAssembly ?? DBNull.Value;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO external_calls (caller_id, target_id, call_count, file, span_start)
                VALUES ($caller, $target, $count, $file, $start);
                """;
            var caller = command.Parameters.Add("$caller", SqliteType.Text);
            var target = command.Parameters.Add("$target", SqliteType.Integer);
            var count = command.Parameters.Add("$count", SqliteType.Integer);
            var file = command.Parameters.Add("$file", SqliteType.Text);
            var start = command.Parameters.Add("$start", SqliteType.Integer);
            await command.PrepareAsync(cancellationToken).ConfigureAwait(false);

            foreach (var call in graph.ExternalCalls)
            {
                caller.Value = call.CallerId;
                target.Value = targetIds[call.TargetFqn];
                count.Value = call.CallCount;
                file.Value = call.FilePath;
                start.Value = call.SpanStart;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO di_registrations (service_fqn, impl_fqn, lifetime, registration_kind, caller_id, file, span_start, project)
                VALUES ($service, $impl, $lifetime, $kind, $caller, $file, $start, $project);
                """;
            var service = command.Parameters.Add("$service", SqliteType.Text);
            var impl = command.Parameters.Add("$impl", SqliteType.Text);
            var lifetime = command.Parameters.Add("$lifetime", SqliteType.Text);
            var kind = command.Parameters.Add("$kind", SqliteType.Text);
            var caller = command.Parameters.Add("$caller", SqliteType.Text);
            var file = command.Parameters.Add("$file", SqliteType.Text);
            var start = command.Parameters.Add("$start", SqliteType.Integer);
            var project = command.Parameters.Add("$project", SqliteType.Text);
            await command.PrepareAsync(cancellationToken).ConfigureAwait(false);

            foreach (var registration in graph.DiRegistrations)
            {
                service.Value = registration.ServiceFqn;
                impl.Value = (object?)registration.ImplementationFqn ?? DBNull.Value;
                lifetime.Value = registration.Lifetime;
                kind.Value = registration.RegistrationKind;
                caller.Value = (object?)registration.CallerId ?? DBNull.Value;
                file.Value = registration.FilePath;
                start.Value = registration.SpanStart;
                project.Value = registration.Project;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO attribute_usages (target_id, attribute_fqn, file, span_start)
                VALUES ($target, $attribute, $file, $start);
                """;
            var target = command.Parameters.Add("$target", SqliteType.Text);
            var attribute = command.Parameters.Add("$attribute", SqliteType.Text);
            var file = command.Parameters.Add("$file", SqliteType.Text);
            var start = command.Parameters.Add("$start", SqliteType.Integer);
            await command.PrepareAsync(cancellationToken).ConfigureAwait(false);

            foreach (var usage in graph.AttributeUsages)
            {
                target.Value = usage.TargetId;
                attribute.Value = usage.AttributeFqn;
                file.Value = usage.FilePath;
                start.Value = usage.SpanStart;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO disclosures (kind, detail, file, span_start) VALUES ($kind, $detail, $file, $start);";
            var kind = command.Parameters.Add("$kind", SqliteType.Text);
            var detail = command.Parameters.Add("$detail", SqliteType.Text);
            var file = command.Parameters.Add("$file", SqliteType.Text);
            var start = command.Parameters.Add("$start", SqliteType.Integer);
            await command.PrepareAsync(cancellationToken).ConfigureAwait(false);

            foreach (var disclosure in graph.Disclosures)
            {
                kind.Value = disclosure.Kind;
                detail.Value = disclosure.Detail;
                file.Value = disclosure.FilePath;
                start.Value = disclosure.SpanStart;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Loads the fact tables into <paramref name="graph"/>; each one absent from a v1 database is skipped.</summary>
    private static async Task LoadFactsAsync(SqliteConnection connection, CodeGraph graph, CancellationToken cancellationToken)
    {
        if (await HasTableAsync(connection, "external_targets", cancellationToken).ConfigureAwait(false))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT c.caller_id, t.fqn, t.namespace, t.assembly, c.file, c.span_start, c.call_count
                FROM external_calls c JOIN external_targets t ON t.id = c.target_id;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                graph.AddExternalCall(new ExternalCall(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.GetString(4),
                    reader.GetInt32(5),
                    reader.GetInt32(6)));
            }
        }

        if (await HasTableAsync(connection, "di_registrations", cancellationToken).ConfigureAwait(false))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT service_fqn, impl_fqn, lifetime, registration_kind, caller_id, file, span_start, project FROM di_registrations;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                graph.AddDiRegistration(new DiRegistration(
                    reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt32(6),
                    reader.GetString(7)));
            }
        }

        if (await HasTableAsync(connection, "attribute_usages", cancellationToken).ConfigureAwait(false))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT target_id, attribute_fqn, file, span_start FROM attribute_usages;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                graph.AddAttributeUsage(new AttributeUsage(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3)));
            }
        }

        foreach (var disclosure in await ReadDisclosuresAsync(connection, kind: null, cancellationToken).ConfigureAwait(false))
        {
            graph.AddDisclosure(disclosure);
        }
    }

    public async Task<IReadOnlyList<ExternalCall>> GetExternalCallsAsync(string prefix, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var results = new List<ExternalCall>();
        await using var connection = await OpenAsync(_databasePath, cancellationToken).ConfigureAwait(false);
        if (!await HasTableAsync(connection, "external_targets", cancellationToken).ConfigureAwait(false))
        {
            return results;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.caller_id, t.fqn, t.namespace, t.assembly, c.file, c.span_start, c.call_count
            FROM external_calls c JOIN external_targets t ON t.id = c.target_id
            WHERE t.namespace = $prefix COLLATE NOCASE
               OR t.namespace LIKE $like ESCAPE '\'
               OR t.assembly = $prefix COLLATE NOCASE
            ORDER BY c.file, c.span_start;
            """;
        command.Parameters.AddWithValue("$prefix", prefix);
        command.Parameters.AddWithValue("$like", prefix.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + ".%");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new ExternalCall(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetInt32(6)));
        }

        return results;
    }

    public async Task<IReadOnlyList<(string Namespace, int Pairs)>> GetExternalNamespacesAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<(string, int)>();
        await using var connection = await OpenAsync(_databasePath, cancellationToken).ConfigureAwait(false);
        if (!await HasTableAsync(connection, "external_targets", cancellationToken).ConfigureAwait(false))
        {
            return results;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT t.namespace, COUNT(*) FROM external_calls c JOIN external_targets t ON t.id = c.target_id
            GROUP BY t.namespace ORDER BY COUNT(*) DESC;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add((reader.GetString(0), reader.GetInt32(1)));
        }

        return results;
    }

    public async Task<IReadOnlyList<DiRegistration>> GetDiRegistrationsAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<DiRegistration>();
        await using var connection = await OpenAsync(_databasePath, cancellationToken).ConfigureAwait(false);
        if (!await HasTableAsync(connection, "di_registrations", cancellationToken).ConfigureAwait(false))
        {
            return results;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT service_fqn, impl_fqn, lifetime, registration_kind, caller_id, file, span_start, project
            FROM di_registrations
            ORDER BY project, file, span_start;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new DiRegistration(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetString(5),
                reader.GetInt32(6),
                reader.GetString(7)));
        }

        return results;
    }

    public async Task<IReadOnlyList<(string AttributeFqn, int Count)>> GetAttributeSummaryAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<(string, int)>();
        await using var connection = await OpenAsync(_databasePath, cancellationToken).ConfigureAwait(false);
        if (!await HasTableAsync(connection, "attribute_usages", cancellationToken).ConfigureAwait(false))
        {
            return results;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT attribute_fqn, COUNT(*) FROM attribute_usages GROUP BY attribute_fqn ORDER BY attribute_fqn;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add((reader.GetString(0), reader.GetInt32(1)));
        }

        return results;
    }

    public async Task<IReadOnlyList<AttributeUsage>> GetAttributeUsagesAsync(string attributeFqn, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(attributeFqn);
        var results = new List<AttributeUsage>();
        await using var connection = await OpenAsync(_databasePath, cancellationToken).ConfigureAwait(false);
        if (!await HasTableAsync(connection, "attribute_usages", cancellationToken).ConfigureAwait(false))
        {
            return results;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT target_id, attribute_fqn, file, span_start FROM attribute_usages
            WHERE attribute_fqn = $fqn
            ORDER BY file, span_start;
            """;
        command.Parameters.AddWithValue("$fqn", attributeFqn);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new AttributeUsage(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        }

        return results;
    }

    public async Task<IReadOnlyList<SymbolNode>> GetUnreferencedNodesAsync(
        IReadOnlyCollection<NodeKind> kinds,
        IReadOnlyCollection<string> accessibilities,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        ArgumentNullException.ThrowIfNull(accessibilities);
        var results = new List<SymbolNode>();
        if (kinds.Count == 0 || accessibilities.Count == 0)
        {
            return results;
        }

        await using var connection = await OpenAsync(_databasePath, cancellationToken).ConfigureAwait(false);
        if (!await HasColumnAsync(connection, "nodes", "member_flags", cancellationToken).ConfigureAwait(false))
        {
            return results;
        }

        var kindNames = kinds.Select(static k => k.ToString()).ToHashSet(StringComparer.Ordinal);
        var accessNames = accessibilities.ToHashSet(StringComparer.Ordinal);

        // Candidates: the requested kinds and accessibilities.
        var candidates = new List<SymbolNode>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, kind, name, fqn, file, span_start, span_end, accessibility, member_flags FROM nodes WHERE accessibility IS NOT NULL;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!kindNames.Contains(reader.GetString(1)) || !accessNames.Contains(reader.GetString(7)))
                {
                    continue;
                }

                candidates.Add(ReadNode(reader) with
                {
                    Accessibility = reader.GetString(7),
                    MemberFlags = reader.IsDBNull(8) ? null : reader.GetString(8),
                });
            }
        }

        // A node is used when it, or anything it transitively contains, is depended on from
        // OUTSIDE that subtree: a static class whose extension methods are called is never named
        // itself, a type that only groups nested types is used through them — but a type whose
        // members only call each other, or a recursive method, is not used by anything.
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT source_id, target_id, kind FROM edges;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            string containment = RelationshipKind.Contains.ToString();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var index = reader.GetString(2) == containment ? children : dependents;
                string key = index == children ? reader.GetString(0) : reader.GetString(1);
                string value = index == children ? reader.GetString(1) : reader.GetString(0);
                if (!index.TryGetValue(key, out var list))
                {
                    list = [];
                    index.Add(key, list);
                }

                list.Add(value);
            }
        }

        foreach (var candidate in candidates.OrderBy(static n => n.Fqn, StringComparer.Ordinal))
        {
            var subtree = new HashSet<string>(StringComparer.Ordinal) { candidate.Id };
            var stack = new Stack<string>();
            stack.Push(candidate.Id);
            while (stack.Count > 0)
            {
                if (children.TryGetValue(stack.Pop(), out var kids))
                {
                    foreach (string kid in kids)
                    {
                        if (subtree.Add(kid))
                        {
                            stack.Push(kid);
                        }
                    }
                }
            }

            bool usedFromOutside = subtree.Any(id =>
                dependents.TryGetValue(id, out var sources) && sources.Any(source => !subtree.Contains(source)));
            if (!usedFromOutside)
            {
                results.Add(candidate);
            }
        }

        return results;
    }

    public async Task<IReadOnlyList<Disclosure>> GetDisclosuresAsync(string kind, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        await using var connection = await OpenAsync(_databasePath, cancellationToken).ConfigureAwait(false);
        return await ReadDisclosuresAsync(connection, kind, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<Disclosure>> ReadDisclosuresAsync(
        SqliteConnection connection, string? kind, CancellationToken cancellationToken)
    {
        var results = new List<Disclosure>();
        if (!await HasTableAsync(connection, "disclosures", cancellationToken).ConfigureAwait(false))
        {
            return results;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = kind is null
            ? "SELECT kind, detail, file, span_start FROM disclosures ORDER BY file, span_start;"
            : "SELECT kind, detail, file, span_start FROM disclosures WHERE kind = $kind ORDER BY file, span_start;";
        if (kind is not null)
        {
            command.Parameters.AddWithValue("$kind", kind);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new Disclosure(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
        }

        return results;
    }

    private static async Task<bool> HasTableAsync(SqliteConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task<bool> HasColumnAsync(SqliteConnection connection, string table, string column, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM pragma_table_info($table) WHERE name = $column;";
        command.Parameters.AddWithValue("$table", table);
        command.Parameters.AddWithValue("$column", column);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
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
