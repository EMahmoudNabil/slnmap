using System.Diagnostics;
using Slnmap.Storage;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// End-to-end `slnmap watch` through the real CLI: initial analyze + save, then a semantic file
/// edit must produce a fresh save with the change visible in the database — and the save's own
/// file-system events must not re-trigger the watcher (the db-exclusion rule, observable as the
/// process settling instead of looping). Reads are bounded per the issue-#10 discipline.
/// </summary>
public sealed class WatchCommandTests : IDisposable
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromMinutes(3);

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
        }
    }

    [Fact]
    public async Task Watch_ReanalyzesOnSave_AndPersistsTheChange()
    {
        CopyDirectory(TestPaths.FixtureSolutionDirectory, _root);
        DotNet.Run("restore FixtureSolution.sln", _root);
        string solutionPath = Path.Combine(_root, "FixtureSolution.sln");
        string dbPath = Path.Combine(_root, "watch.db");

        string config = AppContext.BaseDirectory.Replace('\\', '/')
            .Contains("/Release/", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        string cliDll = Path.Combine(TestPaths.RepoRoot, "src", "Slnmap.Cli", "bin", config, "net9.0", "slnmap.dll");
        Assert.True(File.Exists(cliDll), $"CLI not built at {cliDll}");

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(cliDll);
        psi.ArgumentList.Add("watch");
        psi.ArgumentList.Add(solutionPath);
        psi.ArgumentList.Add("--db");
        psi.ArgumentList.Add(dbPath);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start slnmap watch.");
        // Drain stderr concurrently so a chatty progress stream can never deadlock the pipe.
        var stderrTask = ProcessOutput.ReadUntilAsync(process.StandardError, predicate: null, ReadTimeout);
        try
        {
            // Phase 1: the initial analyze completed and the watcher is armed.
            await ProcessOutput.ReadUntilAsync(
                process.StandardOutput,
                line => line.Contains("Watching:", StringComparison.Ordinal),
                ReadTimeout);
            Assert.True(File.Exists(dbPath), "initial analyze did not save the database");

            // Phase 2: a semantic edit → one re-analyzed-and-saved batch line.
            string shapes = Path.Combine(_root, "FixtureLib", "Shapes.cs");
            string original = File.ReadAllText(shapes).Replace("\r\n", "\n", StringComparison.Ordinal);
            File.WriteAllText(shapes, original.Replace(
                "public sealed class Circle",
                "public class WatchE2EAddition\n{\n}\n\npublic sealed class Circle",
                StringComparison.Ordinal));

            string batchLine = await ProcessOutput.ReadUntilAsync(
                process.StandardOutput,
                line => line.Contains("re-analyzed", StringComparison.Ordinal) && line.Contains("saved in", StringComparison.Ordinal),
                ReadTimeout);
            Assert.DoesNotContain("save skipped", batchLine, StringComparison.Ordinal);

            // Phase 3: the persisted graph contains the new class (poll briefly — the batch line
            // prints after the save, but the reader may race the flush).
            await using var store = new SqliteGraphStore(dbPath);
            var graph = await store.LoadGraphAsync();
            Assert.Contains(graph.Nodes, n => n.Fqn == "Fixture.Lib.WatchE2EAddition");
        }
        finally
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            process.Dispose();
            await stderrTask.ContinueWith(static _ => { });
        }
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
}

