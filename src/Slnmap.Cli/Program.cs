using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Slnmap.Analysis;
using Slnmap.Core.Analysis;
using Slnmap.Core.Graph;
using Slnmap.Core.Storage;
using Slnmap.Mcp;
using Slnmap.Storage;

var dbOption = new Option<string>("--db", "-d")
{
    Description = "Path to the Slnmap SQLite database.",
    DefaultValueFactory = _ => "slnmap.db",
};

var solutionArgument = new Argument<string>("solution")
{
    Description = "Path to the .sln or .csproj to analyze.",
};

var verboseOption = new Option<bool>("--verbose", "-v")
{
    Description = "Print the full, grouped warning breakdown instead of a single summary line.",
};

var analyzeCommand = new Command("analyze", "Analyze a solution and build (or update) its code graph.")
{
    solutionArgument,
    dbOption,
    verboseOption,
};
analyzeCommand.SetAction(async (parseResult, cancellationToken) =>
{
    string solution = Path.GetFullPath(parseResult.GetRequiredValue(solutionArgument));
    string db = parseResult.GetRequiredValue(dbOption);
    bool verbose = parseResult.GetValue(verboseOption);
    var status = new ConsoleStatusLine();

    // Warnings are collected, not printed as they arrive: on real solutions the raw MSBuild
    // diagnostics (NuGet audit advisories, repeated per project) drown out the results.
    var warnings = new WarningReport();
    var analyzer = new RoslynSolutionAnalyzer(warnings.Add);

    await using var store = new SqliteGraphStore(db);
    string currentVersion = CurrentVersion();

    // An existing graph enables incremental re-analysis: only changed files and their
    // dependents are re-walked; everything else is carried over. A graph from a different
    // slnmap version is never reused as a baseline — analysis behavior can change release to
    // release (see issue #6), so a version change always forces a full rebuild.
    AnalysisSnapshot? previous = await LoadPreviousAsync(store, currentVersion, status, cancellationToken).ConfigureAwait(false);
    if (previous is not null)
    {
        status.WriteLine($"incremental: reusing {previous.Graph.NodeCount} nodes from {store.DatabasePath}");
    }

    var stopwatch = Stopwatch.StartNew();
    AnalysisSnapshot snapshot;
    try
    {
        snapshot = await analyzer.AnalyzeAsync(solution, previous, status, cancellationToken).ConfigureAwait(false);
    }
    catch (SdkNotFoundException e)
    {
        // Expected, actionable failure — show the two-line message, not a stack trace.
        Console.Error.WriteLine(e.Message);
        return 1;
    }
    catch (Exception e) when (e is not OperationCanceledException)
    {
        // A clean, actionable message by default (no stack, no internal paths); the full exception
        // only under --verbose, so a user filing a bug report has something to attach.
        if (e is FileNotFoundException notFound)
        {
            // Wrong/relative path or a directory — the most common first-run mistake.
            Console.Error.WriteLine(Palette.Err.Error($"Solution or project file not found: {notFound.FileName ?? solution}"));
            Console.Error.WriteLine(Palette.Err.Label("Check the path and try again (relative paths resolve against the current directory)."));
        }
        else
        {
            Console.Error.WriteLine(Palette.Err.Error($"Could not analyze '{solution}': {e.Message}"));
            Console.Error.WriteLine(Palette.Err.Label("If the path looks correct, run 'slnmap doctor'; otherwise check the file is a valid .sln/.csproj."));
        }

        if (verbose)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(e.ToString());
        }

        return 1;
    }
    finally
    {
        status.Finish();
    }

    var meta = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [MetaKeys.SolutionPath] = solution,
        [MetaKeys.LastAnalyzed] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        [MetaKeys.ToolVersion] = currentVersion,
        [MetaKeys.UnresolvedEndpoints] = snapshot.Stats.UnresolvedEndpoints.ToString(CultureInfo.InvariantCulture),
        [MetaKeys.ConventionalControllers] = snapshot.Stats.ConventionalControllers.ToString(CultureInfo.InvariantCulture),
    };
    await store.SaveAsync(snapshot.Graph, snapshot.Files, meta, cancellationToken).ConfigureAwait(false);
    stopwatch.Stop();

    // Summarize warnings to stderr, before the results block. Under --verbose, first print the
    // grouped, deduplicated breakdown; otherwise a single counts-first line pointing at --verbose.
    if (warnings.HasWarnings)
    {
        if (verbose)
        {
            foreach (var line in warnings.RenderVerbose())
            {
                status.WriteLine(Palette.Err.Warn(line));
            }
        }

        status.WriteLine(Palette.Err.Warn(warnings.SummaryLine(includeVerboseHint: !verbose)));
    }

    // Styling only — identical information to before. Labels dim, counts bright white; the warnings
    // count is green when zero and yellow otherwise; elapsed time is green as a completion cue.
    var pal = Palette.Out;
    var stats = snapshot.Stats;
    var graph = snapshot.Graph;
    string warningsValue = warnings.Count == 0
        ? pal.Success("0")
        : pal.Warn(warnings.Count.ToString(CultureInfo.InvariantCulture));
    Console.WriteLine(pal.Label("Projects:  ") + pal.Number(stats.ProjectCount.ToString(CultureInfo.InvariantCulture)));
    Console.WriteLine(pal.Label("Documents: ") + pal.Number(stats.DocumentsAnalyzed.ToString(CultureInfo.InvariantCulture)) + pal.Label(" analyzed, ") + pal.Number(stats.DocumentsSkipped.ToString(CultureInfo.InvariantCulture)) + pal.Label(" skipped"));
    Console.WriteLine(pal.Label("Graph:     ") + pal.Number(graph.NodeCount.ToString(CultureInfo.InvariantCulture)) + pal.Label(" nodes, ") + pal.Number(graph.EdgeCount.ToString(CultureInfo.InvariantCulture)) + pal.Label(" edges"));
    int endpointCount = graph.Nodes.Count(static n => n.Kind == NodeKind.Endpoint);
    if (endpointCount > 0 || stats.UnresolvedEndpoints > 0 || stats.ConventionalControllers > 0)
    {
        string unresolvedValue = stats.UnresolvedEndpoints == 0
            ? pal.Success("0")
            : pal.Warn(stats.UnresolvedEndpoints.ToString(CultureInfo.InvariantCulture));
        string conventionalNote = stats.ConventionalControllers > 0
            ? pal.Label(", ") + pal.Warn(stats.ConventionalControllers.ToString(CultureInfo.InvariantCulture)) + pal.Label(" conventionally-routed controller(s) not modeled")
            : string.Empty;
        Console.WriteLine(pal.Label("Endpoints: ") + pal.Number(endpointCount.ToString(CultureInfo.InvariantCulture)) + pal.Label(" mapped, ") + unresolvedValue + pal.Label(" unresolved" + (stats.UnresolvedEndpoints > 0 ? " (see warnings; run --verbose for locations)" : "")) + conventionalNote);
    }
    Console.WriteLine(pal.Label("Files:     ") + pal.Number(snapshot.Files.Count.ToString(CultureInfo.InvariantCulture)) + pal.Label(" hashed"));
    Console.WriteLine(pal.Label("Warnings:  ") + warningsValue);
    Console.WriteLine(pal.Label("Elapsed:   ") + pal.Success($"{stopwatch.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s"));
    Console.WriteLine(pal.Label("Saved:     ") + pal.Label(store.DatabasePath));
    return 0;
});

