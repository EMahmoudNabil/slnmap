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
/// <param name="ControllerLikeClassesUnrecognized">Classes that LOOK like an MVC controller syntactically (name ends in "Controller", an [ApiController]/[Route]/[Controller] attribute on the class, or an [Http*] attribute on a member) but do not classify as one semantically — neither ControllerBase-derived nor matching ASP.NET's POCO-controller discovery rule (v0.13.1). Disclosed (one warning per class) rather than silently skipped — the class this closes was previously invisible to controller-endpoint extraction with zero trace of any kind.</param>
/// <param name="ProjectsNotRestored">Projects analyzed without their dependencies — no resolved metadata references (restore never ran or failed), or a restore that recorded errors (v0.14.0). Their results are incomplete; disclosed per project, never silent.</param>
/// <param name="RouteConventionsRegistered">MVC application-/controller-/action-model convention registrations (Conventions.Add/Insert, or a convention attribute) — each can rewrite route templates at startup, invisibly to static analysis (v0.14.0). Disclosed, never interpreted.</param>
public sealed record AnalysisStats(
    int ProjectCount,
    int DocumentsAnalyzed,
    int DocumentsSkipped,
    int UnresolvedEndpoints = 0,
    int ConventionalControllers = 0,
    int RazorPagesNotModeled = 0,
    int RazorFilesDetected = 0,
    int ControllerLikeClassesUnrecognized = 0,
    int RouteConventionsRegistered = 0,
    int ProjectsNotRestored = 0);

/// <summary>User-supplied analysis settings that change what the graph contains.</summary>
/// <param name="RoutePrefix">
/// A route prefix the app adds to every attribute-routed controller at startup in a way static
/// analysis cannot see (typically an <c>IApplicationModelConvention</c>), stated explicitly by the
/// user (<c>--route-prefix</c>), normalized to <c>"/segment"</c>. Applied to controller endpoints
/// only — Minimal API <c>Map*</c> routes carry their own groups. Null for none.
/// </param>
public sealed record AnalysisOptions(string? RoutePrefix = null)
{
    public static AnalysisOptions Default { get; } = new();

    /// <summary>
    /// Normalizes a user-typed prefix ("api", "/api/", "api/v1") to "/api" / "/api/v1"; null or
    /// blank (or just slashes) means no prefix.
    /// </summary>
    public static string? NormalizeRoutePrefix(string? raw)
    {
        // Empty inner segments ("api//v1") collapse, as route matching does — otherwise token
        // segment indexes shifted by the prefix would drift by one (v0.14.0 QA finding 7).
        var segments = (raw ?? string.Empty).Trim().Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return segments.Length == 0 ? null : "/" + string.Join('/', segments);
    }
}

/// <summary>A progress report emitted while analyzing, e.g. ("Compiling", 3, 12). Total may be 0 when unknown.</summary>
public sealed record AnalysisProgress(string Stage, int Completed, int Total);
