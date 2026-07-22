using System.Text;
using Slnmap.Core.Graph;
using Slnmap.Core.Storage;

namespace Slnmap.Mcp;

/// <summary>
/// Shared rendering helpers for the relationship/impact tools added in v0.3.0. Grouping symbols by
/// their owning project (by file path) and emitting file:line is the house layout several tools reuse.
/// </summary>
public sealed partial class SlnmapQueries
{
    /// <summary>
    /// Appends symbols grouped by owning project, each as <c>[Kind] Fqn — file:line</c>, honestly
    /// capped: at most <paramref name="cap"/> lines, with a "N+ … refine" note when more exist. The
    /// caller writes the counts-first header; this renders the detail block beneath it.
    /// </summary>
    private async Task AppendProjectGroupedAsync(
        StringBuilder builder,
        IReadOnlyCollection<SymbolNode> nodes,
        int cap,
        CancellationToken cancellationToken)
    {
        var attributor = ProjectAttributor.From(
            await _store.GetNodesByKindAsync(NodeKind.Project, cancellationToken).ConfigureAwait(false));
        var resolver = new LineResolver();

        var ordered = nodes
            .OrderBy(n => attributor.ProjectOf(n.FilePath) ?? "~unknown", StringComparer.Ordinal)
            .ThenBy(n => n.Fqn, StringComparer.Ordinal)
            .Take(cap)
            .ToList();

        foreach (var group in ordered.GroupBy(n => attributor.ProjectOf(n.FilePath) ?? "(unknown project)"))
        {
            builder.AppendLine($"{group.Key}:");
            foreach (var node in group)
            {
                builder.AppendLine($"  [{node.Kind}] {node.Fqn}{LocationSuffix(node, resolver)}");
            }
        }

        if (nodes.Count > cap)
        {
            builder.AppendLine($"  {cap}+ results, showing first {cap} — refine your query.");
        }
    }

    private static string LocationSuffix(SymbolNode node, LineResolver resolver) =>
        node.FilePath is { } file
            ? $" — {file}:{resolver.LineOf(file, node.Span?.Start ?? 0)}"
            : " — (no source location)";
}
