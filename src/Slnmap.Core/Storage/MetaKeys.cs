namespace Slnmap.Core.Storage;

/// <summary>Well-known keys stored in the graph's <c>meta</c> table.</summary>
public static class MetaKeys
{
    /// <summary>Integer schema version the database was written with.</summary>
    public const string SchemaVersion = "schema_version";

    /// <summary>Round-trip ("O") timestamp of the last completed analysis.</summary>
    public const string LastAnalyzed = "last_analyzed";

    /// <summary>Absolute path of the solution or project the graph was built from.</summary>
    public const string SolutionPath = "solution_path";

    /// <summary>
    /// Version of the slnmap tool that produced this graph (the CLI assembly's version, e.g.
    /// <c>"0.5.0"</c>). Absent in databases written before this key existed.
    /// </summary>
    public const string ToolVersion = "tool_version";
}
