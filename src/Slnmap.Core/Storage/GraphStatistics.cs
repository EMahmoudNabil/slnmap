using Slnmap.Core.Graph;

namespace Slnmap.Core.Storage;

/// <summary>
/// Counts-first summary of a stored graph: totals plus per-kind breakdowns and the
/// project list. Shaped for the CLI <c>status</c> command and the architecture-overview tool.
/// </summary>
public sealed record GraphStatistics(
    int NodeCount,
    int EdgeCount,
    IReadOnlyDictionary<NodeKind, int> NodesByKind,
    IReadOnlyDictionary<RelationshipKind, int> EdgesByKind,
    IReadOnlyList<string> Projects);