var serveCommand = new Command("serve", "Serve the code graph to MCP clients over stdio.")
{
    dbOption,
};
serveCommand.SetAction(async (parseResult, cancellationToken) =>
{
    string db = parseResult.GetRequiredValue(dbOption);
    await using var store = new SqliteGraphStore(db);

    // Read-only staleness is the user's call to make, not ours — never refuse to serve. But they
    // must see it: a graph built by a different slnmap version may reflect analysis behavior
    // (e.g. issue #6) that no longer matches what this version would produce.
    if (File.Exists(store.DatabasePath))
    {
        try
        {
            await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A corrupt/non-Slnmap file used to crash the whole server with a raw stack trace at
            // startup — the same clean two-line message `viz` and `status` already use.
            Console.Error.WriteLine(Palette.Err.Error("The graph file is corrupted or not a Slnmap database."));
            Console.Error.WriteLine(Palette.Err.Label($"Delete {store.DatabasePath} and re-run 'slnmap analyze'."));
            return 1;
        }

        var meta = await store.GetMetaAsync(cancellationToken).ConfigureAwait(false);
        meta.TryGetValue(MetaKeys.ToolVersion, out var storedVersion);
        string currentVersion = CurrentVersion();
        if (storedVersion != currentVersion)
        {
            Console.Error.WriteLine(Palette.Err.Warn(
                $"warning: this database was built with slnmap {storedVersion ?? "an earlier version (predates version tracking)"}, " +
                $"but this is {currentVersion} — analysis behavior may differ. Run 'slnmap analyze' to refresh it."));
        }
    }

    await McpServerHost.RunAsync(store, cancellationToken).ConfigureAwait(false);
    return 0;
});

