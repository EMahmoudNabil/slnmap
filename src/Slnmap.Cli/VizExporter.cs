using System.Reflection;
using System.Text;
using System.Text.Json;
using Slnmap.Core.Graph;

/// <summary>
/// Builds the self-contained interactive HTML for <c>slnmap viz</c>: the graph is embedded as
/// compact JSON (array-of-arrays, integer ids) inside an HTML template that carries the rendering
/// library, so the output opens by double-click with no server and no network access.
/// </summary>
internal static class VizExporter
{
    /// <summary>What the command prints after writing the file.</summary>
    internal sealed record VizStats(
        int NodeCount,
        int DependencyEdgeCount,
        int NamespaceInstances,
        int OrphansAttachedToProjects,
        int OrphansUnattributed,
        long OutputBytes);

    private static readonly string[] EdgeKinds =
        [nameof(RelationshipKind.Calls), nameof(RelationshipKind.References), nameof(RelationshipKind.Implements), nameof(RelationshipKind.Inherits)];

    public static VizStats WriteHtml(
        CodeGraph graph,
        IReadOnlyDictionary<string, string> meta,
        string outputPath,
        string? projectFilter)
    {
        var (nodes, parent, dependencyEdges, namespaceInstances, attached, unattributedCount) = BuildVizModel(graph);

        if (projectFilter is not null)
        {
            (nodes, parent, dependencyEdges) = FilterToProject(nodes, parent, dependencyEdges, projectFilter);
        }

        string solutionDirectory = meta.TryGetValue(Slnmap.Core.Storage.MetaKeys.SolutionPath, out var solutionPath)
            ? Path.GetDirectoryName(solutionPath) ?? string.Empty
            : string.Empty;

        string json = SerializePayload(nodes, parent, dependencyEdges, meta, solutionDirectory);
        string html = LoadResource("viz-template.html")
            .Replace("__SLNMAP_LIB__", LoadResource("vis-network.min.js"))
            .Replace("__SLNMAP_DATA__", json);
        File.WriteAllText(outputPath, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return new VizStats(nodes.Count, dependencyEdges.Count, namespaceInstances, attached, unattributedCount, new FileInfo(outputPath).Length);
    }

    /// <summary>
    /// Turns the stored graph into a strict drill-down tree. Contains is a DAG at the namespace
    /// level — one namespace node is shared by every project that declares types in it — so shared
    /// namespace nodes are materialized as one instance per project, and every type hangs off its
    /// own project's instance (the project is resolved from the type's file path). Types and
    /// members keep their single identity; only namespaces are copied.
    /// </summary>
    private static (List<SymbolNode> Nodes, int[] Parent, List<(int, int, int)> Edges, int NamespaceInstances, int Attached, int Unattributed)
        BuildVizModel(CodeGraph graph)
    {
        var source = graph.Nodes.ToList();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < source.Count; i++)
        {
            index[source[i].Id] = i;
        }

        // Raw Contains relations. For types/members the parent is unique; for namespaces we keep
        // only the namespace→namespace edge (the project→namespace edges are the shared fan-in).
        var rawParent = new int[source.Count];
        Array.Fill(rawParent, -1);
        var namespaceParent = new int[source.Count];
        Array.Fill(namespaceParent, -1);
        var dependencyEdges = new List<(int, int, int)>();
        foreach (var edge in graph.Edges)
        {
            if (edge.Kind == RelationshipKind.Contains)
            {
                int s = index[edge.SourceId], t = index[edge.TargetId];
                if (source[t].Kind == NodeKind.Namespace)
                {
                    if (source[s].Kind == NodeKind.Namespace)
                    {
                        namespaceParent[t] = s;
                    }
                }
                else
                {
                    rawParent[t] = s;
                }
            }
            else
            {
                int kind = Array.IndexOf(EdgeKinds, edge.Kind.ToString());
                if (kind >= 0)
                {
                    dependencyEdges.Add((index[edge.SourceId], index[edge.TargetId], kind));
                }
            }
        }

        var projectDirectories = source
            .Where(n => n.Kind == NodeKind.Project && n.FilePath is not null)
            .Select(n => (Directory: NormalizeDirectory(Path.GetDirectoryName(n.FilePath!)), Index: index[n.Id]))
            .Where(p => p.Directory.Length > 0)
            .OrderByDescending(p => p.Directory.Length)
            .ToList();

        int ProjectOfFile(string? filePath)
        {
            if (filePath is null)
            {
                return -1;
            }

            string file = filePath.Replace('\\', '/');
            foreach (var (directory, projectIndex) in projectDirectories)
            {
                if (file.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
                {
                    return projectIndex;
                }
            }

            return -1;
        }

        // Output list: projects first, then types/members (namespaces are re-materialized).
        var nodes = new List<SymbolNode>();
        var parent = new List<int>();
        var newIndex = new int[source.Count];
        Array.Fill(newIndex, -1);
        int synthetic = -1, attached = 0, unattributed = 0;
        var instances = new Dictionary<(int Project, string Fqn), int>();

        for (int i = 0; i < source.Count; i++)
        {
            if (source[i].Kind == NodeKind.Project)
            {
                newIndex[i] = nodes.Count;
                nodes.Add(source[i]);
                parent.Add(-1);
            }
        }

        int SyntheticRoot()
        {
            if (synthetic < 0)
            {
                synthetic = nodes.Count;
                nodes.Add(SymbolNode.Create(NodeKind.Project, "(unattributed)", "(unattributed)"));
                parent.Add(-1);
            }

            return synthetic;
        }

        // One namespace instance per (project, namespace-chain); parents chain up to the project.
        int NamespaceInstance(int newProject, int ns)
        {
            if (instances.TryGetValue((newProject, source[ns].Fqn), out int existing))
            {
                return existing;
            }

            int up = namespaceParent[ns] >= 0 ? NamespaceInstance(newProject, namespaceParent[ns]) : newProject;
            int created = nodes.Count;
            nodes.Add(source[ns]);
            parent.Add(up);
            instances[(newProject, source[ns].Fqn)] = created;
            return created;
        }

        int Place(int i)
        {
            if (newIndex[i] >= 0)
            {
                return newIndex[i];
            }

            var node = source[i];
            int raw = rawParent[i];
            int placed;
            if (raw >= 0 && source[raw].Kind is not (NodeKind.Namespace or NodeKind.Project))
            {
                // Members and nested types hang off their containing type, wherever it lands.
                placed = Place(raw);
            }
            else
            {
                int project = ProjectOfFile(node.FilePath);
                if (project < 0 && raw >= 0 && source[raw].Kind == NodeKind.Project)
                {
                    project = raw; // global-namespace type: Contains points straight at the project
                }

                if (project < 0)
                {
                    if (raw < 0)
                    {
                        unattributed++;
                    }

                    placed = SyntheticRoot();
                }
                else
                {
                    if (raw < 0)
                    {
                        attached++; // orphan reattached purely by file path
                    }

                    placed = raw >= 0 && source[raw].Kind == NodeKind.Namespace
                        ? NamespaceInstance(newIndex[project], raw)
                        : newIndex[project];
                }
            }

            newIndex[i] = nodes.Count;
            nodes.Add(node);
            parent.Add(placed);
            return newIndex[i];
        }

        for (int i = 0; i < source.Count; i++)
        {
            if (source[i].Kind is not (NodeKind.Project or NodeKind.Namespace))
            {
                Place(i);
            }
        }

        var edges = dependencyEdges
            .Where(e => newIndex[e.Item1] >= 0 && newIndex[e.Item2] >= 0)
            .Select(e => (newIndex[e.Item1], newIndex[e.Item2], e.Item3))
            .ToList();

        (nodes, var finalParent, edges) = CollapseNamespaceChains(nodes, parent.ToArray(), edges);
        int namespaceCount = nodes.Count(n => n.Kind == NodeKind.Namespace);
        return (nodes, finalParent, edges, namespaceCount, attached, unattributed);
    }

    /// <summary>
    /// Folds single-child namespace chains (Microsoft → eShopWeb → Web) into one node so drilling
    /// never wades through empty levels. A namespace whose only live child is another namespace is
    /// absorbed by that child; kept namespaces are then relabeled relative to their nearest kept
    /// namespace ancestor (a chain head under a project shows its full dotted name).
    /// </summary>
    private static (List<SymbolNode>, int[], List<(int, int, int)>) CollapseNamespaceChains(
        List<SymbolNode> nodes, int[] parent, List<(int, int, int)> edges)
    {
        var kids = new List<int>[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            kids[i] = [];
        }

        for (int i = 0; i < nodes.Count; i++)
        {
            if (parent[i] >= 0)
            {
                kids[parent[i]].Add(i);
            }
        }

        var removed = new bool[nodes.Count];
        bool changed = true;
        while (changed)
        {
            changed = false;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (removed[i] || nodes[i].Kind != NodeKind.Namespace)
                {
                    continue;
                }

                var live = kids[i].Where(c => !removed[c]).ToList();
                if (live.Count == 1 && nodes[live[0]].Kind == NodeKind.Namespace)
                {
                    parent[live[0]] = parent[i];
                    if (parent[i] >= 0)
                    {
                        kids[parent[i]].Add(live[0]);
                    }

                    removed[i] = true;
                    changed = true;
                }
            }
        }

        var map = new int[nodes.Count];
        Array.Fill(map, -1);
        var outNodes = new List<SymbolNode>();
        var oldParent = new List<int>();
        for (int i = 0; i < nodes.Count; i++)
        {
            if (!removed[i])
            {
                map[i] = outNodes.Count;
                outNodes.Add(nodes[i]);
                oldParent.Add(parent[i]);
            }
        }

        var outParent = new int[outNodes.Count];
        for (int j = 0; j < outNodes.Count; j++)
        {
            outParent[j] = oldParent[j] < 0 ? -1 : map[oldParent[j]];
        }

        for (int j = 0; j < outNodes.Count; j++)
        {
            if (outNodes[j].Kind != NodeKind.Namespace)
            {
                continue;
            }

            string? ancestorFqn = null;
            for (int p = outParent[j]; p >= 0; p = outParent[p])
            {
                if (outNodes[p].Kind == NodeKind.Namespace)
                {
                    ancestorFqn = outNodes[p].Fqn;
                    break;
                }
            }

            string label = ancestorFqn is not null && outNodes[j].Fqn.StartsWith(ancestorFqn + ".", StringComparison.Ordinal)
                ? outNodes[j].Fqn[(ancestorFqn.Length + 1)..]
                : outNodes[j].Fqn;
            outNodes[j] = outNodes[j] with { Name = label };
        }

        var outEdges = edges.Select(e => (map[e.Item1], map[e.Item2], e.Item3)).ToList();
        return (outNodes, outParent, outEdges);
    }

