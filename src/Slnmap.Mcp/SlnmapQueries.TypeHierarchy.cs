using System.Text;
using Slnmap.Core.Graph;
using Slnmap.Core.Storage;

namespace Slnmap.Mcp;

public sealed partial class SlnmapQueries
{
    private const int HierarchyNodeCap = 200;
    private static readonly RelationshipKind[] HierarchyKinds = [RelationshipKind.Inherits, RelationshipKind.Implements];

    /// <summary>
    /// Renders the base and/or derived type tree for a type as an indented text tree (no box chars),
    /// transitive over Inherits+Implements, depth-capped. direction: up = base types, down = derived
    /// types/implementers, both = both.
    /// </summary>
    public async Task<string> GetTypeHierarchyAsync(string fqn, string direction, int depth, CancellationToken cancellationToken = default)
    {
        if (await NotAnalyzedAsync(cancellationToken).ConfigureAwait(false) is { } notReady)
        {
            return notReady;
        }

        string dir = (direction ?? "both").Trim().ToLowerInvariant();
        if (dir is not ("up" or "down" or "both"))
        {
            return "direction must be 'up' (base types), 'down' (derived types), or 'both'.";
        }

        int maxDepth = Math.Clamp(depth, 1, 10);
        var matches = await _store.GetNodesByFqnAsync(fqn, cancellationToken).ConfigureAwait(false);
        if (matches.Count == 0)
        {
            return await NotFoundAsync(fqn, cancellationToken).ConfigureAwait(false);
        }

        var node = matches[0];
        var builder = new StringBuilder();
        builder.AppendLine($"Type hierarchy: {node.Kind} {node.Fqn} ({dir}, depth<={maxDepth})");

        int emitted = 0;
        if (dir is "up" or "both")
        {
            if (dir == "both")
            {
                builder.AppendLine("Base types (up):");
            }

            builder.AppendLine(node.Name);
            emitted += await AppendSubtreeAsync(builder, node.Id, EdgeDirection.Outgoing, 1, maxDepth, new HashSet<string>(StringComparer.Ordinal) { node.Id }, emitted, cancellationToken).ConfigureAwait(false);
            if (emitted == 0 && dir == "up")
            {
                builder.AppendLine("  (no base types or interfaces)");
            }
        }

        if (dir is "down" or "both")
        {
            if (dir == "both")
            {
                builder.AppendLine("Derived types (down):");
            }

            int before = emitted;
            builder.AppendLine(node.Name);
            emitted += await AppendSubtreeAsync(builder, node.Id, EdgeDirection.Incoming, 1, maxDepth, new HashSet<string>(StringComparer.Ordinal) { node.Id }, emitted, cancellationToken).ConfigureAwait(false);
            if (emitted == before && dir == "down")
            {
                builder.AppendLine("  (no derived types or implementers)");
            }
        }

        if (emitted >= HierarchyNodeCap)
        {
            builder.AppendLine($"  ...capped at {HierarchyNodeCap} nodes — narrow the depth or direction.");
        }

        return builder.ToString().TrimEnd();
    }

    private async Task<int> AppendSubtreeAsync(
        StringBuilder builder,
        string nodeId,
        EdgeDirection direction,
        int depth,
        int maxDepth,
        HashSet<string> visited,
        int emitted,
        CancellationToken cancellationToken)
    {
        if (depth > maxDepth || emitted >= HierarchyNodeCap)
        {
            return emitted;
        }

        var neighborIds = new List<string>();
        foreach (var kind in HierarchyKinds)
        {
            foreach (var edge in await _store.GetEdgesAsync(nodeId, direction, kind, cancellationToken).ConfigureAwait(false))
            {
                neighborIds.Add(direction == EdgeDirection.Outgoing ? edge.TargetId : edge.SourceId);
            }
        }

        var neighbors = (await _store.GetNodesByIdsAsync(neighborIds.Distinct(StringComparer.Ordinal), cancellationToken).ConfigureAwait(false))
            .OrderBy(n => n.Fqn, StringComparer.Ordinal);

        string indent = new string(' ', depth * 2);
        foreach (var neighbor in neighbors)
        {
            if (emitted >= HierarchyNodeCap)
            {
                break;
            }

            bool alreadySeen = !visited.Add(neighbor.Id);
            string file = neighbor.FilePath is { } f ? $" — {f}" : string.Empty;
            builder.AppendLine($"{indent}{neighbor.Name}{(alreadySeen ? " (already shown)" : file)}");
            emitted++;

            if (!alreadySeen)
            {
                emitted = await AppendSubtreeAsync(builder, neighbor.Id, direction, depth + 1, maxDepth, visited, emitted, cancellationToken).ConfigureAwait(false);
            }
        }

        return emitted;
    }
}
