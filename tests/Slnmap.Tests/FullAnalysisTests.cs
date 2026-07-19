using Slnmap.Analysis;
using Slnmap.Core.Analysis;
using Slnmap.Core.Graph;
using Xunit;

namespace Slnmap.Tests;

/// <summary>Restores and analyzes the fixture solution once; shared by all full-analysis tests.</summary>
public sealed class AnalyzedFixtureSolution : IAsyncLifetime
{
    public AnalysisSnapshot Snapshot { get; private set; } = null!;

    public CodeGraph Graph => Snapshot.Graph;

    public async Task InitializeAsync()
    {
        DotNet.Run($"restore \"{TestPaths.FixtureSolution}\"", TestPaths.RepoRoot);
        Snapshot = await new RoslynSolutionAnalyzer().AnalyzeAsync(TestPaths.FixtureSolution);
    }

    public Task DisposeAsync() => Task.CompletedTask;
}

public sealed class FullAnalysisTests : IClassFixture<AnalyzedFixtureSolution>
{
    private readonly AnalyzedFixtureSolution _fixture;

    public FullAnalysisTests(AnalyzedFixtureSolution fixture) => _fixture = fixture;

    private CodeGraph Graph => _fixture.Graph;

    [Fact]
    public void ExtractsTypeNodes()
    {
        GraphAssert.Node(Graph, NodeKind.Interface, "Fixture.Lib.IShape");
        GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.ShapeBase");
        GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.Circle");
        GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.Square");
        GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.Geometry");
    }

    [Fact]
    public void ExtractsMemberNodes()
    {
        GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Lib.IShape.Area()");
        GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Lib.Circle.Area()");
        GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Lib.ShapeBase.Describe()");
        GraphAssert.Node(Graph, NodeKind.Property, "Fixture.Lib.Circle.Radius");
        GraphAssert.Node(
            Graph,
            NodeKind.Method,
            "Fixture.Lib.Geometry.TotalArea(System.Collections.Generic.IEnumerable<Fixture.Lib.IShape>)");
    }

    [Fact]
    public void ExtractsImplementsAndInheritsEdges()
    {
        var shapeBase = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.ShapeBase");
        var shapeContract = GraphAssert.Node(Graph, NodeKind.Interface, "Fixture.Lib.IShape");
        var circle = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.Circle");
        var square = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.Square");

        GraphAssert.Edge(Graph, shapeBase, shapeContract, RelationshipKind.Implements);
        GraphAssert.Edge(Graph, circle, shapeBase, RelationshipKind.Inherits);
        GraphAssert.Edge(Graph, square, shapeBase, RelationshipKind.Inherits);
    }

    [Fact]
    public void ExtractsCallEdges()
    {
        var totalArea = GraphAssert.Node(
            Graph,
            NodeKind.Method,
            "Fixture.Lib.Geometry.TotalArea(System.Collections.Generic.IEnumerable<Fixture.Lib.IShape>)");
        var interfaceArea = GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Lib.IShape.Area()");
        var describe = GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Lib.ShapeBase.Describe()");
        var baseArea = GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Lib.ShapeBase.Area()");

        GraphAssert.Edge(Graph, totalArea, interfaceArea, RelationshipKind.Calls);
        GraphAssert.Edge(Graph, describe, baseArea, RelationshipKind.Calls);
    }

    [Fact]
    public void ExtractsCrossProjectCallFromEntryPoint()
    {
        var totalArea = GraphAssert.Node(
            Graph,
            NodeKind.Method,
            "Fixture.Lib.Geometry.TotalArea(System.Collections.Generic.IEnumerable<Fixture.Lib.IShape>)");

        var callers = Graph.IncomingEdges(totalArea.Id, RelationshipKind.Calls)
            .Select(e => Graph.TryGetNode(e.SourceId, out var n) ? n : null)
            .Where(n => n is not null)
            .ToList();

        Assert.Contains(callers, caller => caller!.FilePath?.EndsWith("Program.cs", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void ExtractsReferenceEdges()
    {
        var circleArea = GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Lib.Circle.Area()");
        var radius = GraphAssert.Node(Graph, NodeKind.Property, "Fixture.Lib.Circle.Radius");
        GraphAssert.Edge(Graph, circleArea, radius, RelationshipKind.References);

        // `new Circle(...)` in the entry point becomes a References edge to the type.
        var circle = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.Circle");
        var referrers = Graph.IncomingEdges(circle.Id, RelationshipKind.References)
            .Select(e => Graph.TryGetNode(e.SourceId, out var n) ? n : null)
            .ToList();
        Assert.Contains(referrers, r => r?.FilePath?.EndsWith("Program.cs", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void ExtractsContainmentChain()
    {
        var project = GraphAssert.Node(Graph, NodeKind.Project, "FixtureLib");
        var fixtureNs = GraphAssert.Node(Graph, NodeKind.Namespace, "Fixture");
        var libNs = GraphAssert.Node(Graph, NodeKind.Namespace, "Fixture.Lib");
        var circle = GraphAssert.Node(Graph, NodeKind.Class, "Fixture.Lib.Circle");
        var circleArea = GraphAssert.Node(Graph, NodeKind.Method, "Fixture.Lib.Circle.Area()");
        var radius = GraphAssert.Node(Graph, NodeKind.Property, "Fixture.Lib.Circle.Radius");

        GraphAssert.Edge(Graph, project, fixtureNs, RelationshipKind.Contains);
        GraphAssert.Edge(Graph, fixtureNs, libNs, RelationshipKind.Contains);
        GraphAssert.Edge(Graph, libNs, circle, RelationshipKind.Contains);
        GraphAssert.Edge(Graph, circle, circleArea, RelationshipKind.Contains);
        GraphAssert.Edge(Graph, circle, radius, RelationshipKind.Contains);
    }

    [Fact]
    public void RecordsFileContentHashes()
    {
        var shapes = _fixture.Snapshot.Files.SingleOrDefault(
            f => f.Path.EndsWith("Shapes.cs", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(shapes);
        Assert.Equal(64, shapes.ContentHash.Length);
        Assert.True(_fixture.Snapshot.Stats.DocumentsAnalyzed > 0);
    }
}