var statusCommand = new Command("status", "Show what is in the code graph database.")
{
    dbOption,
};
statusCommand.SetAction(async (parseResult, cancellationToken) =>
{
    string db = parseResult.GetRequiredValue(dbOption);
    await using var store = new SqliteGraphStore(db);

    if (!File.Exists(store.DatabasePath))
    {
        Console.Error.WriteLine($"No graph at {store.DatabasePath}. Run 'slnmap analyze <solution>' first.");
        return 1;
    }

    await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
    var stats = await store.GetStatisticsAsync(cancellationToken).ConfigureAwait(false);
    var meta = await store.GetMetaAsync(cancellationToken).ConfigureAwait(false);

    var pal = Palette.Out;
    Console.WriteLine(pal.Label("Database:      ") + store.DatabasePath);
    if (meta.TryGetValue(MetaKeys.SolutionPath, out var solutionPath))
    {
        Console.WriteLine(pal.Label("Solution:      ") + solutionPath);
    }

    if (meta.TryGetValue(MetaKeys.LastAnalyzed, out var lastAnalyzed))
    {
        Console.WriteLine(pal.Label("Last analyzed: ") + CliFormat.FriendlyTimestamp(lastAnalyzed));
    }

    if (stats.NodeCount == 0)
    {
        Console.Error.WriteLine(Palette.Err.Error("The graph is empty. Run 'slnmap analyze <solution>' first."));
        return 1;
    }

    int width = CliFormat.TerminalWidth();
    Console.WriteLine(pal.Label("Projects:      ") + pal.Number(stats.Projects.Count.ToString(CultureInfo.InvariantCulture)));
    foreach (var projectLine in CliFormat.WrapList(stats.Projects, 2, width))
    {
        Console.WriteLine(projectLine);
    }

    Console.WriteLine(pal.Label("Nodes:         ") + pal.Number(stats.NodeCount.ToString(CultureInfo.InvariantCulture)));
    foreach (var (kind, count) in stats.NodesByKind.OrderByDescending(kv => kv.Value))
    {
        Console.WriteLine("  " + pal.Type(kind.ToString().PadRight(12)) + " " + pal.Number(count.ToString(CultureInfo.InvariantCulture)));
    }

    Console.WriteLine(); // blank line between the Nodes and Edges blocks

    Console.WriteLine(pal.Label("Edges:         ") + pal.Number(stats.EdgeCount.ToString(CultureInfo.InvariantCulture)));
    foreach (var (kind, count) in stats.EdgesByKind.OrderByDescending(kv => kv.Value))
    {
        Console.WriteLine("  " + pal.Type(kind.ToString().PadRight(12)) + " " + pal.Number(count.ToString(CultureInfo.InvariantCulture)));
    }

    return 0;
});

var outputOption = new Option<string>("--output", "-o")
{
    Description = "Path of the HTML file to write.",
    DefaultValueFactory = _ => "graph.html",
};

var projectOption = new Option<string?>("--project")
{
    Description = "Export only this project's subtree; other projects appear as collapsed stubs.",
};

