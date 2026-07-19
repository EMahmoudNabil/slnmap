namespace Slnmap.Core.Graph;

/// <summary>
/// A directed edge in the code graph. Edges are value-equal: the same
/// (source, target, kind) triple is the same edge.
/// </summary>
public sealed record RelationshipEdge(string SourceId, string TargetId, RelationshipKind Kind);