    /// <summary>
    /// Keeps the chosen project's subtree plus every other project root as a collapsed stub;
    /// cross-project edges are re-pointed at those stubs so coupling stays visible.
    /// </summary>
    private static (List<SymbolNode>, int[], List<(int, int, int)>) FilterToProject(
        List<SymbolNode> nodes, int[] parent, List<(int Source, int Target, int Kind)> edges, string projectFilter)
    {
        int chosen = -1;
        var projectNames = new List<string>();
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].Kind != NodeKind.Project)
            {
                continue;
            }

            projectNames.Add(nodes[i].Name);
            if (string.Equals(nodes[i].Name, projectFilter, StringComparison.OrdinalIgnoreCase))
            {
                chosen = i;
            }
        }

        if (chosen < 0)
        {
            throw new ArgumentException(
                $"Unknown project '{projectFilter}'. Valid projects: {string.Join(", ", projectNames.OrderBy(n => n, StringComparer.Ordinal))}.");
        }

        var rootOf = new int[nodes.Count];
        for (int i = 0; i < nodes.Count; i++)
        {
            int r = i;
            while (parent[r] >= 0)
            {
                r = parent[r];
            }

            rootOf[i] = r;
        }

        var keep = new List<int>();
        for (int i = 0; i < nodes.Count; i++)
        {
            if (rootOf[i] == chosen || nodes[i].Kind == NodeKind.Project)
            {
                keep.Add(i);
            }
        }

        var remap = new Dictionary<int, int>();
        for (int i = 0; i < keep.Count; i++)
        {
            remap[keep[i]] = i;
        }

        var newNodes = keep.Select(i => nodes[i]).ToList();
        var newParent = keep.Select(i => parent[i] >= 0 && remap.ContainsKey(parent[i]) ? remap[parent[i]] : -1).ToArray();
        var newEdges = new List<(int, int, int)>();
        var seen = new HashSet<(int, int, int)>();
        foreach (var (s, t, k) in edges)
        {
            bool sIn = rootOf[s] == chosen;
            bool tIn = rootOf[t] == chosen;
            if (!sIn && !tIn)
            {
                continue;
            }

            int ns = sIn ? remap[s] : remap[rootOf[s]];
            int nt = tIn ? remap[t] : remap[rootOf[t]];
            if (seen.Add((ns, nt, k)))
            {
                newEdges.Add((ns, nt, k));
            }
        }

        return (newNodes, newParent, newEdges);
    }

    private static string SerializePayload(
        List<SymbolNode> nodes,
        int[] parent,
        List<(int Source, int Target, int Kind)> edges,
        IReadOnlyDictionary<string, string> meta,
        string solutionDirectory)
    {
        var lines = new LineCache();
        using var buffer = new MemoryStream();
        // The default JSON encoder escapes <, >, & and quotes, so the payload can never terminate
        // the surrounding <script> block (real FQNs contain "<anonymous type: ...>" and generics).
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("meta");
            writer.WriteString("solution", meta.GetValueOrDefault(Slnmap.Core.Storage.MetaKeys.SolutionPath));
            writer.WriteString("analyzed", meta.GetValueOrDefault(Slnmap.Core.Storage.MetaKeys.LastAnalyzed));
            writer.WriteEndObject();

            writer.WriteStartArray("kinds");
            foreach (var kind in Enum.GetNames<NodeKind>())
            {
                writer.WriteStringValue(kind);
            }

            writer.WriteEndArray();

            writer.WriteStartArray("edgeKinds");
            foreach (var kind in EdgeKinds)
            {
                writer.WriteStringValue(kind);
            }

            writer.WriteEndArray();

            writer.WriteStartArray("nodes");
            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                writer.WriteStartArray();
                writer.WriteNumberValue((int)node.Kind);
                writer.WriteStringValue(node.Name);
                writer.WriteStringValue(node.Fqn);
                if (node.FilePath is null)
                {
                    writer.WriteNullValue();
                    writer.WriteNullValue();
                }
                else
                {
                    string relative = solutionDirectory.Length > 0
                        ? Path.GetRelativePath(solutionDirectory, node.FilePath)
                        : node.FilePath;
                    writer.WriteStringValue(relative.Replace('\\', '/'));
                    int? line = lines.LineOf(node.FilePath, node.Span?.Start);
                    if (line is { } l)
                    {
                        writer.WriteNumberValue(l);
                    }
                    else
                    {
                        writer.WriteNullValue();
                    }
                }

                writer.WriteNumberValue(parent[i]);
                writer.WriteEndArray();
            }

            writer.WriteEndArray();

            writer.WriteStartArray("edges");
            foreach (var (source, target, kind) in edges)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(source);
                writer.WriteNumberValue(target);
                writer.WriteNumberValue(kind);
                writer.WriteEndArray();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string LoadResource(string logicalName)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Embedded resource '{logicalName}' is missing from the build.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string NormalizeDirectory(string? directory) =>
        string.IsNullOrEmpty(directory) ? string.Empty : directory.Replace('\\', '/').TrimEnd('/') + "/";

    /// <summary>Best-effort char-offset → 1-based line from the source file, one read per file.</summary>
    private sealed class LineCache
    {
        private readonly Dictionary<string, string?> _contents = new(StringComparer.Ordinal);

        public int? LineOf(string file, int? charOffset)
        {
            if (charOffset is not { } offset)
            {
                return null;
            }

            if (!_contents.TryGetValue(file, out var text))
            {
                try
                {
                    text = File.Exists(file) ? File.ReadAllText(file) : null;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    text = null;
                }

                _contents[file] = text;
            }

            if (text is null || offset < 0 || offset > text.Length)
            {
                return null;
            }

            int line = 1;
            for (int i = 0; i < offset; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                }
            }

            return line;
        }
    }
}
