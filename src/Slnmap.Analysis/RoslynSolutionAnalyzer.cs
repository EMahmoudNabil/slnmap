using Microsoft.CodeAnalysis.MSBuild;
using Slnmap.Core.Analysis;

namespace Slnmap.Analysis;

/// <summary>
/// Analyzes a solution with Roslyn's <see cref="MSBuildWorkspace"/> and produces a code graph —
/// the run-and-exit path: open a fresh workspace, analyze once, dispose. The analysis itself
/// lives in <see cref="SolutionAnalysisEngine"/>, shared with the resident
/// (<see cref="ResidentAnalyzer"/>) watch path. Roslyn types stay inside this assembly; only
/// Slnmap.Core types cross the boundary.
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
        var solution = await SolutionAnalysisEngine.OpenAsync(workspace, solutionPath, _warningSink, progress, cancellationToken).ConfigureAwait(false);
        return await SolutionAnalysisEngine.AnalyzeAsync(solution, previous, _warningSink, progress, cancellationToken).ConfigureAwait(false);
    }
}
