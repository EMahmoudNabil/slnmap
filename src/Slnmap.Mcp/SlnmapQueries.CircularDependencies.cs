using System.Text;
using Slnmap.Core.Graph;

namespace Slnmap.Mcp;

public sealed partial class SlnmapQueries
{
    private const int CycleCap = 25;
    private const int CyclePathDisplayCap = 12;

    /// <summary>
    /// Detects dependency cycles between projects (scope=project) or namespaces (scope=namespace) over
    /// the derived container graph (cross-container References/Calls; Contains excluded). Each cycle is
    /// reported as a path chain, worst offenders (most crossing references) first, capped. An acyclic
    /// solution reports "0 cycles" — a real answer, not an empty one.
    /// </summary>
    public async Task<string> FindCircularDependenciesAsync(string scope, CancellationToken cancellationToken = default)
    {
        if (await NotAnalyzedAsync(cancellationToken).ConfigureAwait(false) is { } notReady)
        {
            return notReady;
        }

        string level = (scope ?? "project").Trim().ToLowerInvariant();
        if (level is not ("project" or "namespace"))
        {
            return "scope must be 'project' or 'namespace'.";
        }

        var graph = await _store.LoadGraphAsync(cancellationToken).ConfigureAwait(false);

        // Map each node to its container (project or namespace); build a weighted directed graph of
        // cross-container edges. Both derivations reuse the file-path / fqn-prefix attribution the
        // other tools use — container edges are not stored, so this is computed, and the output says so.
        Func<string, string?> containerOf = level == "project"
            ? BuildProjectContainerMap(graph)
            : BuildNamespaceContainerMap(graph);

        var weights = new Dictionary<(string From, string To), int>();
        var adjacency = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        int containerCount = level == "project"
            ? graph.Nodes.Count(n => n.Kind == NodeKind.Project)
            : graph.Nodes.Count(n => n.Kind == NodeKind.Namespace);

        foreach (var edge in graph.Edges)
        {
            if (edge.Kind == RelationshipKind.Contains)
            {
                continue;
            }

            string? from = containerOf(edge.SourceId);
            string? to = containerOf(edge.TargetId);
            if (from is null || to is null || string.Equals(from, to, StringComparison.Ordinal))
            {
                continue;
            }

            weights[(from, to)] = weights.GetValueOrDefault((from, to)) + 1;
            if (!adjacency.TryGetValue(from, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                adjacency[from] = set;
            }

            set.Add(to);
        }

        var cycles = new List<(List<string> Path, int Hops, int Weight)>();
        foreach (var component in StronglyConnectedComponents(adjacency).Where(c => c.Count > 1))
        {
            var path = FindCycleWithin(component, adjacency);
            if (path.Count < 2)
            {
                continue;
            }

            int weight = 0;
            for (int i = 0; i < path.Count - 1; i++)
            {
                weight += weights.GetValueOrDefault((path[i], path[i + 1]));
            }

            cycles.Add((path, path.Count - 1, weight));
        }

        var builder = new StringBuilder();
        if (cycles.Count == 0)
        {
            builder.AppendLine($"0 {level}-level dependency cycle(s) found ({containerCount} {level}(s), derived from cross-{level} references).");
            return builder.ToString().TrimEnd();
        }

        var ranked = cycles.OrderByDescending(c => c.Weight).ThenByDescending(c => c.Hops).ToList();
        builder.AppendLine($"{ranked.Count} {level}-level dependency cycle(s) found (scope={level}), worst first:");
        foreach (var (path, hops, weight) in ranked.Take(CycleCap))
        {
            builder.AppendLine($"  {FormatCyclePath(path)} ({hops} hops, {weight} crossing refs)");
        }

        if (ranked.Count > CycleCap)
        {
            builder.AppendLine($"  {CycleCap}+ cycles, showing first {CycleCap}.");
        }

        return builder.ToString().TrimEnd();
    }

    private Func<string, string?> BuildProjectContainerMap(CodeGraph graph)
    {
        var attributor = ProjectAttributor.From(graph.Nodes.Where(n => n.Kind == NodeKind.Project));
        var fileById = graph.Nodes.ToDictionary(n => n.Id, n => n.FilePath, StringComparer.Ordinal);
        return id => attributor.ProjectOf(fileById.GetValueOrDefault(id));
    }

    private static Func<string, string?> BuildNamespaceContainerMap(CodeGraph graph)
    {
        // Longest namespace whose FQN is a prefix (on a dot boundary) of the node's FQN wins.
        var namespaces = graph.Nodes
            .Where(n => n.Kind == NodeKind.Namespace)
            .Select(n => n.Fqn)
            .OrderByDescending(f => f.Length)
            .ToList();
        var fqnById = graph.Nodes.ToDictionary(n => n.Id, n => n.Fqn, StringComparer.Ordinal);
        return id =>
        {
            if (!fqnById.TryGetValue(id, out var fqn))
            {
                return null;
            }

            foreach (var ns in namespaces)
            {
                if (fqn.Equals(ns, StringComparison.Ordinal) || fqn.StartsWith(ns + ".", StringComparison.Ordinal))
                {
                    return ns;
                }
            }

            return null;
        };
    }

    /// <summary>Tarjan's strongly-connected-components over the small derived container graph.</summary>
    private static List<List<string>> StronglyConnectedComponents(Dictionary<string, HashSet<string>> adjacency)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowlink = new Dictionary<string, int>(StringComparer.Ordinal);
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        var components = new List<List<string>>();
        int counter = 0;

        void StrongConnect(string v)
        {
            index[v] = counter;
            lowlink[v] = counter;
            counter++;
            stack.Push(v);
            onStack.Add(v);

            if (adjacency.TryGetValue(v, out var neighbors))
            {
                foreach (var w in neighbors)
                {
                    if (!index.ContainsKey(w))
                    {
                        StrongConnect(w);
                        lowlink[v] = Math.Min(lowlink[v], lowlink[w]);
                    }
                    else if (onStack.Contains(w))
                    {
                        lowlink[v] = Math.Min(lowlink[v], index[w]);
                    }
                }
            }

            if (lowlink[v] == index[v])
            {
                var component = new List<string>();
                string w;
                do
                {
                    w = stack.Pop();
                    onStack.Remove(w);
                    component.Add(w);
                }
                while (!string.Equals(w, v, StringComparison.Ordinal));
                components.Add(component);
            }
        }

        foreach (var node in adjacency.Keys)
        {
            if (!index.ContainsKey(node))
            {
                StrongConnect(node);
            }
        }

        return components;
    }

