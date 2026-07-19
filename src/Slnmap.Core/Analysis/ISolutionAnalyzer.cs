using Slnmap.Core.Graph;
using Slnmap.Core.Storage;

namespace Slnmap.Core.Analysis;

/// <summary>Builds a <see cref="CodeGraph"/> from a solution or project.</summary>
public interface ISolutionAnalyzer
{
    /// <summary>
    /// Analyzes <paramref name="solutionPath"/> (a .sln or .csproj).
    /// When <paramref name="previous"/> is supplied, analysis is incremental: only documents whose
    /// content hash changed — plus documents holding edges into symbols those documents declare —
    /// are re-analyzed; everything else is carried over from the previous snapshot.
    /// </summary>
    Task<AnalysisSnapshot> AnalyzeAsync(
        string solutionPath,
        AnalysisSnapshot? previous = null,
        IProgress<AnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The result of one analysis run: the graph, plus the file hashes needed to run
/// incrementally next time.
/// </summary>
public sealed record AnalysisSnapshot(CodeGraph Graph, IReadOnlyList<FileRecord> Files, AnalysisStats Stats);

/// <summary>Counters describing how much work an analysis run performed.</summary>
public sealed record AnalysisStats(int ProjectCount, int DocumentsAnalyzed, int DocumentsSkipped);

/// <summary>A progress report emitted while analyzing, e.g. ("Compiling", 3, 12). Total may be 0 when unknown.</summary>
public sealed record AnalysisProgress(string Stage, int Completed, int Total);
