using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Slnmap.Core.Analysis;

namespace Slnmap.Analysis;

/// <summary>The outcome of a warm re-analysis attempt.</summary>
/// <param name="Snapshot">The updated snapshot, or null when <paramref name="RequiresReload"/>.</param>
/// <param name="RequiresReload">
/// True when a changed path is not a document of the warm solution (added/removed/renamed file,
/// or a structural file) — glob semantics make in-snapshot membership a guess, and we don't
/// guess: the caller performs a full <see cref="ResidentAnalyzer.ReloadAsync"/> instead.
/// </param>
public sealed record WarmAnalysisResult(AnalysisSnapshot? Snapshot, bool RequiresReload);

/// <summary>
/// The resident analysis session behind `slnmap watch`: the MSBuildWorkspace is opened ONCE and
/// kept warm; file changes are applied to the immutable Solution snapshot via WithDocumentText,
/// so Roslyn reuses every untouched compilation. Measured on the fixture solution
/// (reports/watch-mode-investigation.md): the workspace open costs ~4.6s and dominates today's
/// run-and-exit incremental path (~5.1s); the warm update path costs ~0.2s — the difference IS
/// the watch feature. The analysis itself is <see cref="SolutionAnalysisEngine"/>, identical to
/// the cold path, and the hash-driven incremental plan guarantees warm == cold results (pinned by
/// ResidentAnalyzerTests).
/// </summary>
public sealed class ResidentAnalyzer : IDisposable
{
    private readonly Action<string>? _warningSink;
    private MSBuildWorkspace? _workspace;
    private Solution? _solution;
    private string? _solutionPath;

    public ResidentAnalyzer(Action<string>? warningSink = null) => _warningSink = warningSink;

    /// <summary>The snapshot produced by the most recent analysis, warm or cold.</summary>
    public AnalysisSnapshot? Current { get; private set; }

    /// <summary>Opens the workspace and runs the initial analysis (incremental when <paramref name="previous"/> is a same-version baseline).</summary>
    public async Task<AnalysisSnapshot> InitializeAsync(
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

        _solutionPath = solutionPath;
        _workspace?.Dispose();
        _workspace = MSBuildWorkspace.Create();
        _solution = await SolutionAnalysisEngine.OpenAsync(_workspace, solutionPath, _warningSink, progress, cancellationToken).ConfigureAwait(false);
        Current = await SolutionAnalysisEngine.AnalyzeAsync(_solution, previous, _warningSink, progress, cancellationToken).ConfigureAwait(false);
        return Current;
    }

    /// <summary>
    /// Applies content changes for <paramref name="changedPaths"/> to the warm solution and
    /// re-analyzes incrementally. Every changed file is re-read from DISK (the watcher's event is
    /// a signal, the file is the truth); the engine re-hashes, so spurious events fall out as
    /// no-op plans. Returns <see cref="WarmAnalysisResult.RequiresReload"/> when any path is not
    /// a known document of the warm snapshot.
    /// </summary>
    public async Task<WarmAnalysisResult> ReanalyzeChangedAsync(
        IReadOnlyCollection<string> changedPaths,
        CancellationToken cancellationToken = default)
    {
        if (_solution is null || Current is null)
        {
            throw new InvalidOperationException("InitializeAsync must complete before warm re-analysis.");
        }

        var solution = _solution;
        foreach (string path in changedPaths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var documentIds = solution.GetDocumentIdsWithFilePath(path);
            if (documentIds.IsDefaultOrEmpty)
            {
                return new WarmAnalysisResult(null, RequiresReload: true);
            }

            if (await ReadTextWithRetryAsync(path, cancellationToken).ConfigureAwait(false) is not { } text)
            {
                // The file vanished between the event and the read — a delete/rename in flight.
                return new WarmAnalysisResult(null, RequiresReload: true);
            }

            foreach (var documentId in documentIds)
            {
                solution = solution.WithDocumentText(documentId, text);
            }
        }

        var snapshot = await SolutionAnalysisEngine.AnalyzeAsync(solution, Current, _warningSink, progress: null, cancellationToken).ConfigureAwait(false);
        _solution = solution;
        Current = snapshot;
        return new WarmAnalysisResult(snapshot, RequiresReload: false);
    }

    /// <summary>
    /// Full workspace reload for structural changes (files added/removed, project files edited).
    /// Still incremental in effect: the engine plans against <see cref="Current"/>'s hashes, so
    /// unchanged documents are not re-walked — only the workspace OPEN is paid again.
    /// </summary>
    public Task<AnalysisSnapshot> ReloadAsync(IProgress<AnalysisProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (_solutionPath is null)
        {
            throw new InvalidOperationException("InitializeAsync must complete before a reload.");
        }

        return InitializeAsync(_solutionPath, Current, progress, cancellationToken);
    }

    public void Dispose() => _workspace?.Dispose();

    /// <summary>
    /// Editors write files in bursts; a save can hold the file locked for a moment after the
    /// change event fires. A few short retries absorb that; a still-unreadable file is treated
    /// as in-flight structural change (null → reload).
    /// </summary>
    private static async Task<SourceText?> ReadTextWithRetryAsync(string path, CancellationToken cancellationToken)
    {
        const int attempts = 5;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return SourceText.From(stream);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (IOException) when (attempt < attempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(40 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
