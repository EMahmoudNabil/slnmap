using Slnmap.Analysis;
using Slnmap.Core.Graph;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// The watch-mode correctness contract (reports/watch-mode-investigation.md): a warm re-analysis
/// (workspace kept resident, text change applied via WithDocumentText) must produce EXACTLY the
/// graph a cold re-analysis of the modified tree produces — same node ids, same edges. The
/// sub-second speed is worthless if the warm snapshot drifts. One sequential journey to pay the
/// fixture restore + workspace open once.
/// </summary>
public sealed class ResidentAnalyzerTests : IDisposable
{
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
    public async Task WarmReanalysis_MatchesColdAnalysis_AndDetectsStructuralChanges()
    {
        CopyDirectory(TestPaths.FixtureSolutionDirectory, _root);
        DotNet.Run("restore FixtureSolution.sln", _root);
        string solutionPath = Path.Combine(_root, "FixtureSolution.sln");

        using var resident = new ResidentAnalyzer();
        var initial = await resident.InitializeAsync(solutionPath);
        GraphAssert.Node(initial.Graph, NodeKind.Class, "Fixture.Lib.Circle");

        // --- Step 1: whitespace-only touch → warm path, graph IDENTICAL (watch skips the save).
        string shapes = Path.Combine(_root, "FixtureLib", "Shapes.cs");
        File.AppendAllText(shapes, "\n");
        var whitespace = await resident.ReanalyzeChangedAsync([shapes]);
        Assert.False(whitespace.RequiresReload);
        AssertGraphsIdentical(initial.Graph, whitespace.Snapshot!.Graph);

        // --- Step 2: a SEMANTIC change → warm result must equal a cold analysis of the same tree.
        string original = File.ReadAllText(shapes).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains("public sealed class Circle", original, StringComparison.Ordinal);
        File.WriteAllText(shapes, original.Replace(
            "public sealed class Circle",
            "public class WarmAddition\n{\n    public int Answer() => 42;\n}\n\npublic sealed class Circle",
            StringComparison.Ordinal));

        var warm = await resident.ReanalyzeChangedAsync([shapes]);
        Assert.False(warm.RequiresReload);
        GraphAssert.Node(warm.Snapshot!.Graph, NodeKind.Class, "Fixture.Lib.WarmAddition");
        GraphAssert.Node(warm.Snapshot.Graph, NodeKind.Method, "Fixture.Lib.WarmAddition.Answer()");

        var cold = await new RoslynSolutionAnalyzer().AnalyzeAsync(solutionPath);
        AssertGraphsIdentical(cold.Graph, warm.Snapshot.Graph);

        // The warm plan only re-walked the changed file's blast radius, not the solution.
        Assert.True(
            warm.Snapshot.Stats.DocumentsAnalyzed < cold.Stats.DocumentsAnalyzed,
            $"warm path re-walked {warm.Snapshot.Stats.DocumentsAnalyzed} of {cold.Stats.DocumentsAnalyzed} documents — not incremental");

        // --- Step 3: a NEW file is not a document of the warm snapshot → declared, not guessed.
        string added = Path.Combine(_root, "FixtureLib", "WatchAddedFile.cs");
        File.WriteAllText(added, "namespace Fixture.Lib;\n\npublic sealed class WatchAddedFile\n{\n}\n");
        var structural = await resident.ReanalyzeChangedAsync([added]);
        Assert.True(structural.RequiresReload);
        Assert.Null(structural.Snapshot);

        // --- Step 4: the reload picks the new file up AND stays incremental across the reload
        // (the plan is hash-driven; unchanged documents are not re-walked).
        var reloaded = await resident.ReloadAsync();
        GraphAssert.Node(reloaded.Graph, NodeKind.Class, "Fixture.Lib.WatchAddedFile");
        Assert.True(
            reloaded.Stats.DocumentsAnalyzed < cold.Stats.DocumentsAnalyzed,
            $"reload re-walked {reloaded.Stats.DocumentsAnalyzed} documents — the hash-driven plan should have skipped unchanged ones");

        var coldAfterAdd = await new RoslynSolutionAnalyzer().AnalyzeAsync(solutionPath);
        AssertGraphsIdentical(coldAfterAdd.Graph, reloaded.Graph);
    }

    private static void AssertGraphsIdentical(CodeGraph expected, CodeGraph actual)
    {
        var expectedNodes = expected.Nodes.ToHashSet();
        var actualNodes = actual.Nodes.ToHashSet();
        Assert.True(expectedNodes.SetEquals(actualNodes),
            "node sets diverged between cold and warm analysis: "
            + $"only-cold=[{string.Join(", ", expectedNodes.Except(actualNodes).Select(n => n.Fqn).Take(5))}] "
            + $"only-warm=[{string.Join(", ", actualNodes.Except(expectedNodes).Select(n => n.Fqn).Take(5))}]");
        Assert.True(expected.Edges.ToHashSet().SetEquals(actual.Edges), "edge sets diverged between cold and warm analysis");
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

