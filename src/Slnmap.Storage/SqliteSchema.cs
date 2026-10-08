namespace Slnmap.Storage;

/// <summary>DDL and versioning for the Slnmap SQLite database.</summary>
internal static class SqliteSchema
{
    /// <summary>
    /// Current schema version, stored in <c>meta('schema_version')</c>. Bump whenever the DDL below
    /// changes in a way that makes an existing database unreadable.
    /// </summary>
    /// <remarks>
    /// v2 (v0.14.0): <c>nodes.accessibility</c> plus the file-owned fact tables
    /// (<c>external_calls</c>, <c>di_registrations</c>, <c>attribute_usages</c>, <c>disclosures</c>). A v1 database is
    /// never migrated in place — <c>analyze</c> rebuilds it — but read paths must still tolerate
    /// one (a running server pointed at a not-yet-rebuilt graph).
    /// </remarks>
    public const int Version = 2;

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
            span_end   INTEGER NULL,
            accessibility TEXT NULL,
            member_flags  TEXT NULL
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

        CREATE TABLE IF NOT EXISTS external_targets (
            id        INTEGER PRIMARY KEY,
            fqn       TEXT NOT NULL UNIQUE,
            namespace TEXT NOT NULL,
            assembly  TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_external_targets_namespace ON external_targets(namespace);
        CREATE INDEX IF NOT EXISTS idx_external_targets_assembly ON external_targets(assembly);

        CREATE TABLE IF NOT EXISTS external_calls (
            caller_id  TEXT NOT NULL,
            target_id  INTEGER NOT NULL,
            call_count INTEGER NOT NULL,
            file       TEXT NOT NULL,
            span_start INTEGER NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_external_calls_target ON external_calls(target_id);

        CREATE TABLE IF NOT EXISTS di_registrations (
            service_fqn       TEXT NOT NULL,
            impl_fqn          TEXT NULL,
            lifetime          TEXT NOT NULL,
            registration_kind TEXT NOT NULL,
            caller_id         TEXT NULL,
            file              TEXT NOT NULL,
            span_start        INTEGER NOT NULL,
            project           TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_di_registrations_service ON di_registrations(service_fqn);
        CREATE INDEX IF NOT EXISTS idx_di_registrations_impl ON di_registrations(impl_fqn);

        CREATE TABLE IF NOT EXISTS attribute_usages (
            target_id     TEXT NOT NULL,
            attribute_fqn TEXT NOT NULL,
            file          TEXT NOT NULL,
            span_start    INTEGER NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_attribute_usages_fqn ON attribute_usages(attribute_fqn);

        CREATE TABLE IF NOT EXISTS disclosures (
            kind       TEXT NOT NULL,
            detail     TEXT NOT NULL,
            file       TEXT NOT NULL,
            span_start INTEGER NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_disclosures_kind ON disclosures(kind);
        """;
}
