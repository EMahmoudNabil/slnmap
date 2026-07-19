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
    public static void Run(string arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo("dotnet", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start dotnet.");
        var stderrTask = process.StandardError.ReadToEndAsync();
        string stdout = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(300_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException($"dotnet {arguments} timed out.");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet {arguments} exited with {process.ExitCode}:\n{stdout}\n{stderrTask.Result}");
        }
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
