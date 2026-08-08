using System.Collections.Concurrent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Slnmap.Core.Analysis;
using Slnmap.Core.Graph;
using Slnmap.Core.Storage;

namespace Slnmap.Analysis;

/// <summary>
/// Analyzes a solution with Roslyn's <see cref="MSBuildWorkspace"/> and produces a code graph.
/// Roslyn types stay inside this assembly; only Slnmap.Core types cross the boundary.
/// </summary>
/// <remarks>
/// No MSBuildLocator registration is needed: since Roslyn 4.9, MSBuildWorkspace runs design-time
/// builds in an out-of-process BuildHost that locates MSBuild itself. Workspace load failures are
/// surfaced through the warning sink and analysis continues with whatever loaded.
/// </remarks>
public sealed class RoslynSolutionAnalyzer : ISolutionAnalyzer
{
    private readonly Action<string>? _warningSink;

    public RoslynSolutionAnalyzer(Action<string>? warningSink = null) => _warningSink = warningSink;

    public async Task<AnalysisSnapshot> AnalyzeAsync(
        string solutionPath,
        AnalysisSnapshot? previous = null,
        IProgress<AnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solutionPath);
        solutionPath = Path.GetFullPath(solutionPath);
        if (!File.Exists(solutionPath))
        {
            throw new FileNotFoundException("Solution or project file not found.", solutionPath);
        }

        using var workspace = MSBuildWorkspace.Create();
        var solution = await OpenAsync(workspace, solutionPath, progress, cancellationToken).ConfigureAwait(false);
        foreach (var diagnostic in workspace.Diagnostics)
        {
            // A non-language project (e.g. a .dcproj) has no C# to analyze; dropping its diagnostic
            // rather than surfacing it keeps the warning list to things the user can act on.
            if (WarningReport.IsNonLanguageProjectDiagnostic(diagnostic.Message))
            {
                continue;
            }

            _warningSink?.Invoke(diagnostic.Message);
        }

        // One entry per csproj: multi-targeted projects appear once per TFM, and one TFM
        // is enough to shape the graph.
        var projects = solution.Projects
            .Where(static p => p.Language == LanguageNames.CSharp)
            .GroupBy(static p => p.FilePath, StringComparer.Ordinal)
            .Select(static g => g.First())
            .ToList();

        // Hash every document up front: input to the incremental planner, output for the next run.
        var documentPaths = projects
            .SelectMany(static p => p.Documents)
            .Select(static d => d.FilePath)
            .OfType<string>()
            .Distinct(StringComparer.Ordinal);
        var currentHashes = FileHasher.HashFiles(documentPaths, cancellationToken);

        var plan = previous is null
            ? AnalysisPlan.Full(currentHashes.Keys)
            : IncrementalPlanner.Plan(previous, currentHashes);

        var work = projects
            .Select(p => (Project: p,
                Documents: p.Documents.Where(d => d.FilePath is not null && plan.FilesToAnalyze.Contains(d.FilePath)).ToList()))
            .ToList();
        int totalDocuments = work.Sum(static w => w.Documents.Count);
        int candidateDocuments = work.Sum(static w => w.Project.Documents.Count(static d => d.FilePath is not null));

        var results = new ConcurrentBag<DocumentResult>();
        var projectNodes = new List<SymbolNode>();
        int analyzedCount = 0;
        int compiledCount = 0;

