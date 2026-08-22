using System.Diagnostics;
using Slnmap.Core.Graph;
using Slnmap.Core.Storage;
using Slnmap.Storage;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// `slnmap link` verb tests, following <see cref="CliErrorHandlingTests"/>'s conventions: drives
/// the built CLI as a real process, asserts clean corrective errors (never a stack trace) for
/// each precondition, and one real end-to-end happy path through the actual
/// analyze -> analyze-ts -> link pipeline against the cross-stack fixture pair.
/// </summary>
public sealed class LinkCommandTests
{
    [Fact]
    public void Link_MissingDatabase_FailsCleanlyWithoutStackTrace()
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"slnmap-link-nodb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        try
        {
            string missingDb = Path.Combine(workDir, "missing.db");
            var (exit, stdout, stderr) = RunCli(workDir, "link", "--db", missingDb);

            Assert.Equal(1, exit);
            Assert.Contains("No graph at", stderr, StringComparison.Ordinal);
            Assert.Contains("slnmap analyze", stderr, StringComparison.Ordinal);
            AssertNoStackTrace(stdout, stderr);
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public async Task Link_DatabaseWithNoEndpoints_FailsCleanlyNamingAnalyze()
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"slnmap-link-noendpoints-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        string db = Path.Combine(workDir, "empty.db");
        try
        {
            // A graph that exists but has no Endpoint nodes at all (e.g. a C# solution with no
            // ASP.NET endpoints) -- distinct from "db missing" and must name the right verb.
            await using (var store = new SqliteGraphStore(db))
            {
                await store.SaveAsync(new CodeGraph(), [], new Dictionary<string, string>(StringComparer.Ordinal));
            }

            var (exit, stdout, stderr) = RunCli(workDir, "link", "--db", db);

            Assert.Equal(1, exit);
            Assert.Contains("no Endpoint nodes", stderr, StringComparison.Ordinal);
            Assert.Contains("slnmap analyze ", stderr, StringComparison.Ordinal);
            AssertNoStackTrace(stdout, stderr);
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public async Task Link_DatabaseWithEndpointsButNoCallSites_FailsCleanlyNamingAnalyzeTs()
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"slnmap-link-nocallsites-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        string db = Path.Combine(workDir, "backend-only.db");
        try
        {
            var graph = new CodeGraph();
            graph.AddNode(SymbolNode.Create(NodeKind.Endpoint, "/api/vendors", "GET /api/vendors", "Endpoints.cs", new SourceSpan(0, 1)));
            await using (var store = new SqliteGraphStore(db))
            {
                await store.SaveAsync(graph, [], new Dictionary<string, string>(StringComparer.Ordinal));
            }

            var (exit, stdout, stderr) = RunCli(workDir, "link", "--db", db);

            Assert.Equal(1, exit);
            Assert.Contains("no FrontendCallSite nodes", stderr, StringComparison.Ordinal);
            Assert.Contains("slnmap analyze-ts ", stderr, StringComparison.Ordinal);
            AssertNoStackTrace(stdout, stderr);
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public async Task Link_RealPipeline_AnalyzeThenAnalyzeTsThenLink_ProducesExpectedTaxonomyAndEdges()
    {
        string workDir = Path.Combine(Path.GetTempPath(), $"slnmap-link-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        string db = Path.Combine(workDir, "e2e.db");
        try
        {
            var analyze = RunCli(workDir, "analyze", TestPaths.FixtureSolution, "--db", db);
            Assert.True(analyze.ExitCode == 0, $"analyze failed: {analyze.Stdout}\n{analyze.Stderr}");

            string frontendRoot = Path.Combine(TestPaths.RepoRoot, "tests", "fixtures-ts", "cross-stack-fixture");
            var analyzeTs = RunCli(workDir, "analyze-ts", frontendRoot, "--db", db);
            Assert.True(analyzeTs.ExitCode == 0, $"analyze-ts failed: {analyzeTs.Stdout}\n{analyzeTs.Stderr}");

            var link = RunCli(workDir, "link", "--db", db, "--verbose");
            Assert.True(link.ExitCode == 0, $"link failed: {link.Stdout}\n{link.Stderr}");
            Assert.Contains("Linked:", link.Stdout, StringComparison.Ordinal);
            Assert.Contains("Disclosed:", link.Stdout, StringComparison.Ordinal);
            // The verb-mismatch entry's disclosure names the conflicting endpoint by fqn.
            Assert.Contains("no DELETE registered; GET /api/vendors exists", link.Stdout, StringComparison.Ordinal);

            await using var store = new SqliteGraphStore(db);
            var graph = await store.LoadGraphAsync();
            var callsEndpointEdges = graph.Edges.Where(e => e.Kind == RelationshipKind.CallsEndpoint).ToList();

            // 6 call sites in the fixture: list/update/current linked with 1 edge each, notify
            // with 3 (fan-out) = 6 edges total; bulkImport/removeAll get none.
            Assert.Equal(6, callsEndpointEdges.Count);

            var meta = await store.GetMetaAsync();
            Assert.True(meta.ContainsKey(MetaKeys.LinkerLastRun));
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public async Task Link_ThenReAnalyze_PreservesLinkerLastRunAndPrintsStalenessNote()
    {
        // cross-stack-linker-implementation.md Part 2.3: analyze must NOT clear LinkerLastRun
        // (or FrontendLastAnalyzed) from meta -- it owns only its own 5 keys. Before this fix,
        // analyze's meta dict was built from scratch, silently wiping every other producer's
        // meta on every re-run.
        string workDir = Path.Combine(Path.GetTempPath(), $"slnmap-link-restale-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        string db = Path.Combine(workDir, "restale.db");
        try
        {
            var analyze1 = RunCli(workDir, "analyze", TestPaths.FixtureSolution, "--db", db);
            Assert.True(analyze1.ExitCode == 0, $"first analyze failed: {analyze1.Stdout}\n{analyze1.Stderr}");

            string frontendRoot = Path.Combine(TestPaths.RepoRoot, "tests", "fixtures-ts", "cross-stack-fixture");
            var analyzeTs = RunCli(workDir, "analyze-ts", frontendRoot, "--db", db);
            Assert.True(analyzeTs.ExitCode == 0, $"analyze-ts failed: {analyzeTs.Stdout}\n{analyzeTs.Stderr}");

            var link = RunCli(workDir, "link", "--db", db);
            Assert.True(link.ExitCode == 0, $"link failed: {link.Stdout}\n{link.Stderr}");

            // Re-analyze the C# side only, unchanged. Before the fix, this would have wiped
            // FrontendLastAnalyzed/LinkerLastRun from meta on save regardless of whether it was
            // a full or incremental rebuild.
            var analyze2 = RunCli(workDir, "analyze", TestPaths.FixtureSolution, "--db", db);
            Assert.True(analyze2.ExitCode == 0, $"second analyze failed: {analyze2.Stdout}\n{analyze2.Stderr}");
            Assert.Contains("cross-stack links were computed before this analysis", analyze2.Stdout, StringComparison.Ordinal);

            await using var store = new SqliteGraphStore(db);
            var meta = await store.GetMetaAsync();
            Assert.True(meta.ContainsKey(MetaKeys.LinkerLastRun), "LinkerLastRun must survive a subsequent analyze run.");
            Assert.True(meta.ContainsKey(MetaKeys.FrontendLastAnalyzed), "FrontendLastAnalyzed must survive a subsequent analyze run.");

            // MetaKeys.FrontendLastAnalyzed's own doc comment scopes the node-dropping behavior
            // to analyze's "next full REBUILD" specifically -- an INCREMENTAL re-analysis of an
            // unchanged solution (this case: same version, existing non-empty graph) reuses the
            // previous baseline wholesale, so frontend nodes and their CallsEndpoint edges
            // survive here. (A full rebuild -- a fresh db, or a version bump -- does still drop
            // them; that path is exercised by Link_ThenFullRebuildAnalyze_DropsFrontendDataAndEdges
            // below.) The staleness note fires unconditionally on LinkerLastRun's mere presence
            // regardless of which case applies, since the CLI has no cheap way to know which
            // happened -- a conservative nudge, not a precise diff.
            var graph = await store.LoadGraphAsync();
            Assert.Contains(graph.Nodes, n => n.Kind == NodeKind.FrontendCallSite);
            Assert.Contains(graph.Edges, e => e.Kind == RelationshipKind.CallsEndpoint);
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    [Fact]
    public async Task Link_ThenFullRebuildAnalyze_DropsFrontendDataAndEdges_ButNotTheStalenessSignalReasoning()
    {
        // The genuine staleness risk (§Q3): a FULL rebuild (forced here via a tampered
        // ToolVersion, mirroring LoadPreviousAsync's own "any version difference forces a full
        // rebuild" rule) drops FrontendCallSite nodes and, with them, their CallsEndpoint
        // edges -- unlike the incremental case above.
        string workDir = Path.Combine(Path.GetTempPath(), $"slnmap-link-fullrebuild-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);
        string db = Path.Combine(workDir, "fullrebuild.db");
        try
        {
            var analyze1 = RunCli(workDir, "analyze", TestPaths.FixtureSolution, "--db", db);
            Assert.True(analyze1.ExitCode == 0, $"first analyze failed: {analyze1.Stdout}\n{analyze1.Stderr}");

            string frontendRoot = Path.Combine(TestPaths.RepoRoot, "tests", "fixtures-ts", "cross-stack-fixture");
            var analyzeTs = RunCli(workDir, "analyze-ts", frontendRoot, "--db", db);
            Assert.True(analyzeTs.ExitCode == 0, $"analyze-ts failed: {analyzeTs.Stdout}\n{analyzeTs.Stderr}");

            var link = RunCli(workDir, "link", "--db", db);
            Assert.True(link.ExitCode == 0, $"link failed: {link.Stdout}\n{link.Stderr}");

            // Force the next analyze into the full-rebuild path.
            await using (var store = new SqliteGraphStore(db))
            {
                var graph = await store.LoadGraphAsync();
                var meta = new Dictionary<string, string>(await store.GetMetaAsync(), StringComparer.Ordinal)
                {
                    [MetaKeys.ToolVersion] = "0.0.0-forced-mismatch",
                };
                var files = (await store.GetFileHashesAsync()).Select(p => new FileRecord(p.Key, p.Value)).ToList();
                await store.SaveAsync(graph, files, meta);
            }

            var analyze2 = RunCli(workDir, "analyze", TestPaths.FixtureSolution, "--db", db);
            Assert.True(analyze2.ExitCode == 0, $"second analyze failed: {analyze2.Stdout}\n{analyze2.Stderr}");
            Assert.Contains("full re-analysis", analyze2.Stderr, StringComparison.Ordinal); // ConsoleStatusLine writes to stderr
            Assert.Contains("cross-stack links were computed before this analysis", analyze2.Stdout, StringComparison.Ordinal);

            await using var finalStore = new SqliteGraphStore(db);
            var finalGraph = await finalStore.LoadGraphAsync();
            Assert.DoesNotContain(finalGraph.Nodes, n => n.Kind == NodeKind.FrontendCallSite);
            Assert.DoesNotContain(finalGraph.Edges, e => e.Kind == RelationshipKind.CallsEndpoint);

            // But LinkerLastRun itself survives even this -- it's meta this command doesn't own.
            var finalMeta = await finalStore.GetMetaAsync();
            Assert.True(finalMeta.ContainsKey(MetaKeys.LinkerLastRun));
        }
        finally
        {
            TryDeleteDirectory(workDir);
        }
    }

    private static void AssertNoStackTrace(string stdout, string stderr)
    {
        string combined = $"{stdout}\n{stderr}";
        Assert.DoesNotContain("Unhandled exception", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", combined, StringComparison.Ordinal);
        Assert.DoesNotContain(".cs:line", combined, StringComparison.Ordinal);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) RunCli(string workDir, params string[] args)
    {
        string config = AppContext.BaseDirectory.Replace('\\', '/')
            .Contains("/Release/", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        string cliDll = Path.Combine(TestPaths.RepoRoot, "src", "Slnmap.Cli", "bin", config, "net9.0", "slnmap.dll");
        Assert.True(File.Exists(cliDll), $"CLI not built at {cliDll}");

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(cliDll);
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
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
