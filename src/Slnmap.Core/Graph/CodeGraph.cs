using System.Diagnostics.CodeAnalysis;

namespace Slnmap.Core.Graph;

/// <summary>
/// In-memory code graph: symbols and the relationships between them.
/// Nodes are keyed by id; edges are deduplicated by value. Not thread-safe.
/// </summary>
public sealed class CodeGraph
{
    private readonly Dictionary<string, SymbolNode> _nodes = new(StringComparer.Ordinal);
    private readonly HashSet<RelationshipEdge> _edges = [];
    private readonly Dictionary<string, List<RelationshipEdge>> _outgoing = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<RelationshipEdge>> _incoming = new(StringComparer.Ordinal);

    public int NodeCount => _nodes.Count;

    public int EdgeCount => _edges.Count;

    public IEnumerable<SymbolNode> Nodes => _nodes.Values;

    public IEnumerable<RelationshipEdge> Edges => _edges;

    /// <summary>Adds a node. Returns false if a node with the same id is already present (the existing node wins).</summary>
    public bool AddNode(SymbolNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        return _nodes.TryAdd(node.Id, node);
    }

    public bool ContainsNode(string id) => _nodes.ContainsKey(id);

    public bool TryGetNode(string id, [NotNullWhen(true)] out SymbolNode? node) =>
        _nodes.TryGetValue(id, out node);

    /// <summary>
    /// Adds an edge. Returns false for an exact duplicate. Endpoints are not required
    /// to exist yet — analysis may discover a relationship before the target's declaration.
    /// </summary>
    public bool AddEdge(RelationshipEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        ArgumentException.ThrowIfNullOrWhiteSpace(edge.SourceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(edge.TargetId);

        if (!_edges.Add(edge))
        {
            return false;
        }

        GetOrAddList(_outgoing, edge.SourceId).Add(edge);
        GetOrAddList(_incoming, edge.TargetId).Add(edge);
        return true;
    }

    /// <summary>Edges whose source is <paramref name="nodeId"/>, optionally filtered by kind.</summary>
    public IEnumerable<RelationshipEdge> OutgoingEdges(string nodeId, RelationshipKind? kind = null) =>
        FilterEdges(_outgoing, nodeId, kind);

    /// <summary>Edges whose target is <paramref name="nodeId"/>, optionally filtered by kind.</summary>
    public IEnumerable<RelationshipEdge> IncomingEdges(string nodeId, RelationshipKind? kind = null) =>
        FilterEdges(_incoming, nodeId, kind);

    private static List<RelationshipEdge> GetOrAddList(Dictionary<string, List<RelationshipEdge>> index, string key)
    {
        if (!index.TryGetValue(key, out var list))
        {
            list = [];
            index.Add(key, list);
        }

        return list;
    }

    private static IEnumerable<RelationshipEdge> FilterEdges(
        Dictionary<string, List<RelationshipEdge>> index,
        string nodeId,
        RelationshipKind? kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        if (!index.TryGetValue(nodeId, out var edges))
        {
            return [];
        }

        return kind is null ? edges : edges.Where(e => e.Kind == kind);
    }
}
