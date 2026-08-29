using System.CommandLine;
using System.ComponentModel;
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

var basePathOption = new Option<string>("--base-path")
{
    Description = "Prefix prepended to a frontend call site's own path before matching it against " +
        "endpoints (e.g. \"/api\", matching an axios baseURL that isn't visible in the call site " +
        "literal itself). Pass \"\" to disable — use when call sites already include the full path.",
    DefaultValueFactory = _ => CrossStackLinker.DefaultBasePathPrefix,
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

    // Meta keys this command does not own (frontend/linker producer state) must survive its own
    // rebuild — analyze rebuilds the graph from Roslyn output alone via a full-replace save, so
    // anything not explicitly carried forward here is silently lost, the same class of bug the
    // cross-stack-linker-investigation.md §Q3 prerequisite found in MergeIntoGraph's edge
    // carry-over. Read before this run's own save overwrites the meta table.
    var existingMeta = File.Exists(store.DatabasePath)
        ? await store.GetMetaAsync(cancellationToken).ConfigureAwait(false)
        : new Dictionary<string, string>(StringComparer.Ordinal);

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

    var meta = new Dictionary<string, string>(existingMeta, StringComparer.Ordinal)
    {
        [MetaKeys.SolutionPath] = solution,
        [MetaKeys.LastAnalyzed] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        [MetaKeys.ToolVersion] = currentVersion,
        [MetaKeys.UnresolvedEndpoints] = snapshot.Stats.UnresolvedEndpoints.ToString(CultureInfo.InvariantCulture),
        [MetaKeys.ConventionalControllers] = snapshot.Stats.ConventionalControllers.ToString(CultureInfo.InvariantCulture),
        [MetaKeys.RazorPagesNotModeled] = snapshot.Stats.RazorPagesNotModeled.ToString(CultureInfo.InvariantCulture),
        [MetaKeys.RazorFilesDetected] = snapshot.Stats.RazorFilesDetected.ToString(CultureInfo.InvariantCulture),
        [MetaKeys.ControllerLikeClassesUnrecognized] = snapshot.Stats.ControllerLikeClassesUnrecognized.ToString(CultureInfo.InvariantCulture),
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
    string razorFilesNote = stats.RazorFilesDetected > 0
        ? pal.Label(", ") + pal.Warn(stats.RazorFilesDetected.ToString(CultureInfo.InvariantCulture)) + pal.Label(" .razor file(s) detected — Blazor markup is not analyzed (github.com/EMahmoudNabil/slnmap issue #30)")
        : string.Empty;
    Console.WriteLine(pal.Label("Documents: ") + pal.Number(stats.DocumentsAnalyzed.ToString(CultureInfo.InvariantCulture)) + pal.Label(" analyzed, ") + pal.Number(stats.DocumentsSkipped.ToString(CultureInfo.InvariantCulture)) + pal.Label(" skipped") + razorFilesNote);
    Console.WriteLine(pal.Label("Graph:     ") + pal.Number(graph.NodeCount.ToString(CultureInfo.InvariantCulture)) + pal.Label(" nodes, ") + pal.Number(graph.EdgeCount.ToString(CultureInfo.InvariantCulture)) + pal.Label(" edges"));
    int endpointCount = graph.Nodes.Count(static n => n.Kind == NodeKind.Endpoint);
    if (endpointCount > 0 || stats.UnresolvedEndpoints > 0 || stats.ConventionalControllers > 0 || stats.RazorPagesNotModeled > 0 || stats.ControllerLikeClassesUnrecognized > 0)
    {
        string unresolvedValue = stats.UnresolvedEndpoints == 0
            ? pal.Success("0")
            : pal.Warn(stats.UnresolvedEndpoints.ToString(CultureInfo.InvariantCulture));
        string conventionalNote = stats.ConventionalControllers > 0
            ? pal.Label(", ") + pal.Warn(stats.ConventionalControllers.ToString(CultureInfo.InvariantCulture)) + pal.Label(" conventionally-routed controller(s) not modeled")
            : string.Empty;
        string razorPagesNote = stats.RazorPagesNotModeled > 0
            ? pal.Label(", ") + pal.Warn(stats.RazorPagesNotModeled.ToString(CultureInfo.InvariantCulture)) + pal.Label(" Razor Page(s) not modeled")
            : string.Empty;
        string controllerLikeNote = stats.ControllerLikeClassesUnrecognized > 0
            ? pal.Label(", ") + pal.Warn(stats.ControllerLikeClassesUnrecognized.ToString(CultureInfo.InvariantCulture)) + pal.Label(" controller-like class(es) not recognized (see warnings)")
            : string.Empty;
        Console.WriteLine(pal.Label("Endpoints: ") + pal.Number(endpointCount.ToString(CultureInfo.InvariantCulture)) + pal.Label(" mapped, ") + unresolvedValue + pal.Label(" unresolved" + (stats.UnresolvedEndpoints > 0 ? " (see warnings; run --verbose for locations)" : "")) + conventionalNote + razorPagesNote + controllerLikeNote);
    }
    Console.WriteLine(pal.Label("Files:     ") + pal.Number(snapshot.Files.Count.ToString(CultureInfo.InvariantCulture)) + pal.Label(" hashed"));
    Console.WriteLine(pal.Label("Warnings:  ") + warningsValue);
    Console.WriteLine(pal.Label("Elapsed:   ") + pal.Success($"{stopwatch.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s"));
    Console.WriteLine(pal.Label("Saved:     ") + pal.Label(store.DatabasePath));
    if (BuildLinkerStalenessNote(existingMeta) is { } linkerNote)
    {
        Console.WriteLine(pal.Label(linkerNote));
    }

    return 0;
});

