using System.Text;
using Slnmap.Core.Graph;
using Slnmap.Core.Storage;

namespace Slnmap.Mcp;

/// <summary>
/// The read-only query operations behind the MCP tools, returning compact, capped, counts-first text.
/// Kept separate from the tool attributes so it can be unit-tested directly against a graph store.
/// Inputs are fully qualified names (never node ids); unresolved FQNs yield "did you mean" suggestions.
/// </summary>
public sealed partial class SlnmapQueries
{
    private const int FindLimit = 20;
    private const int DependencyCap = 50;
    private const int ImpactListCap = 100;
    private const int ImpactTraversalCap = 10_000;
    private const int UsageCap = 50;
    private const int SuggestionCap = 5;
    private const int NamespaceListCap = 15;
    private const int ProjectDependencyCap = 40;

    // HandledBy: an endpoint "uses" its handler, so find_usages(handler) surfaces its endpoints.
    private static readonly RelationshipKind[] UsageKinds = [RelationshipKind.Calls, RelationshipKind.References, RelationshipKind.HandledBy];

    private readonly IGraphStore _store;

    public SlnmapQueries(IGraphStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    public async Task<string> FindSymbolAsync(string query, string? kind, CancellationToken cancellationToken = default)
    {
        if (await NotAnalyzedAsync(cancellationToken).ConfigureAwait(false) is { } notReady)
        {
            return notReady;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return "Provide a search string (matched against symbol name and fully qualified name).";
        }

        NodeKind? kindFilter = null;
        if (!string.IsNullOrWhiteSpace(kind))
        {
            if (!Enum.TryParse(kind, ignoreCase: true, out NodeKind parsed))
            {
                return $"Unknown kind '{kind}'. Valid kinds: {string.Join(", ", Enum.GetNames<NodeKind>())}.";
            }

            kindFilter = parsed;
        }

        // Fetch one past the cap so we can distinguish an exact count from "more exist" and say so,
        // rather than reporting the cap as if it were the total.
        List<SymbolNode> matches;
        if (kindFilter is null)
        {
            matches = (await _store.FindNodesAsync($"%{query}%", FindLimit + 1, cancellationToken).ConfigureAwait(false)).ToList();
        }
        else
        {
            // Filter by kind at the source (not after a capped page), so a common substring paired
            // with a rare kind can't push valid matches past the limit.
            var ofKind = await _store.GetNodesByKindAsync(kindFilter.Value, cancellationToken).ConfigureAwait(false);
            matches = ofKind
                .Where(n => n.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || n.Fqn.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderBy(n => n.Fqn.Length)
                .ThenBy(n => n.Fqn, StringComparer.Ordinal)
                .Take(FindLimit + 1)
                .ToList();
        }

        if (matches.Count == 0)
        {
            string kindNote = kindFilter is null ? string.Empty : $" of kind {kindFilter}";
            return $"No symbols match '{query}'{kindNote}.";
        }

        bool capped = matches.Count > FindLimit;
        var shown = capped ? matches.Take(FindLimit).ToList() : matches;

        var builder = new StringBuilder();
        builder.AppendLine(capped
            ? $"{FindLimit}+ matches for '{query}' (showing first {FindLimit} — refine your query):"
            : $"{shown.Count} match(es) for '{query}':");
        foreach (var node in shown)
        {
            builder.AppendLine(FormatNodeLine(node));
        }

        return builder.ToString().TrimEnd();
    }

    public async Task<string> GetDependenciesAsync(string fqn, string direction, int depth, CancellationToken cancellationToken = default)
    {
        if (await NotAnalyzedAsync(cancellationToken).ConfigureAwait(false) is { } notReady)
        {
            return notReady;
        }

        if (!TryParseDirection(direction, out var edgeDirection))
        {
            return "direction must be 'incoming' (who depends on this) or 'outgoing' (what this depends on).";
        }

        depth = Math.Clamp(depth, 1, 3);
        var (node, resolution) = await ResolveOneAsync(fqn, cancellationToken).ConfigureAwait(false);
        if (node is null)
        {
            return resolution;
        }

        var directEdges = (await _store.GetEdgesAsync(node.Id, edgeDirection, null, cancellationToken).ConfigureAwait(false))
            .Where(e => e.Kind != RelationshipKind.Contains)
            .ToList();
        var neighborIds = directEdges
            .Select(e => edgeDirection == EdgeDirection.Outgoing ? e.TargetId : e.SourceId)
            .Distinct(StringComparer.Ordinal);
        var nodesById = (await _store.GetNodesByIdsAsync(neighborIds, cancellationToken).ConfigureAwait(false))
            .ToDictionary(n => n.Id, StringComparer.Ordinal);

        string directionWord = edgeDirection == EdgeDirection.Outgoing ? "outgoing (depends on)" : "incoming (depended on by)";
        var builder = new StringBuilder();
        builder.AppendLine($"{resolution}Dependencies of {node.Kind} {node.Fqn} — {directionWord}, depth {depth}:");

        int listed = 0;
        bool truncated = false;
        foreach (var group in directEdges.GroupBy(e => e.Kind).OrderBy(g => g.Key.ToString(), StringComparer.Ordinal))
        {
            builder.AppendLine($"  {group.Key} ({group.Count()}):");
            foreach (var edge in group)
            {
                if (listed >= DependencyCap)
                {
                    truncated = true;
                    break;
                }

                string id = edgeDirection == EdgeDirection.Outgoing ? edge.TargetId : edge.SourceId;
                string label = nodesById.TryGetValue(id, out var n) ? $"[{n.Kind}] {n.Fqn}" : id;
                builder.AppendLine($"    {label}");
                listed++;
            }

            if (truncated)
            {
                break;
            }
        }

        if (directEdges.Count == 0)
        {
            // For outgoing, an empty result usually means the only calls are into external packages,
            // which are not tracked — say so rather than a bare "(none)" that reads as a miss. For
            // incoming, it simply means nothing in the solution depends on this symbol.
            builder.AppendLine(edgeDirection == EdgeDirection.Outgoing
                ? "  No dependencies within this solution. Calls into external packages aren't tracked."
                : "  Nothing else in this solution depends on this symbol.");
        }

        if (depth > 1)
        {
            var transitive = (await _store.TraverseAsync(node.Id, edgeDirection, depth, 200, cancellationToken).ConfigureAwait(false))
                .Where(r => r.Depth >= 2)
                .ToList();
            if (transitive.Count > 0)
            {
                builder.AppendLine($"  Transitive (depth 2-{depth}): {transitive.Count}");
                foreach (var reached in transitive)
                {
                    if (listed >= DependencyCap)
                    {
                        truncated = true;
                        break;
                    }

                    builder.AppendLine($"    [{reached.Node.Kind}] {reached.Node.Fqn} @depth {reached.Depth}");
                    listed++;
                }
            }
        }

        if (truncated)
        {
            builder.AppendLine($"  ...truncated at {DependencyCap}.");
        }

        return builder.ToString().TrimEnd();
    }

    public async Task<string> ImpactAnalysisAsync(string fqn, CancellationToken cancellationToken = default)
    {
        if (await NotAnalyzedAsync(cancellationToken).ConfigureAwait(false) is { } notReady)
        {
            return notReady;
        }

        var matches = await _store.GetNodesByFqnAsync(fqn, cancellationToken).ConfigureAwait(false);
        if (matches.Count == 0)
        {
            return await NotFoundAsync(fqn, cancellationToken).ConfigureAwait(false);
        }

        // Seeds are the nodes we traverse dependents from. `changing` is the API surface actually
        // being modified (it is the change, not something impacted by it) and is removed at the end.
        var seeds = matches.ToDictionary(n => n.Id, n => n, StringComparer.Ordinal);
        var changing = new HashSet<string>(matches.Select(m => m.Id), StringComparer.Ordinal);
        var implementers = new List<SymbolNode>();
        foreach (var target in matches)
        {
            if (target.Kind == NodeKind.Interface)
            {
                await ExpandInterfaceTypeAsync(target, seeds, changing, implementers, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // An interface member: its concrete implementations/overrides also break on a change.
                await ExpandInterfaceMembersAsync(target, seeds, implementers, cancellationToken).ConfigureAwait(false);
            }
        }

        var impacted = new Dictionary<string, ReachableNode>(StringComparer.Ordinal);
        bool capped = false;
        foreach (var seedId in seeds.Keys.ToList())
        {
            var reached = await _store.TraverseAsync(seedId, EdgeDirection.Incoming, 5, ImpactTraversalCap, cancellationToken).ConfigureAwait(false);
            if (reached.Count >= ImpactTraversalCap)
            {
                capped = true; // a single seed saturated the traversal — totals below are a lower bound.
            }

            foreach (var node in reached)
            {
                if (!impacted.TryGetValue(node.Node.Id, out var existing) || node.Depth < existing.Depth)
                {
                    impacted[node.Node.Id] = node;
                }
            }
        }

        // The implementing members themselves are impacted (they must change with the interface).
        foreach (var member in implementers)
        {
            impacted.TryAdd(member.Id, new ReachableNode(member, 1));
        }

        // The symbols under change (the interface and its own members) are not their own dependents.
        foreach (var id in changing)
        {
            impacted.Remove(id);
        }

        string targetLabel = matches.Count == 1
            ? $"{matches[0].Kind} {matches[0].Fqn}"
            : $"{matches.Count} symbols named {fqn}";
        if (impacted.Count == 0)
        {
            return $"Impact of changing {targetLabel}: nothing else in the graph depends on it.";
        }

        var all = impacted.Values.OrderBy(r => r.Depth).ThenBy(r => r.Node.Fqn, StringComparer.Ordinal).ToList();
        var attributor = ProjectAttributor.From(await _store.GetNodesByKindAsync(NodeKind.Project, cancellationToken).ConfigureAwait(false));

        var builder = new StringBuilder();
        string countSuffix = capped ? "+ (traversal capped; totals are a lower bound)" : string.Empty;
        builder.AppendLine($"Impact of changing {targetLabel}: {all.Count}{countSuffix} dependent symbol(s).");
        if (implementers.Count > 0)
        {
            builder.AppendLine($"Includes {implementers.Count} interface implementation(s)/override(s).");
        }

        builder.AppendLine("By project:");
        foreach (var group in all
            .GroupBy(r => attributor.ProjectOf(r.Node.FilePath) ?? "(unknown)")
            .OrderByDescending(g => g.Count()))
        {
            builder.AppendLine($"  {group.Key}: {group.Count()}");
        }

        builder.AppendLine("By kind:");
        foreach (var group in all.GroupBy(r => r.Node.Kind).OrderByDescending(g => g.Count()))
        {
            builder.AppendLine($"  {group.Key}: {group.Count()}");
        }

        builder.AppendLine($"Dependents (nearest first, up to {ImpactListCap}):");
        foreach (var reached in all.Take(ImpactListCap))
        {
            builder.AppendLine($"  [{reached.Node.Kind}] {reached.Node.Fqn} @depth {reached.Depth}");
        }

        if (all.Count > ImpactListCap)
        {
            builder.AppendLine($"  ...and {all.Count - ImpactListCap} more.");
        }

        return builder.ToString().TrimEnd();
    }

    public async Task<string> GetArchitectureOverviewAsync(CancellationToken cancellationToken = default)
    {
        if (await NotAnalyzedAsync(cancellationToken).ConfigureAwait(false) is { } notReady)
        {
            return notReady;
        }

        var meta = await _store.GetMetaAsync(cancellationToken).ConfigureAwait(false);

        // A one-shot full load (this tool is called rarely, not on the hot path). It lets project
        // membership be attributed by file path — reliable even when projects share namespaces, which
        // a Contains-walk cannot resolve. Steady-state memory is unaffected: the graph is not retained.
        var graph = await _store.LoadGraphAsync(cancellationToken).ConfigureAwait(false);
        var attributor = ProjectAttributor.From(graph.Nodes.Where(n => n.Kind == NodeKind.Project));
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

        var nodesByKind = graph.Nodes.GroupBy(n => n.Kind).ToDictionary(g => g.Key, g => g.Count());
        var edgesByKind = graph.Edges.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count());
        var projects = graph.Nodes.Where(n => n.Kind == NodeKind.Project).Select(n => n.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var namespaces = graph.Nodes
            .Where(n => n.Kind == NodeKind.Namespace)
            .OrderBy(n => n.Fqn.Length)
            .ThenBy(n => n.Fqn, StringComparer.Ordinal)
            .Take(NamespaceListCap)
            .ToList();

        var builder = new StringBuilder();
        builder.AppendLine("Architecture overview");
        if (meta.TryGetValue(MetaKeys.SolutionPath, out var solutionPath))
        {
            builder.AppendLine($"Solution: {solutionPath}");
        }

        builder.AppendLine($"Totals: {graph.NodeCount} nodes, {graph.EdgeCount} edges across {projects.Count} project(s).");
        builder.AppendLine($"Projects ({projects.Count}): {string.Join(", ", projects)}");

        builder.AppendLine($"Project dependencies (derived from symbol references, up to {ProjectDependencyCap}):");
        var rankedDependencies = dependencies.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key.From, StringComparer.Ordinal).ToList();
        if (rankedDependencies.Count == 0)
        {
            builder.AppendLine("  (none)");
        }
        else
        {
            foreach (var (pair, count) in rankedDependencies.Take(ProjectDependencyCap))
            {
                builder.AppendLine($"  {pair.From} -> {pair.To} ({count})");
            }
        }

        builder.AppendLine("Node kinds:");
        foreach (var (kind, count) in nodesByKind.OrderByDescending(kv => kv.Value))
        {
            builder.AppendLine($"  {kind}: {count}");
        }

        builder.AppendLine("Edge kinds:");
        foreach (var (kind, count) in edgesByKind.OrderByDescending(kv => kv.Value))
        {
            builder.AppendLine($"  {kind}: {count}");
        }

        builder.AppendLine($"Namespaces ({nodesByKind.GetValueOrDefault(NodeKind.Namespace)}, top-level first, up to {NamespaceListCap}):");
        foreach (var ns in namespaces)
        {
            builder.AppendLine($"  {ns.Fqn}");
        }

        return builder.ToString().TrimEnd();
    }

    public async Task<string> FindUsagesAsync(string fqn, CancellationToken cancellationToken = default)
    {
        if (await NotAnalyzedAsync(cancellationToken).ConfigureAwait(false) is { } notReady)
        {
            return notReady;
        }

        var matches = await _store.GetNodesByFqnAsync(fqn, cancellationToken).ConfigureAwait(false);
        if (matches.Count == 0)
        {
            return await NotFoundAsync(fqn, cancellationToken).ConfigureAwait(false);
        }

        var userIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var target in matches)
        {
            foreach (var kind in UsageKinds)
            {
                foreach (var edge in await _store.GetEdgesAsync(target.Id, EdgeDirection.Incoming, kind, cancellationToken).ConfigureAwait(false))
                {
                    userIds.Add(edge.SourceId);
                }
            }
        }

        if (userIds.Count == 0)
        {
            return $"No usages found for {matches[0].Fqn}.";
        }

        var users = await _store.GetNodesByIdsAsync(userIds, cancellationToken).ConfigureAwait(false);
        var ordered = users
            .OrderBy(n => n.FilePath ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(n => n.Fqn, StringComparer.Ordinal)
            .ToList();

        var lineResolver = new LineResolver();
        var builder = new StringBuilder();
        builder.AppendLine($"{ordered.Count} usage(s) of {matches[0].Fqn} (by containing member, up to {UsageCap}):");
        foreach (var user in ordered.Take(UsageCap))
        {
            string location = user.FilePath is { } file
                ? $"{file}:{lineResolver.LineOf(file, user.Span?.Start ?? 0)}"
                : "(no source location)";
            builder.AppendLine($"  [{user.Kind}] {user.Fqn} — {location}");
        }

        if (ordered.Count > UsageCap)
        {
            builder.AppendLine($"  ...and {ordered.Count - UsageCap} more.");
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Expands an interface type for impact analysis: its own members are seeded (so their callers —
    /// often in other files — are counted) and marked as part of the change; the implementing/derived
    /// types and the matching members on them are added as impacted implementers.
    /// </summary>
    private async Task ExpandInterfaceTypeAsync(
        SymbolNode iface,
        Dictionary<string, SymbolNode> seeds,
        HashSet<string> changing,
        List<SymbolNode> implementers,
        CancellationToken cancellationToken)
    {
        var memberEdges = await _store.GetEdgesAsync(iface.Id, EdgeDirection.Outgoing, RelationshipKind.Contains, cancellationToken).ConfigureAwait(false);
        var members = await _store.GetNodesByIdsAsync(memberEdges.Select(e => e.TargetId), cancellationToken).ConfigureAwait(false);
        foreach (var member in members)
        {
            seeds[member.Id] = member;
            changing.Add(member.Id);
            await ExpandInterfaceMembersAsync(member, seeds, implementers, cancellationToken).ConfigureAwait(false);
        }

        foreach (var implementerType in await ClosureAsync(iface.Id, [RelationshipKind.Implements, RelationshipKind.Inherits], cancellationToken).ConfigureAwait(false))
        {
            if (seeds.TryAdd(implementerType.Id, implementerType))
            {
                implementers.Add(implementerType);
            }
        }
    }

    /// <summary>
    /// If <paramref name="target"/> is a member declared on an interface, finds the matching member on
    /// every implementing/derived type and adds it to <paramref name="seeds"/> and <paramref name="implementers"/>.
    /// </summary>
    private async Task ExpandInterfaceMembersAsync(
        SymbolNode target,
        Dictionary<string, SymbolNode> seeds,
        List<SymbolNode> implementers,
        CancellationToken cancellationToken)
    {
        if (target.Kind is not (NodeKind.Method or NodeKind.Property or NodeKind.Event or NodeKind.Field))
        {
            return;
        }

        var containerEdges = await _store.GetEdgesAsync(target.Id, EdgeDirection.Incoming, RelationshipKind.Contains, cancellationToken).ConfigureAwait(false);
        var containers = await _store.GetNodesByIdsAsync(containerEdges.Select(e => e.SourceId), cancellationToken).ConfigureAwait(false);
        var iface = containers.FirstOrDefault(c => c.Kind == NodeKind.Interface);
        if (iface is null || !target.Fqn.StartsWith(iface.Fqn, StringComparison.Ordinal))
        {
            return;
        }

        string memberSuffix = target.Fqn[iface.Fqn.Length..]; // e.g. ".Area()"
        foreach (var implementerType in await ClosureAsync(iface.Id, [RelationshipKind.Implements, RelationshipKind.Inherits], cancellationToken).ConfigureAwait(false))
        {
            var candidates = await _store.GetNodesByFqnAsync(implementerType.Fqn + memberSuffix, cancellationToken).ConfigureAwait(false);
            foreach (var member in candidates.Where(m => m.Kind == target.Kind))
            {
                if (seeds.TryAdd(member.Id, member))
                {
                    implementers.Add(member);
                }
            }
        }
    }

    /// <summary>Types reachable by following the given incoming edge kinds from <paramref name="startTypeId"/> (transitive).</summary>
    private async Task<IReadOnlyList<SymbolNode>> ClosureAsync(string startTypeId, RelationshipKind[] kinds, CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(startTypeId);
        while (queue.Count > 0)
        {
            string current = queue.Dequeue();
            foreach (var kind in kinds)
            {
                foreach (var edge in await _store.GetEdgesAsync(current, EdgeDirection.Incoming, kind, cancellationToken).ConfigureAwait(false))
                {
                    if (seen.Add(edge.SourceId))
                    {
                        queue.Enqueue(edge.SourceId);
                    }
                }
            }
        }

        return await _store.GetNodesByIdsAsync(seen, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(SymbolNode? Node, string Resolution)> ResolveOneAsync(string fqn, CancellationToken cancellationToken)
    {
        var matches = await _store.GetNodesByFqnAsync(fqn, cancellationToken).ConfigureAwait(false);
        if (matches.Count == 0)
        {
            return (null, await NotFoundAsync(fqn, cancellationToken).ConfigureAwait(false));
        }

        // A single FQN can map to several nodes (e.g. an explicit interface implementation shares the
        // Type.Member form with an ordinary member). Note it and use the first deterministically.
        string note = matches.Count == 1
            ? string.Empty
            : $"note: {matches.Count} symbols share this FQN ({string.Join(", ", matches.Select(m => m.Kind))}); using the first.\n";
        return (matches[0], note);
    }

    private async Task<string> NotFoundAsync(string fqn, CancellationToken cancellationToken)
    {
        string seed = LastSegment(fqn);
        var near = string.IsNullOrEmpty(seed)
            ? []
            : await _store.FindNodesAsync($"%{seed}%", SuggestionCap, cancellationToken).ConfigureAwait(false);
        if (near.Count == 0)
        {
            return $"No symbol with FQN '{fqn}'. Use find_symbol to search by name.";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"No symbol with FQN '{fqn}'. Did you mean:");
        foreach (var node in near)
        {
            builder.AppendLine(FormatNodeLine(node));
        }

        return builder.ToString().TrimEnd();
    }

    private async Task<string?> NotAnalyzedAsync(CancellationToken cancellationToken)
    {
        var meta = await _store.GetMetaAsync(cancellationToken).ConfigureAwait(false);
        return meta.ContainsKey(MetaKeys.LastAnalyzed)
            ? null
            : "The code graph has not been built yet — run 'slnmap analyze <solution>' first.";
    }

    private static string FormatNodeLine(SymbolNode node)
    {
        string location = node.FilePath is { } file ? $" — {file}" : string.Empty;
        return $"  [{node.Kind}] {node.Fqn}{location}";
    }

    private static bool TryParseDirection(string direction, out EdgeDirection edgeDirection)
    {
        switch (direction?.Trim().ToLowerInvariant())
        {
            case "incoming" or "in" or "dependents":
                edgeDirection = EdgeDirection.Incoming;
                return true;
            case "outgoing" or "out" or "dependencies":
                edgeDirection = EdgeDirection.Outgoing;
                return true;
            default:
                edgeDirection = default;
                return false;
        }
    }

    private static string LastSegment(string fqn)
    {
        int paren = fqn.IndexOf('(', StringComparison.Ordinal);
        string head = paren >= 0 ? fqn[..paren] : fqn;
        int dot = head.LastIndexOf('.');
        return dot >= 0 ? head[(dot + 1)..] : head;
    }

    /// <summary>
    /// Attributes a symbol to its owning project by file path: a file lives under its project's
    /// directory. Longest-matching directory wins (handles nested projects). Reliable even when
    /// several projects share a namespace, which a containment walk cannot disambiguate.
    /// </summary>
    private sealed class ProjectAttributor
    {
        private readonly IReadOnlyList<(string Directory, string Name)> _projects;

        private ProjectAttributor(IReadOnlyList<(string, string)> projects) => _projects = projects;

        public static ProjectAttributor From(IEnumerable<SymbolNode> projectNodes)
        {
            var projects = projectNodes
                .Where(p => p.FilePath is not null)
                .Select(p => (Directory: NormalizeDirectory(Path.GetDirectoryName(p.FilePath!)), p.Name))
                .Where(p => p.Directory.Length > 0)
                .OrderByDescending(p => p.Directory.Length)
                .ToList();
            return new ProjectAttributor(projects);
        }

        public string? ProjectOf(string? file)
        {
            if (file is null)
            {
                return null;
            }

            string normalized = file.Replace('\\', '/');
            foreach (var (directory, name) in _projects)
            {
                if (normalized.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
                {
                    return name;
                }
            }

            return null;
        }

        // Trailing slash so "…/WebMVC/" does not prefix-match "…/WebMVC.Tests/…".
        private static string NormalizeDirectory(string? directory) =>
            string.IsNullOrEmpty(directory) ? string.Empty : directory.Replace('\\', '/').TrimEnd('/') + "/";
    }

    /// <summary>Best-effort char-offset → 1-based line, reading (and caching) source files that still exist.</summary>
    private sealed class LineResolver
    {
        private readonly Dictionary<string, string?> _contents = new(StringComparer.Ordinal);

        public string LineOf(string file, int charOffset)
        {
            if (!_contents.TryGetValue(file, out var text))
            {
                text = File.Exists(file) ? SafeRead(file) : null;
                _contents[file] = text;
            }

            if (text is null || charOffset < 0 || charOffset > text.Length)
            {
                return "?";
            }

            int line = 1;
            for (int i = 0; i < charOffset; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                }
            }

            return line.ToString();
        }

        private static string? SafeRead(string file)
        {
            try
            {
                return File.ReadAllText(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
