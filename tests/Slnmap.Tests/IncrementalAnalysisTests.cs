using Slnmap.Analysis;
using Slnmap.Core.Graph;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// Exercises incremental analysis against a private copy of the fixture solution
/// so file edits never touch the repository.
/// </summary>
public sealed class IncrementalAnalysisTests : IDisposable
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
            // Best effort; the OS temp cleaner will get it eventually.
        }
    }

    [Fact]
    public async Task Incremental_ReanalyzesOnlyChangedAndDependentDocuments()
    {
        CopyDirectory(TestPaths.FixtureSolutionDirectory, _root);
        DotNet.Run("restore FixtureSolution.sln", _root);
        string solutionPath = Path.Combine(_root, "FixtureSolution.sln");
        var analyzer = new RoslynSolutionAnalyzer();

        var first = await analyzer.AnalyzeAsync(solutionPath);
        Assert.True(first.Stats.DocumentsAnalyzed > 0);

        // Nothing changed: everything is skipped and the graph carries over verbatim.
        var unchanged = await analyzer.AnalyzeAsync(solutionPath, first);
        Assert.Equal(0, unchanged.Stats.DocumentsAnalyzed);
        Assert.Equal(first.Graph.NodeCount, unchanged.Graph.NodeCount);
        Assert.Equal(first.Graph.EdgeCount, unchanged.Graph.EdgeCount);

        // Add a method to Circle. Shapes.cs changed; Program.cs depends on symbols
        // declared there (Circle, Geometry), so exactly those two are re-analyzed.
        string shapesPath = Path.Combine(_root, "FixtureLib", "Shapes.cs");
        const string areaLine = "public override double Area() => Math.PI * Radius * Radius;";
        File.WriteAllText(shapesPath, File.ReadAllText(shapesPath).Replace(
            areaLine,
            areaLine + "\n\n    public double Perimeter() => 2 * Math.PI * Radius;"));

        var second = await analyzer.AnalyzeAsync(solutionPath, first);

        Assert.True(second.Stats.DocumentsAnalyzed >= 2, $"expected >= 2 documents, got {second.Stats.DocumentsAnalyzed}");
        Assert.True(
            second.Stats.DocumentsAnalyzed < first.Stats.DocumentsAnalyzed,
            $"expected fewer than the full {first.Stats.DocumentsAnalyzed} documents, got {second.Stats.DocumentsAnalyzed}");

        // The new member and its edges are present.
        var perimeter = GraphAssert.Node(second.Graph, NodeKind.Method, "Fixture.Lib.Circle.Perimeter()");
        var radius = GraphAssert.Node(second.Graph, NodeKind.Property, "Fixture.Lib.Circle.Radius");
        GraphAssert.Edge(second.Graph, perimeter, radius, RelationshipKind.References);

        // Untouched declarations and re-derived cross-file edges survived.
        GraphAssert.Node(second.Graph, NodeKind.Class, "Fixture.Lib.Square");
        var totalArea = GraphAssert.Node(
            second.Graph,
            NodeKind.Method,
            "Fixture.Lib.Geometry.TotalArea(System.Collections.Generic.IEnumerable<Fixture.Lib.IShape>)");
        Assert.Contains(
            second.Graph.IncomingEdges(totalArea.Id, RelationshipKind.Calls),
            e => second.Graph.TryGetNode(e.SourceId, out var caller)
                && caller.FilePath?.EndsWith("Program.cs", StringComparison.Ordinal) == true);

        // The stored hash for the edited file was refreshed.
        string? firstHash = first.Files.Single(f => f.Path == shapesPath).ContentHash;
        string? secondHash = second.Files.Single(f => f.Path == shapesPath).ContentHash;
        Assert.NotEqual(firstHash, secondHash);
    }

    [Fact]
    public async Task Incremental_WithTwoTopLevelEntryPoints_PreservesTheEdgeCensus()
    {
        // Regression for the entry-point FQN collision: with merged entry-point nodes, editing a
        // file one app depends on made the planner re-analyze the OTHER app's Program.cs and
        // permanently drop the first app's edges. Both edit directions are exercised so the test
        // fails without the fix regardless of which file used to win the node dedup.
        CopyDirectory(TestPaths.FixtureSolutionDirectory, _root);
        DotNet.Run("restore FixtureSolution.sln", _root);
        string solutionPath = Path.Combine(_root, "FixtureSolution.sln");
        var analyzer = new RoslynSolutionAnalyzer();

        var full = await analyzer.AnalyzeAsync(solutionPath);

        // Edit FixtureCli's dependency (Text.cs): only it and FixtureCli/Program.cs re-analyze.
        File.AppendAllText(Path.Combine(_root, "FixtureLib", "Text.cs"), "\n// census probe 1\n");
        var afterTextEdit = await analyzer.AnalyzeAsync(solutionPath, full);
        Assert.True(afterTextEdit.Stats.DocumentsAnalyzed < full.Stats.DocumentsAnalyzed);
        Assert.Equal(full.Graph.NodeCount, afterTextEdit.Graph.NodeCount);
        Assert.Equal(full.Graph.EdgeCount, afterTextEdit.Graph.EdgeCount);

        // Edit FixtureApp's dependency (Shapes.cs): the other direction.
        File.AppendAllText(Path.Combine(_root, "FixtureLib", "Shapes.cs"), "\n// census probe 2\n");
        var afterShapesEdit = await analyzer.AnalyzeAsync(solutionPath, afterTextEdit);
        Assert.True(afterShapesEdit.Stats.DocumentsAnalyzed < full.Stats.DocumentsAnalyzed);
        Assert.Equal(full.Graph.NodeCount, afterShapesEdit.Graph.NodeCount);
        Assert.Equal(full.Graph.EdgeCount, afterShapesEdit.Graph.EdgeCount);

        // Both entry points survived the cycle as distinct nodes.
        GraphAssert.Node(afterShapesEdit.Graph, NodeKind.Method, "FixtureApp.<top-level-statements-entry-point>");
        GraphAssert.Node(afterShapesEdit.Graph, NodeKind.Method, "FixtureCli.<top-level-statements-entry-point>");
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