        foreach (var (project, documents) in work)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new AnalysisProgress("Compiling", ++compiledCount, work.Count));

            // Warms the solution snapshot's compilation cache sequentially (in dependency
            // order), so the parallel semantic-model work below never rebuilds one.
            var compilation = await project.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (compilation is null)
            {
                _warningSink?.Invoke($"Skipping project '{project.Name}': no compilation available.");
                continue;
            }

            var projectNode = SymbolNode.Create(NodeKind.Project, project.Name, project.Name, project.FilePath);
            projectNodes.Add(projectNode);
            if (documents.Count == 0)
            {
                continue;
            }

            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = cancellationToken,
            };
            await Parallel.ForEachAsync(documents, options, async (document, ct) =>
            {
                try
                {
                    var result = await DocumentWalker.AnalyzeAsync(document, projectNode.Id, ct).ConfigureAwait(false);
                    if (result is not null)
                    {
                        results.Add(result);
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _warningSink?.Invoke($"Failed to analyze '{document.FilePath}': {e.Message}");
                }

                int done = Interlocked.Increment(ref analyzedCount);
                progress?.Report(new AnalysisProgress("Analyzing", done, totalDocuments));
            }).ConfigureAwait(false);
        }

        var graph = plan.BaselineGraph;
        foreach (var projectNode in projectNodes)
        {
            graph.AddNode(projectNode);
        }

        foreach (var result in results)
        {
            foreach (var node in result.Nodes)
            {
                graph.AddNode(node);
            }

            foreach (var edge in result.Edges)
            {
                graph.AddEdge(edge);
            }
        }

        // Order matters: dangling edges must go first, so orphan-namespace detection never
        // miscounts a namespace as "still has children" via an edge to a node that no longer
        // exists in the merged graph.
        graph = PruneDanglingEdges(graph);
        graph = PruneOrphanNamespaces(graph);

        var files = currentHashes.Select(static kv => new FileRecord(kv.Key, kv.Value)).ToList();
        var stats = new AnalysisStats(projects.Count, analyzedCount, candidateDocuments - totalDocuments);
        return new AnalysisSnapshot(graph, files, stats);
    }

    private async Task<Solution> OpenAsync(
        MSBuildWorkspace workspace,
        string path,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new AnalysisProgress("Loading", 0, 0));
        var loadProgress = progress is null ? null : new LoadProgressAdapter(progress);

        try
        {
            if (path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            {
                var project = await workspace.OpenProjectAsync(path, loadProgress, cancellationToken).ConfigureAwait(false);
                return project.Solution;
            }

            return await workspace.OpenSolutionAsync(path, loadProgress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException && SdkResolutionDiagnostics.IsSdkResolutionFailure(e))
        {
            // The out-of-process build host can't resolve the SDK the solution requires (usually a
            // global.json pinning an uninstalled SDK). Surface an actionable message, not a stack trace.
            var installed = await DotnetSdks.ListAsync(cancellationToken).ConfigureAwait(false);
            var requirement = GlobalJson.FindSdkRequirement(path);
            throw new SdkNotFoundException(SdkResolutionDiagnostics.BuildMessage(path, requirement, installed.Versions), e);
        }
    }

    /// <summary>
    /// Drops any edge whose source or target node id is absent from the merged graph. The
    /// incremental planner's baseline eviction (<see cref="IncrementalPlanner"/>) keeps an edge
    /// whenever its SOURCE file survives, regardless of whether its target's file is being
    /// re-walked — that's safe when the target symbol still exists (same content-hash id, just
    /// regenerated), but if the target symbol was genuinely deleted (not just moved or
    /// unchanged), the kept edge now points at a node id that no longer exists anywhere in this
    /// graph. This is the safety net for that case: run after merging, before
    /// <see cref="PruneOrphanNamespaces"/>.
    /// </summary>
    private static CodeGraph PruneDanglingEdges(CodeGraph graph)
    {
        var pruned = new CodeGraph();
        foreach (var node in graph.Nodes)
        {
            pruned.AddNode(node);
        }

        foreach (var edge in graph.Edges)
        {
            if (graph.ContainsNode(edge.SourceId) && graph.ContainsNode(edge.TargetId))
            {
                pruned.AddEdge(edge);
            }
        }

        return pruned;
    }

    /// <summary>Namespaces that no longer contain anything (after incremental eviction) are dropped.</summary>
    private static CodeGraph PruneOrphanNamespaces(CodeGraph graph)
    {
        var dead = new HashSet<string>(StringComparer.Ordinal);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var node in graph.Nodes)
            {
                if (node.Kind != NodeKind.Namespace || dead.Contains(node.Id))
                {
                    continue;
                }

                if (!graph.OutgoingEdges(node.Id, RelationshipKind.Contains).Any(e => !dead.Contains(e.TargetId)))
                {
                    dead.Add(node.Id);
                    changed = true;
                }
            }
        }

        if (dead.Count == 0)
        {
            return graph;
        }

        var pruned = new CodeGraph();
        foreach (var node in graph.Nodes)
        {
            if (!dead.Contains(node.Id))
            {
                pruned.AddNode(node);
            }
        }

        foreach (var edge in graph.Edges)
        {
            if (!dead.Contains(edge.SourceId) && !dead.Contains(edge.TargetId))
            {
                pruned.AddEdge(edge);
            }
        }

        return pruned;
    }

    /// <summary>Translates MSBuild project-load progress into analysis progress (total unknown).</summary>
    private sealed class LoadProgressAdapter : IProgress<ProjectLoadProgress>
    {
        private readonly IProgress<AnalysisProgress> _inner;
        private int _loaded;

        public LoadProgressAdapter(IProgress<AnalysisProgress> inner) => _inner = inner;

        public void Report(ProjectLoadProgress value)
        {
            if (value.Operation == ProjectLoadOperation.Resolve)
            {
                _inner.Report(new AnalysisProgress("Loading", Interlocked.Increment(ref _loaded), 0));
            }
        }
    }
}
