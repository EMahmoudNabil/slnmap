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
/// <param name="UnresolvedEndpoints">Endpoint registrations whose route could not be resolved statically — counted, never guessed (each also surfaces a warning with its location and reason).</param>
/// <param name="ConventionalControllers">Controllers routed conventionally (no route attributes) — a different routing system, noted (one warning per class) rather than counted as unresolved.</param>
/// <param name="RazorPagesNotModeled">Razor Pages (PageModel-derived classes with OnGet/OnPost/... handlers) — route by file location, a different routing system this tool cannot resolve statically; noted (one warning per class), never counted as unresolved (v0.12.2).</param>
/// <param name="RazorFilesDetected">.razor files found on disk under an analyzed project's directory — Blazor component markup is not walked as an analyzer document at all (v0.12.2, foreign-patterns-trial finding #1); disclosed here instead of silently vanishing from the document count.</param>
public sealed record AnalysisStats(
    int ProjectCount,
    int DocumentsAnalyzed,
    int DocumentsSkipped,
    int UnresolvedEndpoints = 0,
    int ConventionalControllers = 0,
    int RazorPagesNotModeled = 0,
    int RazorFilesDetected = 0);

/// <summary>A progress report emitted while analyzing, e.g. ("Compiling", 3, 12). Total may be 0 when unknown.</summary>
public sealed record AnalysisProgress(string Stage, int Completed, int Total);
