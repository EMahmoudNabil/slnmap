using System.Diagnostics;
using System.Text.Json;
using Slnmap.Core.Graph;
using Slnmap.Core.Storage;
using Slnmap.Storage;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// `slnmap check` (v0.15.0), driven as a real process like <see cref="LinkCommandTests"/>: exit 0
/// = pass, 1 = new findings, 2 = the graph could not be checked; a baseline accepts what exists
/// today and only new findings fail.
/// </summary>
public sealed class CheckCommandTests : IDisposable
{
    private readonly string _workDir = Path.Combine(Path.GetTempPath(), $"slnmap-check-{Guid.NewGuid():N}");

    public CheckCommandTests() => Directory.CreateDirectory(_workDir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private string DbPath => Path.Combine(_workDir, "graph.db");

    private string BaselinePath => Path.Combine(_workDir, "slnmap-baseline.json");

    private static SymbolNode Endpoint(string verb, string template) =>
        SymbolNode.Create(NodeKind.Endpoint, template, $"{verb} {template}", "Endpoints.cs", new SourceSpan(0, 1));

    private static SymbolNode CallSite(string verb, string template, string location) =>
        SymbolNode.Create(NodeKind.FrontendCallSite, template, $"{verb} {location}", "x.ts", new SourceSpan(0, 1));

    /// <summary>One linked call, one verb-mismatch orphan.</summary>
    private static CodeGraph OneOrphanGraph()
    {
        var graph = new CodeGraph();
        graph.AddNode(Endpoint("GET", "/api/vendors"));
        graph.AddNode(CallSite("GET", "/vendors", "src/vendors.ts:10:3"));
        graph.AddNode(CallSite("DELETE", "/vendors", "src/vendors.ts:20:3"));
        return graph;
    }

    private async Task SaveAsync(CodeGraph graph, IReadOnlyDictionary<string, string>? meta = null)
    {
        await using var store = new SqliteGraphStore(DbPath);
        await store.SaveAsync(graph, [], meta ?? new Dictionary<string, string>(StringComparer.Ordinal) { [MetaKeys.LastAnalyzed] = "test" });
    }

    [Fact]
    public void MissingDatabase_Exit2_NamesAnalyze()
    {
        var (exit, stdout, stderr) = RunCli("check", "--db", DbPath);

        Assert.Equal(2, exit);
        Assert.Contains("No graph at", stderr, StringComparison.Ordinal);
        Assert.Contains("slnmap analyze", stderr, StringComparison.Ordinal);
        AssertNoStackTrace(stdout, stderr);
    }

    [Fact]
    public async Task UnknownFailOnCategory_Exit2()
    {
        await SaveAsync(OneOrphanGraph());

        var (exit, _, stderr) = RunCli("check", "--db", DbPath, "--fail-on", "orphans,typos");

        Assert.Equal(2, exit);
        Assert.Contains("Unknown --fail-on category 'typos'", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OrphanWithoutBaseline_Exit1_ListsItAsNew()
    {
        await SaveAsync(OneOrphanGraph());

        var (exit, stdout, stderr) = RunCli("check", "--db", DbPath, "--baseline", BaselinePath);

        Assert.Equal(1, exit);
        Assert.Contains("Orphan calls:  1 found, 0 in baseline, 1 new", stdout, StringComparison.Ordinal);
        // Assertions stop short of the em dashes: a redirected console re-encodes them.
        Assert.Contains("NEW  DELETE src/vendors.ts:20 (/vendors)", stdout, StringComparison.Ordinal);
        Assert.Contains("verb-mismatch; no DELETE registered; GET /api/vendors exists", stdout, StringComparison.Ordinal);
        Assert.Contains("Cycles:        0 project-level, 0 in baseline, 0 new", stdout, StringComparison.Ordinal);
        Assert.Contains("Baseline:      none at", stdout, StringComparison.Ordinal);
        Assert.Contains("--update-baseline", stdout, StringComparison.Ordinal);
        Assert.Contains("Result:        FAIL", stdout, StringComparison.Ordinal);
        Assert.Contains("1 new orphan call(s)", stdout, StringComparison.Ordinal);
        AssertNoStackTrace(stdout, stderr);
    }

    [Fact]
    public async Task UpdateBaseline_WritesKeysWithoutLineNumbers_ThenTheCheckPasses()
    {
        await SaveAsync(OneOrphanGraph());

        var update = RunCli("check", "--db", DbPath, "--baseline", BaselinePath, "--update-baseline");
        Assert.Equal(0, update.ExitCode);
        Assert.Contains("1 orphan call(s), 0 project cycle(s) accepted as known", update.Stdout, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(File.ReadAllText(BaselinePath));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(["DELETE src/vendors.ts /vendors"], document.RootElement.GetProperty("orphans").EnumerateArray().Select(e => e.GetString()!).ToArray());
        Assert.Empty(document.RootElement.GetProperty("cycles").EnumerateArray());

        var check = RunCli("check", "--db", DbPath, "--baseline", BaselinePath);
        Assert.Equal(0, check.ExitCode);
        Assert.Contains("Orphan calls:  1 found, 1 in baseline, 0 new", check.Stdout, StringComparison.Ordinal);
        Assert.Contains("Result:        PASS", check.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AKnownOrphanThatMovedLines_IsStillKnown()
    {
        await SaveAsync(OneOrphanGraph());
        Assert.Equal(0, RunCli("check", "--db", DbPath, "--baseline", BaselinePath, "--update-baseline").ExitCode);

        var moved = new CodeGraph();
        moved.AddNode(Endpoint("GET", "/api/vendors"));
        moved.AddNode(CallSite("GET", "/vendors", "src/vendors.ts:30:3"));
        moved.AddNode(CallSite("DELETE", "/vendors", "src/vendors.ts:99:3"));
        await SaveAsync(moved);

        var check = RunCli("check", "--db", DbPath, "--baseline", BaselinePath);
        Assert.Equal(0, check.ExitCode);
        Assert.Contains("1 in baseline, 0 new", check.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ANewOrphanAfterTheBaseline_Exit1_NamesOnlyTheNewOne()
    {
        await SaveAsync(OneOrphanGraph());
        Assert.Equal(0, RunCli("check", "--db", DbPath, "--baseline", BaselinePath, "--update-baseline").ExitCode);

        var grown = OneOrphanGraph();
        grown.AddNode(CallSite("POST", "/orders", "src/orders.ts:5:1"));
        await SaveAsync(grown);

        var check = RunCli("check", "--db", DbPath, "--baseline", BaselinePath);
        Assert.Equal(1, check.ExitCode);
        Assert.Contains("Orphan calls:  2 found, 1 in baseline, 1 new", check.Stdout, StringComparison.Ordinal);
        Assert.Contains("NEW  POST src/orders.ts:5 (/orders)", check.Stdout, StringComparison.Ordinal);
        Assert.Contains("no-match", check.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("NEW  DELETE", check.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AResolvedBaselineEntry_IsReportedNotFailed()
    {
        await SaveAsync(OneOrphanGraph());
        Assert.Equal(0, RunCli("check", "--db", DbPath, "--baseline", BaselinePath, "--update-baseline").ExitCode);

        var fixedGraph = new CodeGraph();
        fixedGraph.AddNode(Endpoint("GET", "/api/vendors"));
        fixedGraph.AddNode(Endpoint("DELETE", "/api/vendors"));
        fixedGraph.AddNode(CallSite("GET", "/vendors", "src/vendors.ts:10:3"));
        fixedGraph.AddNode(CallSite("DELETE", "/vendors", "src/vendors.ts:20:3"));
        await SaveAsync(fixedGraph);

        var check = RunCli("check", "--db", DbPath, "--baseline", BaselinePath);
        Assert.Equal(0, check.ExitCode);
        Assert.Contains("Orphan calls:  0 found", check.Stdout, StringComparison.Ordinal);
        Assert.Contains("1 entry no longer found", check.Stdout, StringComparison.Ordinal);
        Assert.Contains("--update-baseline prunes them", check.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BackendOnlyGraph_SkipsOrphans_Passes()
    {
        var graph = new CodeGraph();
        graph.AddNode(Endpoint("GET", "/api/vendors"));
        await SaveAsync(graph);

        var (exit, stdout, _) = RunCli("check", "--db", DbPath, "--baseline", BaselinePath);

        Assert.Equal(0, exit);
        Assert.Contains("Orphan calls:  skipped", stdout, StringComparison.Ordinal);
        Assert.Contains("the graph has no frontend data", stdout, StringComparison.Ordinal);
        Assert.Contains("Result:        PASS", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailOnWithoutOrphans_ReportsThemButPasses()
    {
        await SaveAsync(OneOrphanGraph());

        var (exit, stdout, _) = RunCli("check", "--db", DbPath, "--baseline", BaselinePath, "--fail-on", "cycles,unrestored");

        Assert.Equal(0, exit);
        Assert.Contains("1 new (not enforced by --fail-on)", stdout, StringComparison.Ordinal);
        Assert.Contains("Result:        PASS", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProjectCycle_Exit1_UntilBaselined()
    {
        await SaveAsync(Build.MutualProjects());

        var first = RunCli("check", "--db", DbPath, "--baseline", BaselinePath);
        Assert.Equal(1, first.ExitCode);
        Assert.Contains("Cycles:        1 project-level, 0 in baseline, 1 new", first.Stdout, StringComparison.Ordinal);
        Assert.Contains("NEW  Alpha -> Beta -> Alpha", first.Stdout, StringComparison.Ordinal);
        Assert.Contains("Result:        FAIL", first.Stdout, StringComparison.Ordinal);
        Assert.Contains("1 new cycle(s)", first.Stdout, StringComparison.Ordinal);

        Assert.Equal(0, RunCli("check", "--db", DbPath, "--baseline", BaselinePath, "--update-baseline").ExitCode);
        var second = RunCli("check", "--db", DbPath, "--baseline", BaselinePath);
        Assert.Equal(0, second.ExitCode);
        Assert.Contains("Cycles:        1 project-level, 1 in baseline, 0 new", second.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnrestoredProjects_Exit1_NotBaselinable()
    {
        var graph = new CodeGraph();
        graph.AddNode(Endpoint("GET", "/api/vendors"));
        await SaveAsync(graph, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MetaKeys.LastAnalyzed] = "test",
            [MetaKeys.ProjectsNotRestored] = "2",
        });

        var (exit, stdout, _) = RunCli("check", "--db", DbPath, "--baseline", BaselinePath);

        Assert.Equal(1, exit);
        Assert.Contains("Not restored:  2 project(s)", stdout, StringComparison.Ordinal);
        Assert.Contains("analyzed with missing dependencies", stdout, StringComparison.Ordinal);
        Assert.Contains("Result:        FAIL", stdout, StringComparison.Ordinal);
        Assert.Contains("2 unrestored project(s)", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidBaselineFile_Exit2()
    {
        await SaveAsync(OneOrphanGraph());
        File.WriteAllText(BaselinePath, "{ not json");

        var (exit, _, stderr) = RunCli("check", "--db", DbPath, "--baseline", BaselinePath);

        Assert.Equal(2, exit);
        Assert.Contains("is not valid JSON", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnderGitHubActions_NewFindingsBecomeAnnotations()
    {
        await SaveAsync(OneOrphanGraph());

        var (exit, stdout, _) = RunCli(new Dictionary<string, string> { ["GITHUB_ACTIONS"] = "true" }, "check", "--db", DbPath, "--baseline", BaselinePath);

        Assert.Equal(1, exit);
        Assert.Contains("::error file=src/vendors.ts,line=20,title=slnmap check::New orphan frontend call: DELETE src/vendors.ts /vendors (verb-mismatch: no DELETE registered; GET /api/vendors exists)", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutsideGitHubActions_NoAnnotations()
    {
        await SaveAsync(OneOrphanGraph());

        var (_, stdout, _) = RunCli(new Dictionary<string, string> { ["GITHUB_ACTIONS"] = string.Empty }, "check", "--db", DbPath, "--baseline", BaselinePath);

        Assert.DoesNotContain("::error", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BasePathOverride_ChangesWhatLinks()
    {
        var graph = new CodeGraph();
        graph.AddNode(Endpoint("GET", "/gateway/vendors"));
        graph.AddNode(CallSite("GET", "/vendors", "src/vendors.ts:10:3"));
        await SaveAsync(graph);

        Assert.Equal(1, RunCli("check", "--db", DbPath, "--baseline", BaselinePath).ExitCode);
        Assert.Equal(0, RunCli("check", "--db", DbPath, "--baseline", BaselinePath, "--base-path", "/gateway").ExitCode);
    }

    private static void AssertNoStackTrace(string stdout, string stderr)
    {
        string combined = $"{stdout}\n{stderr}";
        Assert.DoesNotContain("Unhandled exception", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", combined, StringComparison.Ordinal);
    }

    private (int ExitCode, string Stdout, string Stderr) RunCli(params string[] args) => RunCli(null, args);

    private (int ExitCode, string Stdout, string Stderr) RunCli(IReadOnlyDictionary<string, string>? environment, params string[] args)
    {
        string config = AppContext.BaseDirectory.Replace('\\', '/')
            .Contains("/Release/", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        string cliDll = Path.Combine(TestPaths.RepoRoot, "src", "Slnmap.Cli", "bin", config, "net9.0", "slnmap.dll");
        Assert.True(File.Exists(cliDll), $"CLI not built at {cliDll}");

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(cliDll);
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        // Never inherit the CI's own GITHUB_ACTIONS: the annotation tests set it explicitly.
        psi.Environment["GITHUB_ACTIONS"] = string.Empty;
        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            psi.Environment[key] = value;
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the slnmap CLI.");
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("slnmap CLI timed out.");
        }

        return (process.ExitCode, stdout, stderr);
    }
}
