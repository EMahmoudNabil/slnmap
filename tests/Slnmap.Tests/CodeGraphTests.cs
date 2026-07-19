using Slnmap.Core.Graph;
using Xunit;

namespace Slnmap.Tests;

public sealed class CodeGraphTests
{
    private static SymbolNode Node(NodeKind kind, string fqn) =>
        SymbolNode.Create(kind, fqn[(fqn.LastIndexOf('.') + 1)..], fqn);

    [Fact]
    public void AddNode_DeduplicatesById_FirstNodeWins()
    {
        var graph = new CodeGraph();
        var original = Node(NodeKind.Class, "N.Widget");
        var duplicate = original with { FilePath = "src/Widget.cs" };

        Assert.True(graph.AddNode(original));
        Assert.False(graph.AddNode(duplicate));

        Assert.Equal(1, graph.NodeCount);
        Assert.True(graph.TryGetNode(original.Id, out var stored));
        Assert.Null(stored.FilePath);
    }

    [Fact]
    public void TryGetNode_ReturnsFalseForUnknownId()
    {
        var graph = new CodeGraph();

        Assert.False(graph.TryGetNode("missing", out _));
        Assert.False(graph.ContainsNode("missing"));
    }

    [Fact]
    public void AddEdge_DeduplicatesByValue()
    {
        var graph = new CodeGraph();

        Assert.True(graph.AddEdge(new RelationshipEdge("a", "b", RelationshipKind.Calls)));
        Assert.False(graph.AddEdge(new RelationshipEdge("a", "b", RelationshipKind.Calls)));
        Assert.True(graph.AddEdge(new RelationshipEdge("a", "b", RelationshipKind.References)));

        Assert.Equal(2, graph.EdgeCount);
    }

    [Fact]
    public void AddEdge_RejectsBlankEndpoints()
    {
        var graph = new CodeGraph();

        Assert.ThrowsAny<ArgumentException>(() => graph.AddEdge(new RelationshipEdge("", "b", RelationshipKind.Calls)));
        Assert.ThrowsAny<ArgumentException>(() => graph.AddEdge(new RelationshipEdge("a", " ", RelationshipKind.Calls)));
    }

    [Fact]
    public void OutgoingAndIncomingEdges_TrackDirection()
    {
        var graph = new CodeGraph();
        var callerToCallee = new RelationshipEdge("caller", "callee", RelationshipKind.Calls);
        var calleeToOther = new RelationshipEdge("callee", "other", RelationshipKind.References);
        graph.AddEdge(callerToCallee);
        graph.AddEdge(calleeToOther);

        Assert.Equal([callerToCallee], graph.OutgoingEdges("caller"));
        Assert.Equal([callerToCallee], graph.IncomingEdges("callee"));
        Assert.Equal([calleeToOther], graph.OutgoingEdges("callee"));
        Assert.Empty(graph.IncomingEdges("caller"));
    }

    [Fact]
    public void EdgeQueries_FilterByKind()
    {
        var graph = new CodeGraph();
        var implements = new RelationshipEdge("derived", "contract", RelationshipKind.Implements);
        graph.AddEdge(implements);
        graph.AddEdge(new RelationshipEdge("derived", "baseType", RelationshipKind.Inherits));

        Assert.Equal([implements], graph.OutgoingEdges("derived", RelationshipKind.Implements));
        Assert.Empty(graph.OutgoingEdges("derived", RelationshipKind.Calls));
    }

    [Fact]
    public void EdgeQueries_ReturnEmptyForUnknownNode()
    {
        var graph = new CodeGraph();

        Assert.Empty(graph.OutgoingEdges("nowhere"));
        Assert.Empty(graph.IncomingEdges("nowhere"));
    }

    [Fact]
    public void ContainmentChain_CanBeTraversed()
    {
        var graph = new CodeGraph();
        var ns = Node(NodeKind.Namespace, "Slnmap");
        var type = Node(NodeKind.Class, "Slnmap.Widget");
        var method = Node(NodeKind.Method, "Slnmap.Widget.Render()");
        graph.AddNode(ns);
        graph.AddNode(type);
        graph.AddNode(method);
        graph.AddEdge(new RelationshipEdge(ns.Id, type.Id, RelationshipKind.Contains));
        graph.AddEdge(new RelationshipEdge(type.Id, method.Id, RelationshipKind.Contains));

        var children = graph.OutgoingEdges(type.Id, RelationshipKind.Contains)
            .Select(e => graph.TryGetNode(e.TargetId, out var child) ? child : null)
            .ToList();

        Assert.Equal([method], children);
    }
}