var vizCommand = new Command("viz", "Export the code graph as a self-contained interactive HTML file.")
{
    dbOption,
    outputOption,
    projectOption,
};
vizCommand.SetAction(async (parseResult, cancellationToken) =>
{
    string db = parseResult.GetRequiredValue(dbOption);
    string output = Path.GetFullPath(parseResult.GetRequiredValue(outputOption));
    string? project = parseResult.GetValue(projectOption);
    await using var store = new SqliteGraphStore(db);

    if (!File.Exists(store.DatabasePath))
    {
        Console.Error.WriteLine($"No graph at {store.DatabasePath}. Run 'slnmap analyze <solution>' first.");
        return 1;
    }

    CodeGraph graph;
    try
    {
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        graph = await store.LoadGraphAsync(cancellationToken).ConfigureAwait(false);
    }
    catch (Exception e) when (e is not OperationCanceledException)
    {
        // A stale/torn/corrupt db throws a raw SqliteException with internal file paths;
        // show the same clean two-line message 'analyze' uses for its failure paths instead.
        Console.Error.WriteLine(Palette.Err.Error("The graph file is corrupted or not a Slnmap database."));
        Console.Error.WriteLine(Palette.Err.Label("Delete slnmap.db and re-run 'slnmap analyze'."));
        return 1;
    }

    if (graph.NodeCount == 0)
    {
        Console.Error.WriteLine(Palette.Err.Error("The graph is empty. Run 'slnmap analyze <solution>' first."));
        return 1;
    }

    var vizMeta = await store.GetMetaAsync(cancellationToken).ConfigureAwait(false);
    var stopwatch = Stopwatch.StartNew();
    VizExporter.VizStats stats;
    try
    {
        stats = VizExporter.WriteHtml(graph, vizMeta, output, project);
    }
    catch (ArgumentException e)
    {
        // Unknown --project — the message already lists the valid names.
        Console.Error.WriteLine(Palette.Err.Error(e.Message));
        return 1;
    }

    stopwatch.Stop();

    if (project is null && stats.NodeCount > 30_000)
    {
        Console.Error.WriteLine(Palette.Err.Warn(
            $"Large graph ({stats.NodeCount} nodes): the HTML will open, but consider --project <name> for a lighter file."));
    }

    var vizPal = Palette.Out;
    Console.WriteLine(vizPal.Label("Nodes:     ") + vizPal.Number(stats.NodeCount.ToString(CultureInfo.InvariantCulture)) + vizPal.Label(" (drill-down starts at the project level)"));
    Console.WriteLine(vizPal.Label("Edges:     ") + vizPal.Number(stats.DependencyEdgeCount.ToString(CultureInfo.InvariantCulture)) + vizPal.Label(" dependency edges (Contains is the hierarchy)"));
    if (stats.OrphansAttachedToProjects + stats.OrphansUnattributed > 0)
    {
        Console.WriteLine(vizPal.Label("Reattached:") + " " + vizPal.Number(stats.OrphansAttachedToProjects.ToString(CultureInfo.InvariantCulture)) + vizPal.Label(" orphaned nodes to projects by path, ") + vizPal.Number(stats.OrphansUnattributed.ToString(CultureInfo.InvariantCulture)) + vizPal.Label(" unattributed"));
    }

    Console.WriteLine(vizPal.Label("Size:      ") + vizPal.Number((stats.OutputBytes / 1024.0).ToString("F0", CultureInfo.InvariantCulture)) + vizPal.Label(" KB"));
    Console.WriteLine(vizPal.Label("Elapsed:   ") + vizPal.Success($"{stopwatch.Elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture)} ms"));
    Console.WriteLine(vizPal.Label("Saved:     ") + vizPal.Label(output));
    return 0;
});

var doctorPathArgument = new Argument<string>("path")
{
    Description = "Directory or solution to check for a governing global.json (defaults to the current directory).",
    DefaultValueFactory = _ => Directory.GetCurrentDirectory(),
};

var doctorCommand = new Command("doctor", "Check that the environment can run Slnmap (SDK, global.json, MSBuild, graph directory).")
{
    doctorPathArgument,
    dbOption,
};
doctorCommand.SetAction(async (parseResult, cancellationToken) =>
{
    string db = parseResult.GetRequiredValue(dbOption);
    string path = parseResult.GetValue(doctorPathArgument) ?? Directory.GetCurrentDirectory();
    var checks = await EnvironmentDoctor.RunAsync(db, path, cancellationToken).ConfigureAwait(false);
    foreach (var check in checks)
    {
        Console.WriteLine($"[{(check.Ok ? "ok" : "FAIL")}] {check.Name}: {check.Detail}");
        if (!check.Ok && check.Fix is { } fix)
        {
            Console.WriteLine($"       fix: {fix}");
        }
    }

    bool allOk = checks.All(c => c.Ok);
    Console.WriteLine(allOk ? "\nAll checks passed." : "\nSome checks failed — see fixes above.");
    return allOk ? 0 : 1;
});

var rootCommand = new RootCommand("Slnmap — maps a .NET solution into a queryable code graph.")
{
    analyzeCommand,
    serveCommand,
    statusCommand,
    vizCommand,
    doctorCommand,
};

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);

