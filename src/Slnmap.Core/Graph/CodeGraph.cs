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
    private readonly HashSet<ExternalCall> _externalCalls = [];
    private readonly HashSet<DiRegistration> _diRegistrations = [];
    private readonly HashSet<AttributeUsage> _attributeUsages = [];
    private readonly HashSet<Disclosure> _disclosures = [];

    public int NodeCount => _nodes.Count;

    public int EdgeCount => _edges.Count;

    public IEnumerable<SymbolNode> Nodes => _nodes.Values;

    public IEnumerable<RelationshipEdge> Edges => _edges;

    /// <summary>Calls from source members into symbols outside the solution (schema v2).</summary>
    public IEnumerable<ExternalCall> ExternalCalls => _externalCalls;

    /// <summary>Dependency-injection registration call sites (schema v2).</summary>
    public IEnumerable<DiRegistration> DiRegistrations => _diRegistrations;

    /// <summary>Attributes applied to source symbols (schema v2).</summary>
    public IEnumerable<AttributeUsage> AttributeUsages => _attributeUsages;

    /// <summary>Things analysis found but could not model, by location (v0.14.0).</summary>
    public IEnumerable<Disclosure> Disclosures => _disclosures;

    /// <summary>Total number of file-owned facts across all fact kinds.</summary>
    public int FactCount => _externalCalls.Count + _diRegistrations.Count + _attributeUsages.Count + _disclosures.Count;

    public bool AddDisclosure(Disclosure disclosure)
    {
        ArgumentNullException.ThrowIfNull(disclosure);
        return _disclosures.Add(disclosure);
    }

    /// <summary>Removes every disclosure of <paramref name="kind"/>; returns how many were removed.</summary>
    public int RemoveDisclosures(string kind) => _disclosures.RemoveWhere(d => d.Kind == kind);

    public bool AddExternalCall(ExternalCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return _externalCalls.Add(call);
    }

    public bool AddDiRegistration(DiRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        return _diRegistrations.Add(registration);
    }

    public bool AddAttributeUsage(AttributeUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return _attributeUsages.Add(usage);
    }

    /// <summary>
    /// Copies every file-owned fact from <paramref name="source"/> into this graph, keeping only
    /// facts whose owning file satisfies <paramref name="keepFile"/> (all of them when null).
    /// Every place that rebuilds a graph from an existing one must call this — a rebuild that
    /// copies nodes and edges alone silently drops the facts.
    /// </summary>
    public void CopyFactsFrom(CodeGraph source, Func<string, bool>? keepFile = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        foreach (var call in source._externalCalls)
        {
            if (keepFile is null || keepFile(call.FilePath))
            {
                _externalCalls.Add(call);
            }
        }

        foreach (var registration in source._diRegistrations)
        {
            if (keepFile is null || keepFile(registration.FilePath))
            {
                _diRegistrations.Add(registration);
            }
        }

        foreach (var usage in source._attributeUsages)
        {
            if (keepFile is null || keepFile(usage.FilePath))
            {
                _attributeUsages.Add(usage);
            }
        }

        foreach (var disclosure in source._disclosures)
        {
            if (keepFile is null || keepFile(disclosure.FilePath))
            {
                _disclosures.Add(disclosure);
            }
        }
    }

    /// <summary>Set equality over all fact kinds.</summary>
    public bool FactsEqual(CodeGraph other)
    {
        ArgumentNullException.ThrowIfNull(other);
        return _externalCalls.SetEquals(other._externalCalls)
            && _diRegistrations.SetEquals(other._diRegistrations)
            && _attributeUsages.SetEquals(other._attributeUsages)
            && _disclosures.SetEquals(other._disclosures);
    }

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
