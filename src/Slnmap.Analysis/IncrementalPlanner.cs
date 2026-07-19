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
/// re-analyzed, plus their dependents — files whose symbols hold edges into symbols the
/// changed files declare. Everything those files previously contributed is evicted from
/// the baseline graph; untouched files carry over as-is.
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

        // Dependents: files whose members point at symbols declared in dirty files.
        // One level suffices — a dependent's own declarations are unchanged, so nothing
        // further out can observe a difference.
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

        foreach (var edge in graph.Edges)
        {
            bool sourceKept = fileOfNode.GetValueOrDefault(edge.SourceId) is not { } sourceFile || !evicted.Contains(sourceFile);
            bool targetKept = fileOfNode.GetValueOrDefault(edge.TargetId) is not { } targetFile || !evicted.Contains(targetFile);
            if (sourceKept && targetKept)
            {
                baseline.AddEdge(edge);
            }
        }

        return new AnalysisPlan { FilesToAnalyze = filesToAnalyze, BaselineGraph = baseline };
    }
}
