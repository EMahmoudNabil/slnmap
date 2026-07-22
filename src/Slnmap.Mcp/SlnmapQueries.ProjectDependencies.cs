using System.Text;
using Slnmap.Core.Graph;

namespace Slnmap.Mcp;

public sealed partial class SlnmapQueries
{
    private const int ProjectDepPairCap = 40;

    /// <summary>
    /// Project-to-project reference map with cross-project symbol-reference counts, derived (not stored)
    /// the same way as the architecture overview: endpoints attributed to projects by file path. Pass a
    /// project name to focus on its inbound/outbound edges, or "all" (default) for the full map. Ends
    /// with a single hotspot line: the pair with the most cross-project references.
    /// </summary>
    public async Task<string> GetProjectDependenciesAsync(string? project, CancellationToken cancellationToken = default)
    {
        if (await NotAnalyzedAsync(cancellationToken).ConfigureAwait(false) is { } notReady)
        {
            return notReady;
        }

        var graph = await _store.LoadGraphAsync(cancellationToken).ConfigureAwait(false);
        var projectNodes = graph.Nodes.Where(n => n.Kind == NodeKind.Project).ToList();
        var attributor = ProjectAttributor.From(projectNodes);
        var fileById = graph.Nodes.ToDictionary(n => n.Id, n => n.FilePath, StringComparer.Ordinal);

        var dependencies = new Dictionary<(string From, string To), int>();
        foreach (var edge in graph.Edges)
        {
            if (edge.Kind == RelationshipKind.Contains)
            {
                continue;
            }

            string? from = attributor.ProjectOf(fileById.GetValueOrDefault(edge.SourceId));
            string? to = attributor.ProjectOf(fileById.GetValueOrDefault(edge.TargetId));
            if (from is not null && to is not null && !string.Equals(from, to, StringComparison.Ordinal))
            {
                dependencies[(from, to)] = dependencies.GetValueOrDefault((from, to)) + 1;
            }
        }

        bool scoped = !string.IsNullOrWhiteSpace(project) && !string.Equals(project, "all", StringComparison.OrdinalIgnoreCase);
        if (scoped && !projectNodes.Any(p => string.Equals(p.Name, project, StringComparison.OrdinalIgnoreCase)))
        {
            return $"Unknown project '{project}'. Valid projects: {string.Join(", ", projectNodes.Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal))}.";
        }

        var relevant = scoped
            ? dependencies.Where(kv => string.Equals(kv.Key.From, project, StringComparison.OrdinalIgnoreCase)
                || string.Equals(kv.Key.To, project, StringComparison.OrdinalIgnoreCase)).ToList()
            : dependencies.ToList();

        var ranked = relevant.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.From, StringComparer.Ordinal).ToList();
        var builder = new StringBuilder();
        string scopeLabel = scoped ? $"for project '{project}'" : $"across {projectNodes.Count} project(s)";
        builder.AppendLine($"{ranked.Count} cross-project dependency pair(s) {scopeLabel} (derived from References + Calls):");

        if (ranked.Count == 0)
        {
            builder.AppendLine(scoped
                ? "  isolated: no cross-project references to or from this project."
                : "  (no cross-project references)");
            return builder.ToString().TrimEnd();
        }

        foreach (var (pair, count) in ranked.Take(ProjectDepPairCap))
        {
            builder.AppendLine($"  {pair.From} -> {pair.To} ({count})");
        }

        if (ranked.Count > ProjectDepPairCap)
        {
            builder.AppendLine($"  {ProjectDepPairCap}+ pairs, showing first {ProjectDepPairCap} — narrow with a project name.");
        }

        var hotspot = ranked[0];
        builder.AppendLine($"Hotspot: {hotspot.Key.From} -> {hotspot.Key.To} ({hotspot.Value} cross-project references)");
        return builder.ToString().TrimEnd();
    }
}
