using System.CommandLine;
using System.Diagnostics;
using System.Globalization;
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

    // An existing graph enables incremental re-analysis: only changed files and their
    // dependents are re-walked; everything else is carried over.
    AnalysisSnapshot? previous = await LoadPreviousAsync(store, cancellationToken).ConfigureAwait(false);
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
    doctorCommand,
};

return await rootCommand.Parse(args).InvokeAsync().ConfigureAwait(false);

/// <summary>
/// Loads the stored graph as an incremental baseline, or null when there is no prior graph
/// (fresh database or empty). Stats are irrelevant to the analyzer and left at zero.
/// </summary>
static async Task<AnalysisSnapshot?> LoadPreviousAsync(SqliteGraphStore store, CancellationToken cancellationToken)
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

    var hashes = await store.GetFileHashesAsync(cancellationToken).ConfigureAwait(false);
    var files = hashes.Select(pair => new FileRecord(pair.Key, pair.Value)).ToList();
    return new AnalysisSnapshot(graph, files, new AnalysisStats(0, 0, 0));
}

/// <summary>Single-line progress display on stderr that warnings can safely interleave with.</summary>
internal sealed class ConsoleStatusLine : IProgress<AnalysisProgress>
{
    private const int Width = 70;
    private readonly object _gate = new();

    public void Report(AnalysisProgress value)
    {
        string total = value.Total > 0 ? $"/{value.Total}" : string.Empty;
        lock (_gate)
        {
            Console.Error.Write($"\r{value.Stage} {value.Completed}{total}".PadRight(Width));
        }
    }

    public void WriteLine(string message)
    {
        lock (_gate)
        {
            Console.Error.Write('\r' + new string(' ', Width) + '\r');
            Console.Error.WriteLine(message);
        }
    }

    public void Finish()
    {
        lock (_gate)
        {
            Console.Error.WriteLine();
        }
    }
}
