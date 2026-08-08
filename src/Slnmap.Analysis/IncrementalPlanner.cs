using Slnmap.Core.Analysis;
using Slnmap.Core.Graph;

namespace Slnmap.Analysis;

/// <summary>What an analysis run must do: which files to (re)analyze, starting from which graph.</summary>
internal sealed class AnalysisPlan
{
    public required HashSet<string> FilesToAnalyze { get; init; }

    public required CodeGraph BaselineGraph { get; init; }

    public static AnalysisPlan Full(IEnumerable<string> allFiles) => new()
    {
        FilesToAnalyze = new HashSet<string>(allFiles, StringComparer.Ordinal),
        BaselineGraph = new CodeGraph(),
    };
}

/// <summary>
/// Plans incremental re-analysis from a previous snapshot: changed and added files are
/// re-analyzed, plus their one-hop dependents — files whose symbols hold edges into symbols
/// the changed files declare. Re-walking a dependent exists only to refresh that dependent's
/// own outgoing edges (in case a symbol it points at was renamed or removed in the changed
/// file) — it does not, and need not, protect edges that *other* files hold into the
/// dependent's own unrelated declarations. Edge survival in the baseline is scoped by the
/// edge's source file only: an edge is "owned" by whichever document's walk produced it, and
/// every node id is a deterministic content hash (kind + fully-qualified name), so a
/// surviving edge always re-links correctly once its target's file is re-walked — see the
/// eviction loop below. A post-merge dangling-edge prune (<c>RoslynSolutionAnalyzer</c>)
/// is the safety net for edges whose target was genuinely deleted, not just regenerated.
/// </summary>
internal static class IncrementalPlanner
{
    public static AnalysisPlan Plan(AnalysisSnapshot previous, IReadOnlyDictionary<string, string> currentHashes)
    {
        var previousHashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in previous.Files)
        {
            previousHashes[file.Path] = file.ContentHash;
        }

        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (path, hash) in currentHashes)
        {
            if (!previousHashes.TryGetValue(path, out var previousHash) || previousHash != hash)
            {
                changed.Add(path);
            }
        }

        var removed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in previousHashes.Keys)
        {
            if (!currentHashes.ContainsKey(path))
            {
                removed.Add(path);
            }
        }

        var graph = previous.Graph;
        var dirty = new HashSet<string>(changed, StringComparer.Ordinal);
        dirty.UnionWith(removed);

        var fileOfNode = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes)
        {
            fileOfNode[node.Id] = node.FilePath;
        }

        var affectedNodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in graph.Nodes)
        {
            if (node.FilePath is not null && dirty.Contains(node.FilePath))
            {
                affectedNodes.Add(node.Id);
            }
        }

        // Dependents: files whose members point at symbols declared in dirty files. One hop
        // suffices for THIS purpose — a dependent's own declarations are unchanged, so
        // re-walking it once is enough to refresh its own outgoing edges (the only thing that
        // can differ: whether the symbol it references in the dirty file still exists, or still
        // means the same thing). This does not, by itself, protect edges that some other file
        // holds into the dependent's own unrelated nodes — eviction below is scoped by an
        // edge's source file only, precisely so re-walking a dependent never has to imply
        // evicting edges other files own into it.
        var filesToAnalyze = new HashSet<string>(changed, StringComparer.Ordinal);
        foreach (var edge in graph.Edges)
        {
            if (!affectedNodes.Contains(edge.TargetId))
            {
                continue;
            }

            if (fileOfNode.TryGetValue(edge.SourceId, out var sourceFile)
                && sourceFile is not null
                && currentHashes.ContainsKey(sourceFile))
            {
                filesToAnalyze.Add(sourceFile);
            }
        }

        var evicted = new HashSet<string>(filesToAnalyze, StringComparer.Ordinal);
        evicted.UnionWith(removed);

        var baseline = new CodeGraph();
        foreach (var node in graph.Nodes)
        {
            if (node.FilePath is null || !evicted.Contains(node.FilePath))
            {
                baseline.AddNode(node);
            }
        }

        // An edge is owned by whichever document's walk produced it — always the edge's SOURCE
        // (DocumentWalker only ever emits an edge while walking the document containing the
        // referencing code). So an edge survives here whenever its source file survives, even if
        // its target's file is being re-walked: the target's node id is a deterministic content
        // hash (kind + fully-qualified name, see SymbolNode.CreateId), so re-walking the target's
        // file regenerates an identical node id for an unchanged symbol and the kept edge
        // re-links correctly. If the target symbol was genuinely removed (not just regenerated),
        // the edge becomes dangling and is swept up by RoslynSolutionAnalyzer's post-merge
        // dangling-edge prune, not by gating eviction on the target here.
        foreach (var edge in graph.Edges)
        {
            bool sourceKept = fileOfNode.GetValueOrDefault(edge.SourceId) is not { } sourceFile || !evicted.Contains(sourceFile);
            if (sourceKept)
            {
                baseline.AddEdge(edge);
            }
        }

        return new AnalysisPlan { FilesToAnalyze = filesToAnalyze, BaselineGraph = baseline };
    }
}
