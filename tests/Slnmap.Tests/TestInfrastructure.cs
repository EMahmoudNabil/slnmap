using System.Diagnostics;
using Slnmap.Core.Graph;
using Xunit.Sdk;

namespace Slnmap.Tests;

internal static class TestPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string FixtureSolutionDirectory => Path.Combine(RepoRoot, "tests", "fixtures", "FixtureSolution");

    public static string FixtureSolution => Path.Combine(FixtureSolutionDirectory, "FixtureSolution.sln");

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Slnmap.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root from " + AppContext.BaseDirectory);
    }
}

internal static class DotNet
{
    // A generous ceiling that never fires in a healthy run — its only job is to convert an
    // indefinite hang (observed: issue #10, an unbounded read stalling on a contended machine)
    // into a clear TimeoutException instead of a silent, unbounded wait.
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(120);

    public static void Run(string arguments, string workingDirectory)
    {
        // Every call site here is `restore` against a fresh temp copy of FixtureSolution, run
        // from several test classes' fixtures concurrently (xUnit parallelizes across classes by
        // default). --disable-build-servers stops these concurrent invocations from contending
        // over shared MSBuild/VBCSCompiler build-server state (observed: issue #10).
        var startInfo = new ProcessStartInfo("dotnet", $"{arguments} --disable-build-servers")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start dotnet.");
        // Both reads are started before either is awaited, so stdout and stderr drain
        // concurrently — required to avoid a pipe deadlock on a process that writes enough to
        // both streams to fill an OS pipe buffer.
        var stderrTask = ProcessOutput.ReadUntilAsync(process.StandardError, predicate: null, ReadTimeout);
        var stdoutTask = ProcessOutput.ReadUntilAsync(process.StandardOutput, predicate: null, ReadTimeout);
        string stdout = stdoutTask.GetAwaiter().GetResult();
        if (!process.WaitForExit(300_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"dotnet {arguments} timed out.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet {arguments} exited with {process.ExitCode}:\n{stdout}\n{stderrTask.GetAwaiter().GetResult()}");
        }
    }
}

/// <summary>
/// Shared timeout-bounded stream reading for tests that drive the CLI or another process as a
/// subprocess — an unbounded <c>ReadToEnd()</c>/<c>ReadLine()</c> turns a stalled child process
/// into an indefinite hang instead of a clear failure (issue #10).
/// </summary>
internal static class ProcessOutput
{
    /// <summary>
    /// Reads lines until <paramref name="predicate"/> matches one (returning everything read so
    /// far, including the matching line), until end-of-stream if <paramref name="predicate"/> is
    /// <see langword="null"/> (returning everything read), or until <paramref name="timeout"/>
    /// elapses, whichever comes first. A <see langword="null"/> predicate reaching end-of-stream
    /// is success (the full output); a non-null predicate reaching end-of-stream without a match,
    /// or the timeout firing in either mode, throws <see cref="TimeoutException"/>.
    /// </summary>
    public static async Task<string> ReadUntilAsync(StreamReader reader, Func<string, bool>? predicate, TimeSpan timeout)
    {
        var lines = new List<string>();
        using var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            var lineTask = reader.ReadLineAsync();
            var completed = await Task.WhenAny(lineTask, Task.Delay(timeout, cts.Token)).ConfigureAwait(false);
            if (completed != lineTask)
            {
                break;
            }

            string? line = await lineTask.ConfigureAwait(false);
            if (line is null)
            {
                if (predicate is null)
                {
                    return string.Join('\n', lines);
                }

                break;
            }

            lines.Add(line);
            if (predicate is not null && predicate(line))
            {
                return string.Join('\n', lines);
            }
        }

        throw new TimeoutException(
            $"Did not see the expected line within {timeout}. Output so far:\n{string.Join('\n', lines)}");
    }
}

internal static class GraphAssert
{
    public static SymbolNode Node(CodeGraph graph, NodeKind kind, string fqn)
    {
        var matches = graph.Nodes.Where(n => n.Kind == kind && n.Fqn == fqn).ToList();
        return matches.Count == 1
            ? matches[0]
            : throw new XunitException(
                $"Expected exactly one {kind} node with FQN '{fqn}' but found {matches.Count}. " +
                $"Nodes of that kind: {string.Join(", ", graph.Nodes.Where(n => n.Kind == kind).Select(n => n.Fqn).OrderBy(f => f, StringComparer.Ordinal))}");
    }

    public static void Edge(CodeGraph graph, SymbolNode source, SymbolNode target, RelationshipKind kind)
    {
        if (!graph.OutgoingEdges(source.Id, kind).Any(e => e.TargetId == target.Id))
        {
            throw new XunitException(
                $"Expected edge {source.Fqn} —{kind}→ {target.Fqn}. " +
                $"Outgoing {kind} edges of source: {string.Join(", ", graph.OutgoingEdges(source.Id, kind).Select(e => graph.TryGetNode(e.TargetId, out var n) ? n.Fqn : e.TargetId))}");
        }
    }
}
