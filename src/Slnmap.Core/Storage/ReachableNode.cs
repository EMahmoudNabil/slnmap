using Slnmap.Core.Graph;

namespace Slnmap.Core.Storage;

/// <summary>
/// A node reached by a transitive traversal, tagged with the shortest hop distance
/// (<paramref name="Depth"/>, 1-based) from the start node.
/// </summary>
public sealed record ReachableNode(SymbolNode Node, int Depth);