var frontendRootArgument = new Argument<string>("frontend-root")
{
    Description = "Path to the frontend project's root directory.",
};

var tsconfigOption = new Option<string?>("--tsconfig")
{
    Description = "Path to the tsconfig.json to use (defaults to <frontend-root>/tsconfig.json).",
};

var analyzeTsCommand = new Command(
    "analyze-ts",
    "Analyze a frontend project's HTTP call sites (via slnmap-ts) and merge them into the code graph.")
{
    frontendRootArgument,
    tsconfigOption,
    dbOption,
    verboseOption,
};
analyzeTsCommand.SetAction(async (parseResult, cancellationToken) =>
{
    string frontendRoot = Path.GetFullPath(parseResult.GetRequiredValue(frontendRootArgument));
    string db = parseResult.GetRequiredValue(dbOption);
    bool verbose = parseResult.GetValue(verboseOption);
    string tsconfig = Path.GetFullPath(
        parseResult.GetValue(tsconfigOption) ?? Path.Combine(frontendRoot, "tsconfig.json"));

    // Step 1: validate the root + tsconfig presence — corrective, no stack traces
    // (ts-extractor-investigation.md §Q1.1 step 1; CliErrorHandlingTests.cs conventions).
    if (!Directory.Exists(frontendRoot))
    {
        Console.Error.WriteLine(Palette.Err.Error($"Frontend root not found: {frontendRoot}"));
        Console.Error.WriteLine(Palette.Err.Label("Check the path and try again."));
        return 1;
    }

    if (!File.Exists(tsconfig))
    {
        // Two distinct audiences (field trial, reports/analyze-ts-field-trial.md §4.4): a
        // project whose tsconfig just isn't where expected (the first line), and a genuinely
        // tsconfig-less plain-JavaScript project (a real, not-hypothetical case — the second
        // line), which the first line alone left with no path forward. slnmap-ts itself handles
        // allowJs projects fully once a tsconfig exists (verified against a real plain-JS
        // codebase in the field trial) — the gap was purely this message never saying so.
        Console.Error.WriteLine(Palette.Err.Error($"tsconfig not found: {tsconfig}"));
        Console.Error.WriteLine(Palette.Err.Label("Pass --tsconfig to point at the frontend project's tsconfig.json."));
        Console.Error.WriteLine(Palette.Err.Label("If this is a plain JavaScript project, create a minimal tsconfig.json with \"allowJs\": true to enable analysis."));
        return 1;
    }

    // Step 2: Node presence — a hard failure, not a silent skip, because the user explicitly
    // asked for this verb (§Q1.1 step 2). Exact wording from the investigation.
    if (!await IsNodeAvailableAsync(cancellationToken).ConfigureAwait(false))
    {
        Console.Error.WriteLine(Palette.Err.Error("Node.js not found."));
        Console.Error.WriteLine(Palette.Err.Label(
            "Frontend analysis requires Node 18+ — install it from https://nodejs.org, or run without `analyze-ts` to skip frontend coverage."));
        return 1;
    }

    string tempArtifact = Path.Combine(Path.GetTempPath(), $"slnmap-ts-artifact-{Guid.NewGuid():N}.json");
    try
    {
        // Step 3: extraction. Absolute paths throughout (§7.4's lesson — never rely on a shared
        // CWD between this process and the spawned one).
        var (exitCode, stdout, stderr) = await RunSlnmapTsExtractAsync(frontendRoot, tsconfig, tempArtifact, cancellationToken)
            .ConfigureAwait(false);
        if (exitCode != 0)
        {
            Console.Error.WriteLine(Palette.Err.Error("slnmap-ts extraction failed."));
            Console.Error.WriteLine(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
            if (verbose)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"stdout:\n{stdout}\nstderr:\n{stderr}");
            }

            return 1;
        }

        if (!File.Exists(tempArtifact))
        {
            Console.Error.WriteLine(Palette.Err.Error("slnmap-ts reported success but produced no output artifact."));
            return 1;
        }

        // Step 4: validate the artifact — schemaVersion, producer, shape. A malformed artifact
        // is a clean error; never a partial ingest (TsArtifactFacts.Parse validates every call
        // site before returning).
        string json = await File.ReadAllTextAsync(tempArtifact, cancellationToken).ConfigureAwait(false);
        TsArtifact artifact;
        try
        {
            artifact = TsArtifactFacts.Parse(json);
        }
        catch (TsArtifactException e)
        {
            Console.Error.WriteLine(Palette.Err.Error(e.Message));
            return 1;
        }

        // Step 5: ingest — kind-scoped prune-and-replace (§Q1.2), reusing LoadGraphAsync/
        // SaveAsync unmodified; every existing meta/file row is preserved explicitly (SaveAsync
        // is a full rebuild — anything not passed back in is lost).
        await using var store = new SqliteGraphStore(db);
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var existingGraph = await store.LoadGraphAsync(cancellationToken).ConfigureAwait(false);
        var existingMeta = await store.GetMetaAsync(cancellationToken).ConfigureAwait(false);
        var existingFiles = await store.GetFileHashesAsync(cancellationToken).ConfigureAwait(false);

        var newNodes = TsArtifactFacts.BuildNodes(artifact, frontendRoot);
        var mergedGraph = TsArtifactFacts.MergeIntoGraph(existingGraph, newNodes);

        string? orderingNote = BuildOrderingNote(existingMeta);

        var mergedMeta = new Dictionary<string, string>(existingMeta, StringComparer.Ordinal)
        {
            [MetaKeys.FrontendLastAnalyzed] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            [MetaKeys.FrontendUnresolvedCallSites] = (artifact.Stats?.UnresolvedCount ?? 0).ToString(CultureInfo.InvariantCulture),
        };
        var fileRecords = existingFiles.Select(pair => new FileRecord(pair.Key, pair.Value)).ToList();

        await store.SaveAsync(mergedGraph, fileRecords, mergedMeta, cancellationToken).ConfigureAwait(false);

        // Step 6: summary, in the existing style.
        var pal = Palette.Out;
        int resolved = artifact.Stats?.ResolvedCount ?? 0;
        int unresolved = artifact.Stats?.UnresolvedCount ?? 0;
        double coverage = artifact.Stats?.CoveragePercent ?? 0;
        Console.WriteLine(
            pal.Label("Frontend:  ")
            + pal.Number(resolved.ToString(CultureInfo.InvariantCulture))
            + pal.Label(" call sites resolved, ")
            + pal.Number(unresolved.ToString(CultureInfo.InvariantCulture))
            + pal.Label($" unresolved ({coverage.ToString("0.#", CultureInfo.InvariantCulture)}% coverage)"));

        if (verbose && artifact.Stats?.ByCategory is { Count: > 0 } byCategory)
        {
            foreach (var (category, count) in byCategory.OrderByDescending(pair => pair.Value))
            {
                Console.WriteLine(pal.Label($"    {category}: ") + pal.Number(count.ToString(CultureInfo.InvariantCulture)));
            }
        }

        if (orderingNote is not null)
        {
            Console.WriteLine(pal.Label(orderingNote));
        }

        if (BuildLinkerStalenessNote(existingMeta) is { } linkerNote)
        {
            Console.WriteLine(pal.Label(linkerNote));
        }

        Console.WriteLine(pal.Label("Saved:     ") + pal.Label(store.DatabasePath));
        return 0;
    }
    finally
    {
        if (File.Exists(tempArtifact))
        {
            try
            {
                File.Delete(tempArtifact);
            }
            catch (IOException)
            {
                // Best-effort cleanup of a temp file.
            }
        }
    }
});

var linkCommand = new Command(
    "link",
    "Compute cross-stack edges between frontend HTTP call sites and their C# endpoints (cross-stack-linker-investigation.md).")
{
    dbOption,
    verboseOption,
    basePathOption,
};
linkCommand.SetAction(async (parseResult, cancellationToken) =>
{
    string db = parseResult.GetRequiredValue(dbOption);
    bool verbose = parseResult.GetValue(verboseOption);
    string basePath = parseResult.GetRequiredValue(basePathOption);

    await using var store = new SqliteGraphStore(db);
    if (!File.Exists(store.DatabasePath))
    {
        Console.Error.WriteLine(Palette.Err.Error($"No graph at {store.DatabasePath}."));
        Console.Error.WriteLine(Palette.Err.Label("Run 'slnmap analyze <solution>' first."));
        return 1;
    }

    await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
    var graph = await store.LoadGraphAsync(cancellationToken).ConfigureAwait(false);
    var existingMeta = await store.GetMetaAsync(cancellationToken).ConfigureAwait(false);
    var existingFiles = await store.GetFileHashesAsync(cancellationToken).ConfigureAwait(false);

    int endpointCount = graph.Nodes.Count(static n => n.Kind == NodeKind.Endpoint);
    if (endpointCount == 0)
    {
        Console.Error.WriteLine(Palette.Err.Error("The graph has no Endpoint nodes."));
        Console.Error.WriteLine(Palette.Err.Label("Run 'slnmap analyze <solution>' first."));
        return 1;
    }

    int callSiteCount = graph.Nodes.Count(static n => n.Kind == NodeKind.FrontendCallSite);
    if (callSiteCount == 0)
    {
        Console.Error.WriteLine(Palette.Err.Error("The graph has no FrontendCallSite nodes."));
        Console.Error.WriteLine(Palette.Err.Label("Run 'slnmap analyze-ts <frontend-root>' first."));
        return 1;
    }

    var stopwatch = Stopwatch.StartNew();

    // Full recompute (investigation §Q3/§Q6): drop every existing CallsEndpoint edge — however
    // it got there — and rebuild from the current node sets. Cheap at real scale (a few hundred
    // thousand in-memory string comparisons), so there is no reason to attempt incremental edge
    // maintenance and inherit its staleness bug surface for a cost this small.
    var relinked = new CodeGraph();
    foreach (var node in graph.Nodes)
    {
        relinked.AddNode(node);
    }

    foreach (var edge in graph.Edges.Where(static e => e.Kind != RelationshipKind.CallsEndpoint))
    {
        relinked.AddEdge(edge);
    }

    var results = CrossStackLinker.Link(relinked, basePath);
    foreach (var edge in CrossStackLinker.ToEdges(results))
    {
        relinked.AddEdge(edge);
    }

    stopwatch.Stop();

    var byOutcome = results.ToLookup(r => r.Outcome);
    int unique = byOutcome[CallSiteLinkOutcome.Unique].Count();
    int precedence = byOutcome[CallSiteLinkOutcome.PrecedenceResolved].Count();
    int setEdge = byOutcome[CallSiteLinkOutcome.SetEdge].Count();
    int noMatch = byOutcome[CallSiteLinkOutcome.NoSkeletonMatch].Count();
    int verbMismatch = byOutcome[CallSiteLinkOutcome.VerbMismatch].Count();
    int unknownVerb = byOutcome[CallSiteLinkOutcome.UnknownVerb].Count();
    int ambiguousHost = byOutcome[CallSiteLinkOutcome.AmbiguousHost].Count();
    int linked = unique + precedence + setEdge;
    int disclosed = noMatch + verbMismatch + unknownVerb + ambiguousHost;
    int prefixAmbiguous = byOutcome[CallSiteLinkOutcome.SetEdge].Count(static r => r.AmbiguityReason is not null);
    // v0.13.0: call sites resolved to an absolute URL (Host set regardless of link outcome — the
    // "carry its host visibly" disclosure, CallSiteLinkResult.Host's own doc comment).
    int withHost = results.Count(static r => r.Host is not null);
    // v0.13.1: linked only via the base-path-stripped fallback candidate — an INFERRED link, never
    // rendered like a literal match (CallSiteLinkResult.ViaPrefixStripped's own doc comment).
    int viaPrefixStripped = results.Count(static r => r.ViaPrefixStripped);

    var meta = new Dictionary<string, string>(existingMeta, StringComparer.Ordinal)
    {
        [MetaKeys.LinkerLastRun] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        [MetaKeys.LinkerBasePathPrefix] = basePath,
    };
    var fileRecords = existingFiles.Select(static pair => new FileRecord(pair.Key, pair.Value)).ToList();
    await store.SaveAsync(relinked, fileRecords, meta, cancellationToken).ConfigureAwait(false);

    var pal = Palette.Out;
    string prefixAmbiguousSuffix = prefixAmbiguous > 0
        ? pal.Label(", ") + pal.Warn(prefixAmbiguous.ToString(CultureInfo.InvariantCulture)) + pal.Label(" prefix-ambiguous")
        : string.Empty;
    string viaPrefixStrippedSuffix = viaPrefixStripped > 0
        ? pal.Label(", ") + pal.Warn(viaPrefixStripped.ToString(CultureInfo.InvariantCulture)) + pal.Label(" via prefix-stripped path")
        : string.Empty;
    Console.WriteLine(
        pal.Label("Linked:    ")
        + pal.Number(linked.ToString(CultureInfo.InvariantCulture))
        + pal.Label($"/{results.Count.ToString(CultureInfo.InvariantCulture)} call sites (")
        + pal.Number(unique.ToString(CultureInfo.InvariantCulture)) + pal.Label(" unique, ")
        + pal.Number(precedence.ToString(CultureInfo.InvariantCulture)) + pal.Label(" via precedence, ")
        + pal.Number(setEdge.ToString(CultureInfo.InvariantCulture)) + pal.Label(" set-edge")
        + prefixAmbiguousSuffix + viaPrefixStrippedSuffix + pal.Label(")"));
    string unknownVerbSuffix = unknownVerb > 0
        ? pal.Label(", ") + pal.Number(unknownVerb.ToString(CultureInfo.InvariantCulture)) + pal.Label(" unknown verb")
        : string.Empty;
    string ambiguousHostSuffix = ambiguousHost > 0
        ? pal.Label(", ") + pal.Warn(ambiguousHost.ToString(CultureInfo.InvariantCulture)) + pal.Label(" ambiguous host")
        : string.Empty;
    Console.WriteLine(
        pal.Label("Disclosed: ")
        + (disclosed == 0 ? pal.Success("0") : pal.Warn(disclosed.ToString(CultureInfo.InvariantCulture)))
        + pal.Label(" (")
        + pal.Number(noMatch.ToString(CultureInfo.InvariantCulture)) + pal.Label(" no match, ")
        + pal.Number(verbMismatch.ToString(CultureInfo.InvariantCulture)) + pal.Label(" verb mismatch")
        + unknownVerbSuffix + ambiguousHostSuffix + pal.Label(")"));
    if (withHost > 0)
    {
        Console.WriteLine(
            pal.Label("Hosts:     ")
            + pal.Number(withHost.ToString(CultureInfo.InvariantCulture))
            + pal.Label(" call site(s) resolved to an absolute URL — matched by path only; run with --verbose or 'list_frontend_callsites' to see each host"));
    }

    if (verbose)
    {
        foreach (var result in results
            .Where(static r => r.Outcome is CallSiteLinkOutcome.NoSkeletonMatch or CallSiteLinkOutcome.VerbMismatch or CallSiteLinkOutcome.UnknownVerb
                || r.AmbiguityReason is not null || r.Host is not null)
            .OrderBy(static r => r.CallSite.Fqn, StringComparer.Ordinal))
        {
            string callSiteVerb = result.CallSite.Fqn[..result.CallSite.Fqn.IndexOf(' ', StringComparison.Ordinal)];
            string conflictNote = result.ConflictingVerbEndpoints.Count > 0
                ? $" — no {callSiteVerb} registered; " + string.Join(", ", result.ConflictingVerbEndpoints.Select(static e => e.Fqn)) + " exists"
                : string.Empty;
            string ambiguityNote = result.AmbiguityReason is { } reason ? $" — {reason}" : string.Empty;
            string strippedNote = result.ViaPrefixStripped ? " via prefix-stripped path" : string.Empty;
            string hostNote = result.Host is { } host ? $" [host: {host}]" : string.Empty;
            Console.WriteLine(pal.Label($"  {result.CallSite.Fqn} — {result.Outcome}{conflictNote}{ambiguityNote}{strippedNote}{hostNote}"));
        }
    }

    Console.WriteLine(pal.Label("Elapsed:   ") + pal.Success($"{stopwatch.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s"));
    Console.WriteLine(pal.Label("Saved:     ") + pal.Label(store.DatabasePath));
    return 0;
});

var watchCommand = new Command("watch", "Analyze once, then keep a warm workspace and re-analyze on every file save (near-instant on content changes).")
{
    solutionArgument,
    dbOption,
    verboseOption,
};
watchCommand.SetAction(async (parseResult, cancellationToken) =>
{
    string solution = Path.GetFullPath(parseResult.GetRequiredValue(solutionArgument));
    string db = parseResult.GetRequiredValue(dbOption);
    bool verbose = parseResult.GetValue(verboseOption);
    var status = new ConsoleStatusLine();
    var warnings = new WarningReport();

    await using var store = new SqliteGraphStore(db);
    string currentVersion = CurrentVersion();
    AnalysisSnapshot? previous = await LoadPreviousAsync(store, currentVersion, status, cancellationToken).ConfigureAwait(false);
    if (previous is not null)
    {
        status.WriteLine($"incremental: reusing {previous.Graph.NodeCount} nodes from {store.DatabasePath}");
    }

    using var resident = new Slnmap.Analysis.ResidentAnalyzer(warnings.Add);
    var pal = Palette.Out;
    var stopwatch = Stopwatch.StartNew();
    AnalysisSnapshot snapshot;
    try
    {
        snapshot = await resident.InitializeAsync(solution, previous, status, cancellationToken).ConfigureAwait(false);
    }
    catch (SdkNotFoundException e)
    {
        Console.Error.WriteLine(e.Message);
        return 1;
    }
    catch (OperationCanceledException)
    {
        return 0;
    }
    finally
    {
        status.Finish();
    }

    await store.SaveAsync(snapshot.Graph, snapshot.Files, BuildMeta(solution, snapshot, currentVersion), cancellationToken).ConfigureAwait(false);
    stopwatch.Stop();
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

    Console.WriteLine(pal.Label("Graph:     ") + pal.Number(snapshot.Graph.NodeCount.ToString(CultureInfo.InvariantCulture)) + pal.Label(" nodes, ") + pal.Number(snapshot.Graph.EdgeCount.ToString(CultureInfo.InvariantCulture)) + pal.Label(" edges"));
    Console.WriteLine(pal.Label("Elapsed:   ") + pal.Success($"{stopwatch.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)}s"));
    Console.WriteLine(pal.Label("Saved:     ") + pal.Label(store.DatabasePath));
    Console.WriteLine(pal.Label("Watching:  ") + pal.Label(Path.GetDirectoryName(solution)!) + pal.Label("  (Ctrl+C to stop; run 'slnmap serve' beside this — it reads the same file)"));

    string watchRoot = Path.GetDirectoryName(solution)!;
    var filter = new Slnmap.Cli.WatchFilter(store.DatabasePath);
    var pendingLock = new object();
    var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var lastEvent = Stopwatch.StartNew();

    void Enqueue(string path)
    {
        if (filter.Classify(path) == Slnmap.Cli.WatchVerdict.Ignore)
        {
            return;
        }

        lock (pendingLock)
        {
            pending.Add(path);
            lastEvent.Restart();
        }
    }

    using var watcher = new FileSystemWatcher(watchRoot)
    {
        IncludeSubdirectories = true,
        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
    };
    watcher.Changed += (_, e) => Enqueue(e.FullPath);
    watcher.Created += (_, e) => Enqueue(e.FullPath);
    watcher.Deleted += (_, e) => Enqueue(e.FullPath);
    watcher.Renamed += (_, e) => { Enqueue(e.OldFullPath); Enqueue(e.FullPath); };
    watcher.EnableRaisingEvents = true;

    var current = snapshot;
    try
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            List<string> batch;
            lock (pendingLock)
            {
                // Debounce: editors save in bursts; wait for 400ms of quiet, then take the batch.
                if (pending.Count == 0 || lastEvent.ElapsedMilliseconds < 400)
                {
                    continue;
                }

                batch = [.. pending];
                pending.Clear();
            }

            var sw = Stopwatch.StartNew();
            try
            {
                bool structural = batch.Any(p => filter.Classify(p) == Slnmap.Cli.WatchVerdict.Structural);
                AnalysisSnapshot updated;
                if (structural)
                {
                    Console.WriteLine(pal.Label($"[{DateTime.Now:HH:mm:ss}] project files changed — reloading the workspace..."));
                    updated = await resident.ReloadAsync(progress: null, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    var result = await resident.ReanalyzeChangedAsync(batch, cancellationToken).ConfigureAwait(false);
                    if (result.RequiresReload)
                    {
                        Console.WriteLine(pal.Label($"[{DateTime.Now:HH:mm:ss}] files added/removed — reloading the workspace..."));
                        updated = await resident.ReloadAsync(progress: null, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        updated = result.Snapshot!;
                    }
                }

                sw.Stop();
                if (GraphsEqual(current.Graph, updated.Graph))
                {
                    current = updated;
                    Console.WriteLine(pal.Label($"[{DateTime.Now:HH:mm:ss}] re-analyzed {batch.Count} file(s) in {sw.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture)}s — graph unchanged, save skipped"));
                    continue;
                }

                var saveWatch = Stopwatch.StartNew();
                await store.SaveAsync(updated.Graph, updated.Files, BuildMeta(solution, updated, currentVersion), cancellationToken).ConfigureAwait(false);
                saveWatch.Stop();
                current = updated;
                Console.WriteLine(pal.Label($"[{DateTime.Now:HH:mm:ss}] re-analyzed {batch.Count} file(s) in {sw.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture)}s — graph ") + pal.Number(updated.Graph.NodeCount.ToString(CultureInfo.InvariantCulture)) + pal.Label(" nodes / ") + pal.Number(updated.Graph.EdgeCount.ToString(CultureInfo.InvariantCulture)) + pal.Label($" edges, saved in {saveWatch.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture)}s"));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                // One bad batch must not kill the session — report and keep watching.
                Console.Error.WriteLine(Palette.Err.Warn($"[{DateTime.Now:HH:mm:ss}] re-analysis failed: {e.Message} — still watching."));
            }
        }
    }
    catch (OperationCanceledException)
    {
        // Ctrl+C — clean exit.
    }

    Console.WriteLine(pal.Label("watch stopped."));
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
    analyzeTsCommand,
    linkCommand,
    watchCommand,
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

/// <summary>The meta rows every graph save writes — one builder shared by analyze and watch.</summary>
static IReadOnlyDictionary<string, string> BuildMeta(string solution, AnalysisSnapshot snapshot, string currentVersion) =>
    new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [MetaKeys.SolutionPath] = solution,
        [MetaKeys.LastAnalyzed] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        [MetaKeys.ToolVersion] = currentVersion,
        [MetaKeys.UnresolvedEndpoints] = snapshot.Stats.UnresolvedEndpoints.ToString(CultureInfo.InvariantCulture),
        [MetaKeys.ConventionalControllers] = snapshot.Stats.ConventionalControllers.ToString(CultureInfo.InvariantCulture),
        [MetaKeys.RazorPagesNotModeled] = snapshot.Stats.RazorPagesNotModeled.ToString(CultureInfo.InvariantCulture),
        [MetaKeys.RazorFilesDetected] = snapshot.Stats.RazorFilesDetected.ToString(CultureInfo.InvariantCulture),
        [MetaKeys.ControllerLikeClassesUnrecognized] = snapshot.Stats.ControllerLikeClassesUnrecognized.ToString(CultureInfo.InvariantCulture),
    };

/// <summary>Set equality over nodes and edges — a whitespace-only touch produces an identical graph, and watch skips the save.</summary>
static bool GraphsEqual(CodeGraph a, CodeGraph b) =>
    a.NodeCount == b.NodeCount
    && a.EdgeCount == b.EdgeCount
    && a.Nodes.ToHashSet().SetEquals(b.Nodes)
    && a.Edges.ToHashSet().SetEquals(b.Edges);

/// <summary>
/// The running slnmap assembly's version (e.g. <c>"0.5.0"</c>) — the CLI project's
/// <c>&lt;Version&gt;</c>, not <see cref="AssemblyInformationalVersionAttribute"/>, which the SDK
/// suffixes with a <c>+&lt;git-sha&gt;</c> build-metadata tag that changes on every commit and
/// would force a full rebuild far more often than an actual release.
/// </summary>
static string CurrentVersion() =>
    Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

/// <summary>
/// The `slnmap-ts` npm version <c>analyze-ts</c> pins for its `npx` fallback path — must track
/// <c>src/slnmap-ts/package.json</c>'s <c>version</c>. There is no automated check binding the
/// two today (they live in different toolchains); a version bump on one side without the other
/// is a real drift risk worth a manual note in the release checklist.
/// </summary>
static string PinnedSlnmapTsVersion() => "0.3.0";

/// <summary>Whether `node` is reachable at all — a simple `node --version` probe, 5s ceiling.</summary>
static async Task<bool> IsNodeAvailableAsync(CancellationToken cancellationToken)
{
    string? nodePath = ResolveOnPath("node");
    if (nodePath is null)
    {
        return false;
    }

    var result = await RunProcessAsync(nodePath, ["--version"], TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
    return result.ExitCode == 0;
}

/// <summary>
/// Runs `slnmap-ts extract` and returns its exit code + captured output. Two paths, in order:
/// (1) a directly PATH-resolvable `slnmap-ts` binary (a local/global `npm install` or `npm
/// link` — zero network); (2) `npx --yes --package=slnmap-ts@&lt;pinned&gt;`, exactly as
/// designed (ts-extractor-investigation.md §Q1.1) — `npx` resolves a local copy first before
/// reaching the network. Path (1) is not in the investigation's original wording; it exists
/// because `npx --package=name@version` was verified to force registry resolution regardless of
/// a local/linked install (a bare `npx name` does prefer local resolution, but that syntax can't
/// express the version pin the design calls for) — see reports/analyze-ts-verb-report.md for the
/// empirical trace. This is also what makes the package testable end-to-end before it is ever
/// published to the npm registry. Both candidates are resolved via <see cref="ResolveOnPath"/>
/// BEFORE either is spawned, rather than by catching a "not found" exception from
/// <see cref="Process.Start(ProcessStartInfo)"/> — see that method's own remarks for why.
/// </summary>
static async Task<(int ExitCode, string Stdout, string Stderr)> RunSlnmapTsExtractAsync(
    string frontendRoot, string tsconfig, string outPath, CancellationToken cancellationToken)
{
    string[] extractArgs = ["extract", frontendRoot, "--tsconfig", tsconfig, "--out", outPath];

    if (ResolveOnPath("slnmap-ts") is { } directPath)
    {
        return await RunProcessAsync(directPath, extractArgs, TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
    }

    if (ResolveOnPath("npx") is not { } npxPath)
    {
        return (1, string.Empty, "Neither 'slnmap-ts' nor 'npx' could be found on PATH.");
    }

    string[] npxArgs = ["--yes", $"--package=slnmap-ts@{PinnedSlnmapTsVersion()}", "slnmap-ts", .. extractArgs];
    return await RunProcessAsync(npxPath, npxArgs, TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
}

/// <summary>
/// Manual PATH (+ PATHEXT on Windows) resolution, returning the fully-resolved file path or
/// <see langword="null"/>. Necessary — and sufficient — because of two things verified
/// empirically on this machine (reports/analyze-ts-verb-report.md Part 2): npm-installed command
/// shims (`npx`, and any locally/globally installed bin script such as `slnmap-ts` itself) are
/// `.cmd` files on Windows, and <see cref="Process.Start(ProcessStartInfo)"/> with
/// <c>UseShellExecute = false</c> does NOT search PATH/PATHEXT for a bare name the way a shell
/// does — it throws <see cref="Win32Exception"/> even when the shim is genuinely on PATH. The
/// first fix attempted (route every spawn through <c>cmd.exe /c</c>) traded that exception for a
/// worse bug: <c>cmd.exe</c> itself always exists, so <c>Process.Start</c> always succeeds, and a
/// missing target shows up as `cmd.exe`'s own non-zero exit with a LOCALIZED "is not recognized"
/// message — indistinguishable from a genuine extraction failure, which silently broke the
/// slnmap-ts→npx fallback (a missing `slnmap-ts` was reported as a failed run instead of falling
/// through to `npx`). Resolving the FULL path ourselves sidesteps both problems: a resolved
/// `...\npx.CMD` starts directly with no shell involved at all (verified — Windows' CreateProcess
/// launches a `.cmd` file fine once given its complete path; only bare-name PATHEXT SEARCHING is
/// unsupported), and an unresolved name gives a clean, locale-independent "not found" the caller
/// can act on before spawning anything.
/// </summary>
static string? ResolveOnPath(string name)
{
    string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
    string[] extensions = OperatingSystem.IsWindows()
        ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';')
        : [string.Empty];

    foreach (string directory in pathEnv.Split(Path.PathSeparator))
    {
        if (directory.Length == 0)
        {
            continue;
        }

        foreach (string extension in extensions)
        {
            string candidate = Path.Combine(directory, name + extension);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    return null;
}

/// <summary>Starts an already-resolved executable and returns its exit code + captured output,
/// killing it and returning a synthetic timeout result if it runs past <paramref name="timeout"/>.</summary>
static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
    string fileName, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken cancellationToken)
{
    var psi = new ProcessStartInfo(fileName) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
    foreach (var arg in args)
    {
        psi.ArgumentList.Add(arg);
    }

    using var process = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");

    var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
    var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
    using var timeoutCts = new CancellationTokenSource(timeout);
    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
    try
    {
        await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
    {
        process.Kill(entireProcessTree: true);
        return (1, string.Empty, $"'{fileName}' timed out after {timeout}.");
    }

    string stdout = await stdoutTask.ConfigureAwait(false);
    string stderr = await stderrTask.ConfigureAwait(false);
    return (process.ExitCode, stdout, stderr);
}

/// <summary>
/// The run-analyze-ts-after-analyze ordering note (ts-extractor-investigation.md §Q1.2 caveat):
/// `analyze` rebuilds the graph from Roslyn output alone and silently drops frontend-producer
/// node kinds on its next full run. When the stored <see cref="MetaKeys.LastAnalyzed"/> is newer
/// than the stored <see cref="MetaKeys.FrontendLastAnalyzed"/> (or frontend data has never been
/// ingested at all while a C# analysis exists), this run is restoring exactly what that rebuild
/// would have dropped — informational, one line, not a warning.
/// </summary>
static string? BuildOrderingNote(IReadOnlyDictionary<string, string> existingMeta)
{
    if (!existingMeta.TryGetValue(MetaKeys.LastAnalyzed, out var csharpRaw)
        || !DateTimeOffset.TryParse(csharpRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var csharpTimestamp))
    {
        return null;
    }

    if (!existingMeta.TryGetValue(MetaKeys.FrontendLastAnalyzed, out var frontendRaw)
        || !DateTimeOffset.TryParse(frontendRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var frontendTimestamp))
    {
        return "Note: this db has C# analysis but no prior frontend data — nothing was restored, this is the first analyze-ts run.";
    }

    return csharpTimestamp > frontendTimestamp
        ? "Note: the C# side was re-analyzed more recently than the frontend — this run restores frontend call sites `analyze` would otherwise have dropped."
        : null;
}

/// <summary>
/// cross-stack-linker-investigation.md §Q3: neither `analyze` nor `analyze-ts` can write
/// CallsEndpoint edges themselves, so any change either one makes to the graph leaves the last
/// `slnmap link` run's edges reflecting an older state. `existingMeta` is read BEFORE this run's
/// own save, so the presence of <see cref="MetaKeys.LinkerLastRun"/> here means links exist and
/// are now, by definition, at least as stale as whatever this run just changed — informational,
/// one line, not a warning (matches <see cref="BuildOrderingNote"/>'s tone).
/// </summary>
static string? BuildLinkerStalenessNote(IReadOnlyDictionary<string, string> existingMeta) =>
    existingMeta.ContainsKey(MetaKeys.LinkerLastRun)
        ? "Note: cross-stack links were computed before this analysis — run 'slnmap link' to refresh them."
        : null;

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
