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

/// <summary>
/// Maps between the directory `slnmap watch` was given and its fully resolved real path (v0.14.1).
/// On macOS, FileSystemWatcher (FSEvents) reports nothing when the watched directory is reached
/// through a symlink — the default temp folder `/var/folders/…` is really `/private/var/folders/…`
/// — so the watcher must watch the real path, and each event path is mapped back to the path the
/// workspace knows its documents by. Found by the first macOS CI run: watch never saw a save.
/// </summary>
internal sealed class WatchRoot
{
    private readonly string _givenRoot;

    public WatchRoot(string givenRoot)
        : this(givenRoot, ResolveRealPath(Path.GetFullPath(givenRoot)))
    {
    }

    /// <summary>For tests: a given root and the real path it resolves to.</summary>
    internal WatchRoot(string givenRoot, string realRoot)
    {
        _givenRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(givenRoot));
        RealRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(realRoot));
    }

    /// <summary>The directory to hand to FileSystemWatcher.</summary>
    public string RealRoot { get; }

    /// <summary>An event path under <see cref="RealRoot"/>, rewritten under the given root.</summary>
    public string MapBack(string eventPath)
    {
        if (string.Equals(RealRoot, _givenRoot, StringComparison.Ordinal)
            || !eventPath.StartsWith(RealRoot, StringComparison.Ordinal))
        {
            return eventPath;
        }

        // A drive or volume root keeps its trailing separator ("D:\", "/"), so what follows it
        // is already the relative part; any other root must be followed by a separator, or the
        // match was only a shared name prefix ("/repo" vs "/repo2").
        bool rootEndsWithSeparator = Path.EndsInDirectorySeparator(RealRoot);
        if (!rootEndsWithSeparator
            && eventPath.Length > RealRoot.Length
            && eventPath[RealRoot.Length] != Path.DirectorySeparatorChar)
        {
            return eventPath;
        }

        string relative = eventPath[RealRoot.Length..].TrimStart(Path.DirectorySeparatorChar);
        return relative.Length == 0 ? _givenRoot : Path.Join(_givenRoot, relative);
    }

    /// <summary>
    /// Resolves every symlinked component of <paramref name="path"/> (not just the last one, which
    /// is all <see cref="FileSystemInfo.ResolveLinkTarget"/> does). Components that don't exist or
    /// can't be read are kept as they are.
    /// </summary>
    internal static string ResolveRealPath(string path) => ResolveRealPath(path, depth: 0);

    private static string ResolveRealPath(string path, int depth)
    {
        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? string.Empty;
        string current = root;
        foreach (string part in full[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            try
            {
                var info = new DirectoryInfo(current);
                // A link's target is stored as written and may itself pass through a link
                // (macOS: a target under /var is really under /private/var), so resolve it again.
                // The depth bound stops a link cycle.
                if (info.LinkTarget is not null
                    && depth < 32
                    && info.ResolveLinkTarget(returnFinalTarget: true) is { } target)
                {
                    current = ResolveRealPath(target.FullName, depth + 1);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Keep the component as written.
            }
        }

        return current;
    }
}
