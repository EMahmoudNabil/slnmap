using System.Diagnostics;
using Slnmap.Analysis;
using Slnmap.Core.Storage;
using Slnmap.Mcp;
using Slnmap.Storage;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// Regression tests for the field-observed upgrade gotcha: after a slnmap version bump,
/// `analyze` must not silently reuse a graph built by a different version as an incremental
/// baseline (see reports/db-version-check-report.md), and `serve` must warn — but still serve —
/// when the database it's about to read predates or postdates the running version.
/// </summary>
public sealed class DbVersionCheckTests : IDisposable
{
    /// <summary>
    /// The version this test run's slnmap assembly reports, computed the same way
    /// Program.cs's <c>CurrentVersion()</c> does (AssemblyVersion, not the git-sha-suffixed
    /// AssemblyInformationalVersion) — kept in sync via reflection so these tests never hardcode
    /// a version string that goes stale on the next release bump.
    /// </summary>
    private static readonly string CurrentVersion =
        typeof(VizExporter).Assembly.GetName().Version?.ToString(3) ?? "unknown";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "slnmap-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the OS temp cleaner will get it eventually.
        }
    }

    [Fact]
    public async Task OldDatabase_NoVersionMetadata_TriggersFullRebuildAndWritesVersion()
    {
        string solutionPath = CopyFixtureSolution();
        string dbPath = Path.Combine(_root, "graph.db");

        // Simulate a pre-fix database: a real graph, but no tool_version meta key at all —
        // exactly what every database written before this fix looks like.
        var snapshot = await new RoslynSolutionAnalyzer().AnalyzeAsync(solutionPath);
        await using (var seed = new SqliteGraphStore(dbPath))
        {
            var meta = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [MetaKeys.SolutionPath] = solutionPath,
                [MetaKeys.LastAnalyzed] = "test",
            };
            await seed.SaveAsync(snapshot.Graph, snapshot.Files, meta);
        }

        var (exit, stdout, stderr) = RunCli("analyze", solutionPath, "-d", dbPath);

        Assert.Equal(0, exit);
        Assert.Contains($"slnmap version changed (unknown -> {CurrentVersion}): performing full re-analysis", stderr, StringComparison.Ordinal);
        Assert.Contains("analyzed, 0 skipped", stdout, StringComparison.Ordinal); // full rebuild, nothing carried over

        await using var check = new SqliteGraphStore(dbPath);
        await check.InitializeAsync();
        var savedMeta = await check.GetMetaAsync();
        Assert.Equal(CurrentVersion, savedMeta[MetaKeys.ToolVersion]);
    }

    [Fact]
    public async Task MismatchedVersion_TriggersFullRebuildAndLogsLine()
    {
        string solutionPath = CopyFixtureSolution();
        string dbPath = Path.Combine(_root, "graph.db");

        var snapshot = await new RoslynSolutionAnalyzer().AnalyzeAsync(solutionPath);
        await using (var seed = new SqliteGraphStore(dbPath))
        {
            var meta = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [MetaKeys.SolutionPath] = solutionPath,
                [MetaKeys.LastAnalyzed] = "test",
                [MetaKeys.ToolVersion] = "0.0.1", // a real but stale/bogus version, not just absent
            };
            await seed.SaveAsync(snapshot.Graph, snapshot.Files, meta);
        }

        var (exit, stdout, stderr) = RunCli("analyze", solutionPath, "-d", dbPath);

        Assert.Equal(0, exit);
        Assert.Contains($"slnmap version changed (0.0.1 -> {CurrentVersion}): performing full re-analysis", stderr, StringComparison.Ordinal);
        Assert.Contains("analyzed, 0 skipped", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void MatchingVersion_IncrementalPathUnaffected()
    {
        string solutionPath = CopyFixtureSolution();
        string dbPath = Path.Combine(_root, "graph.db");

        var (coldExit, _, coldErr) = RunCli("analyze", solutionPath, "-d", dbPath);
        Assert.Equal(0, coldExit);
        Assert.DoesNotContain("slnmap version changed", coldErr, StringComparison.Ordinal);

        // Whitespace-only touch of one fixture file — should trigger the ordinary incremental
        // path (some documents skipped), not a version-driven full rebuild.
        string touched = Path.Combine(_root, "FixtureLib", "EvictionChain.cs");
        File.AppendAllText(touched, "\n");

        var (incExit, incOut, incErr) = RunCli("analyze", solutionPath, "-d", dbPath);
        Assert.Equal(0, incExit);
        Assert.DoesNotContain("slnmap version changed", incErr, StringComparison.Ordinal);
        Assert.Contains("incremental: reusing", incErr, StringComparison.Ordinal);
        Assert.DoesNotContain("analyzed, 0 skipped", incOut, StringComparison.Ordinal); // some documents were carried over
    }

    [Fact]
    public async Task Serve_MismatchedVersion_PrintsWarning_AndToolsStillRespond()
    {
        string solutionPath = CopyFixtureSolution();
        string dbPath = Path.Combine(_root, "graph.db");

        var snapshot = await new RoslynSolutionAnalyzer().AnalyzeAsync(solutionPath);
        await using (var seed = new SqliteGraphStore(dbPath))
        {
            var meta = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [MetaKeys.SolutionPath] = solutionPath,
                [MetaKeys.LastAnalyzed] = "test",
                [MetaKeys.ToolVersion] = "0.0.1",
            };
            await seed.SaveAsync(snapshot.Graph, snapshot.Files, meta);
        }

        using var process = StartCli("serve", "-d", dbPath);
        try
        {
            string stderrSoFar = await ProcessOutput.ReadUntilAsync(
                process.StandardError,
                line => line.Contains("Slnmap MCP server ready", StringComparison.Ordinal),
                TimeSpan.FromSeconds(30));

            Assert.Contains(
                $"this database was built with slnmap 0.0.1, but this is {CurrentVersion}",
                stderrSoFar,
                StringComparison.Ordinal);
            Assert.False(process.HasExited, "serve must not refuse to start on a version mismatch — it should only warn.");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            process.WaitForExit(10_000);
        }

        // Tools themselves are never gated on tool_version (staleness is advisory, not a query
        // concern) — confirmed directly against the same mismatched-version database, once the
        // server process has fully released its file handles.
        await using var store = new SqliteGraphStore(dbPath);
        var queries = new SlnmapQueries(store);
        string result = await queries.FindSymbolAsync("IEvictionContract", null);
        Assert.Contains("Fixture.Lib.IEvictionContract", result, StringComparison.Ordinal);
    }

    private string CopyFixtureSolution()
    {
        CopyDirectory(TestPaths.FixtureSolutionDirectory, _root);
        DotNet.Run("restore FixtureSolution.sln", _root);
        return Path.Combine(_root, "FixtureSolution.sln");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            if (relative.StartsWith("bin", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("obj", StringComparison.OrdinalIgnoreCase)
                || relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    // ReadUntilAsync now lives in TestInfrastructure.cs's ProcessOutput class (shared with
    // DotNet.Run, which needed the same timeout-bounded-read treatment — see issue #10).

    private static (int ExitCode, string Stdout, string Stderr) RunCli(params string[] args)
    {
        using var process = StartCli(args);
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("slnmap CLI timed out.");
        }

        return (process.ExitCode, stdout, stderr);
    }

    private static Process StartCli(params string[] args)
    {
        string config = AppContext.BaseDirectory.Replace('\\', '/')
            .Contains("/Release/", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        string cliDll = Path.Combine(TestPaths.RepoRoot, "src", "Slnmap.Cli", "bin", config, "net9.0", "slnmap.dll");
        Assert.True(File.Exists(cliDll), $"CLI not built at {cliDll}");

        var psi = new ProcessStartInfo("dotnet")
        {
            // Never the repo or the fixture copy: a stray default slnmap.db must not land there.
            WorkingDirectory = Path.GetTempPath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(cliDll);
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start the slnmap CLI.");
    }
}