/// <summary>
/// Loads the stored graph as an incremental baseline, or null when there is no prior graph
/// (fresh database, empty, or written by a different slnmap version). Stats are irrelevant to
/// the analyzer and left at zero.
/// </summary>
static async Task<AnalysisSnapshot?> LoadPreviousAsync(
    SqliteGraphStore store, string currentVersion, ConsoleStatusLine status, CancellationToken cancellationToken)
{
    if (!File.Exists(store.DatabasePath))
    {
        return null;
    }

    await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
    var graph = await store.LoadGraphAsync(cancellationToken).ConfigureAwait(false);
    if (graph.NodeCount == 0)
    {
        return null;
    }

    var meta = await store.GetMetaAsync(cancellationToken).ConfigureAwait(false);
    meta.TryGetValue(MetaKeys.ToolVersion, out var storedVersion);
    if (storedVersion != currentVersion)
    {
        // Any difference forces a full rebuild, not just major/minor: analysis behavior can
        // change in a patch release (issue #6's fix is exactly that), and a spurious full
        // rebuild costs seconds while silently reusing a stale graph costs correctness.
        status.WriteLine(
            $"slnmap version changed ({storedVersion ?? "unknown"} -> {currentVersion}): performing full re-analysis");
        return null;
    }

    var hashes = await store.GetFileHashesAsync(cancellationToken).ConfigureAwait(false);
    var files = hashes.Select(pair => new FileRecord(pair.Key, pair.Value)).ToList();
    return new AnalysisSnapshot(graph, files, new AnalysisStats(0, 0, 0));
}

/// <summary>
/// The running slnmap assembly's version (e.g. <c>"0.5.0"</c>) — the CLI project's
/// <c>&lt;Version&gt;</c>, not <see cref="AssemblyInformationalVersionAttribute"/>, which the SDK
/// suffixes with a <c>+&lt;git-sha&gt;</c> build-metadata tag that changes on every commit and
/// would force a full rebuild far more often than an actual release.
/// </summary>
static string CurrentVersion() =>
    Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

/// <summary>
/// Progress display on stderr that warnings can safely interleave with. Interactive terminals get
/// a single overwriting line (carriage-return rewrite); redirected/piped output (logs, CI,
/// Tee-Object) gets milestone lines only — stage changes and every 10% — because "\r" does not
/// overwrite in a capture, so per-update writes flooded captured output with thousands of
/// progress lines (issue #16, found by the first external install audit).
/// </summary>
internal sealed class ConsoleStatusLine : IProgress<AnalysisProgress>
{
    private const int Width = 70;
    private readonly object _gate = new();
    private readonly bool _redirected;
    private string? _lastStage;
    private int _lastBucket = -1;

    public ConsoleStatusLine()
        : this(Console.IsErrorRedirected)
    {
    }

    /// <summary>Test seam: the redirection decision is injected because a test runner's stderr is always redirected.</summary>
    internal ConsoleStatusLine(bool redirected) => _redirected = redirected;

    public void Report(AnalysisProgress value)
    {
        lock (_gate)
        {
            if (_redirected)
            {
                // Milestones only: the first report of each stage, then every 10% when the total
                // is known. Stages with unknown totals (Loading) print only their first report.
                int bucket = value.Total > 0 ? value.Completed * 10 / value.Total : -1;
                bool stageChanged = value.Stage != _lastStage;
                if (!stageChanged && bucket == _lastBucket)
                {
                    return;
                }

                _lastStage = value.Stage;
                _lastBucket = bucket;
                if (stageChanged || bucket > 0)
                {
                    Console.Error.WriteLine(Render(value));
                }

                return;
            }

            Console.Error.Write(('\r' + Render(value)).PadRight(Width));
        }
    }

    public void WriteLine(string message)
    {
        lock (_gate)
        {
            if (!_redirected)
            {
                Console.Error.Write('\r' + new string(' ', Width) + '\r');
            }

            Console.Error.WriteLine(message);
        }
    }

    public void Finish()
    {
        lock (_gate)
        {
            if (!_redirected)
            {
                Console.Error.WriteLine();
            }
        }
    }

    private static string Render(AnalysisProgress value)
    {
        string total = value.Total > 0 ? $"/{value.Total}" : string.Empty;
        return $"{value.Stage} {value.Completed}{total}";
    }
}
