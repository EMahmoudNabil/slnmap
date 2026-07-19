namespace Slnmap.Storage;

/// <summary>DDL and versioning for the Slnmap SQLite database.</summary>
internal static class SqliteSchema
{
    /// <summary>
    /// Current schema version, stored in <c>meta('schema_version')</c>. Bump whenever the DDL below
    /// changes in a way that makes an existing database unreadable.
    /// </summary>
    public const int Version = 1;

    /// <summary>
    /// Schema per the E3 plan. <c>nodes.kind</c> / <c>edges.kind</c> store the enum member
    /// <em>name</em> (e.g. <c>"Class"</c>, <c>"Calls"</c>) as TEXT; span is split into
    /// <c>span_start</c> / <c>span_end</c>, both null for symbols without a single location.
    /// </summary>
    public const string Ddl = """
        CREATE TABLE IF NOT EXISTS nodes (
            id         TEXT PRIMARY KEY,
            kind       TEXT NOT NULL,
            name       TEXT NOT NULL,
            fqn        TEXT NOT NULL,
            file       TEXT NULL,
            span_start INTEGER NULL,
            span_end   INTEGER NULL
        );

        CREATE INDEX IF NOT EXISTS idx_nodes_name ON nodes(name);

        CREATE TABLE IF NOT EXISTS edges (
            source_id TEXT NOT NULL,
            target_id TEXT NOT NULL,
            kind      TEXT NOT NULL,
            PRIMARY KEY (source_id, target_id, kind)
        ) WITHOUT ROWID;

        CREATE INDEX IF NOT EXISTS idx_edges_source ON edges(source_id, kind);
        CREATE INDEX IF NOT EXISTS idx_edges_target ON edges(target_id, kind);

        CREATE TABLE IF NOT EXISTS files (
            path         TEXT PRIMARY KEY,
            content_hash TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS meta (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """;
}