    /// <summary>Finds one concrete cycle within a strongly-connected component (guaranteed to exist).</summary>
    private static List<string> FindCycleWithin(List<string> component, Dictionary<string, HashSet<string>> adjacency)
    {
        var members = component.ToHashSet(StringComparer.Ordinal);
        string start = component.OrderBy(c => c, StringComparer.Ordinal).First();
        var result = new List<string>();

        bool Dfs(string current, List<string> path, HashSet<string> onPath)
        {
            if (!adjacency.TryGetValue(current, out var neighbors))
            {
                return false;
            }

            foreach (var next in neighbors.Where(members.Contains).OrderBy(n => n, StringComparer.Ordinal))
            {
                if (string.Equals(next, start, StringComparison.Ordinal) && path.Count >= 1)
                {
                    result.AddRange(path);
                    result.Add(start);
                    return true;
                }

                if (onPath.Add(next))
                {
                    path.Add(next);
                    if (Dfs(next, path, onPath))
                    {
                        return true;
                    }

                    path.RemoveAt(path.Count - 1);
                    onPath.Remove(next);
                }
            }

            return false;
        }

        Dfs(start, [start], new HashSet<string>(StringComparer.Ordinal) { start });
        return result;
    }

    private static string FormatCyclePath(List<string> path)
    {
        if (path.Count <= CyclePathDisplayCap)
        {
            return string.Join(" -> ", path);
        }

        return string.Join(" -> ", path.Take(CyclePathDisplayCap)) + " -> ...";
    }
}
