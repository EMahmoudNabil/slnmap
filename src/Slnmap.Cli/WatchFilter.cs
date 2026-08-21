namespace Slnmap.Cli;

/// <summary>How `slnmap watch` should react to a file-system event.</summary>
internal enum WatchVerdict
{
    /// <summary>Not analysis input: build outputs, VCS internals, the graph database itself, unrelated files.</summary>
    Ignore,

    /// <summary>A C# source file — content changes ride the warm re-analysis path.</summary>
    Content,

    /// <summary>Solution/project shape changed — requires a full workspace reload (membership is never guessed).</summary>
    Structural,
}

/// <summary>
/// Event classification for the watch loop, separated from FileSystemWatcher for testability.
/// The database exclusion is load-bearing, not hygiene: the default db path sits INSIDE the
/// watched tree, so without it every save would re-trigger the watcher forever.
/// </summary>
internal sealed class WatchFilter
{
    private static readonly string[] StructuralExtensions = [".csproj", ".sln", ".slnx", ".props", ".targets"];
    private static readonly string[] IgnoredSegments = ["bin", "obj", ".git", ".vs", "node_modules"];

    private readonly string _databasePath;

    public WatchFilter(string databasePath) => _databasePath = Path.GetFullPath(databasePath);

    public WatchVerdict Classify(string fullPath)
    {
        fullPath = Path.GetFullPath(fullPath);

        // The graph database and its SQLite sidecars / atomic-swap temp file.
        if (fullPath.StartsWith(_databasePath, StringComparison.OrdinalIgnoreCase))
        {
            return WatchVerdict.Ignore;
        }

        var segments = fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Any(static s => IgnoredSegments.Contains(s, StringComparer.OrdinalIgnoreCase)))
        {
            return WatchVerdict.Ignore;
        }

        string extension = Path.GetExtension(fullPath);
        if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return WatchVerdict.Content;
        }

        if (StructuralExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            || Path.GetFileName(fullPath).Equals("global.json", StringComparison.OrdinalIgnoreCase))
        {
            return WatchVerdict.Structural;
        }

        return WatchVerdict.Ignore;
    }
}
