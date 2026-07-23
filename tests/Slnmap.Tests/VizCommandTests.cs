using System.Diagnostics;
using Slnmap.Core.Graph;
using Slnmap.Core.Storage;
using Slnmap.Storage;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// `slnmap viz` error paths, end to end through the real CLI: every failure must produce an
/// actionable message and exit 1 — and never a broken HTML file on disk.
/// </summary>
public sealed class VizCommandTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "slnmap-vizcmd-tests", Guid.NewGuid().ToString("N"));

    public VizCommandTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the OS temp cleaner will get it eventually.
        }
    }

    [Fact]
    public void MissingDatabase_FailsWithHouseStyleMessage_AndWritesNoFile()
    {
        string output = Path.Combine(_directory, "graph.html");
        var (exit, _, stderr) = RunCli("viz", "--db", Path.Combine(_directory, "missing.db"), "--output", output);

        Assert.Equal(1, exit);
        Assert.Contains("No graph at", stderr, StringComparison.Ordinal);
        Assert.Contains("Run 'slnmap analyze <solution>' first.", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task EmptyGraph_FailsWithMessage_NotABrokenFile()
    {
        string db = Path.Combine(_directory, "empty.db");
        await using (var store = new SqliteGraphStore(db))
        {
            await store.InitializeAsync();
        }

        string output = Path.Combine(_directory, "graph.html");
        var (exit, _, stderr) = RunCli("viz", "--db", db, "--output", output);

        Assert.Equal(1, exit);
        Assert.Contains("The graph is empty.", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task UnknownProject_FailsWithExactMessage_AndWritesNoFile()
    {
        string db = Path.Combine(_directory, "one.db");
        var graph = new CodeGraph();
        graph.AddNode(SymbolNode.Create(NodeKind.Project, "App", "App", Path.Combine(_directory, "App.csproj")));
        await using (var store = new SqliteGraphStore(db))
        {
            await store.SaveAsync(graph, [], new Dictionary<string, string> { [MetaKeys.LastAnalyzed] = "test" });
        }

        string output = Path.Combine(_directory, "graph.html");
        var (exit, _, stderr) = RunCli("viz", "--db", db, "--project", "Nope", "--output", output);

        Assert.Equal(1, exit);
        Assert.Contains("Unknown project 'Nope'. Valid projects: App.", stderr, StringComparison.Ordinal);
        Assert.False(File.Exists(output));
    }

    private static (int ExitCode, string Stdout, string Stderr) RunCli(params string[] args)
    {
        string config = AppContext.BaseDirectory.Replace('\\', '/')
            .Contains("/Release/", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        string cliDll = Path.Combine(TestPaths.RepoRoot, "src", "Slnmap.Cli", "bin", config, "net9.0", "slnmap.dll");
        Assert.True(File.Exists(cliDll), $"CLI not built at {cliDll}");

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = TestPaths.RepoRoot,
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
        var stderrTask = process.StandardError.ReadToEndAsync();
        string stdout = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(60_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("slnmap CLI timed out.");
        }

        return (process.ExitCode, stdout, stderrTask.Result);
    }
}
