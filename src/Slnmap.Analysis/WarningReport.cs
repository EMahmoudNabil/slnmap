using System.Text.RegularExpressions;

namespace Slnmap.Analysis;

/// <summary>
/// Collects the warning messages emitted during analysis and renders them for the CLI:
/// a single counts-first summary line by default, or a grouped breakdown under <c>--verbose</c>.
/// </summary>
/// <remarks>
/// The messages arrive as raw MSBuild workspace diagnostics. Two transformations make them useful:
/// (1) the misleading <c>"Msbuild failed when processing the file '…' with message: "</c> wrapper is
/// stripped — analysis did not fail, a design-time build merely reported a warning; (2) NuGet audit
/// (package vulnerability) warnings are recognized and grouped by package, since the same advisory is
/// otherwise repeated per project and a package with several advisories repeats per advisory.
/// Thread-safe: document-level analysis reports warnings from parallel workers.
/// </remarks>
public sealed partial class WarningReport
{
    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];

    /// <summary>Records one raw warning message (from the analyzer's warning sink).</summary>
    public void Add(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var entry = Parse(message);
        lock (_gate)
        {
            _entries.Add(entry);
        }
    }

    /// <summary>Total number of warnings recorded — the machine-facing count shown in the results block.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// Number of distinct warnings, keyed by the project-stripped message body: the same warning
    /// repeated across projects collapses to one, while two different advisories for the same package
    /// (different severity/URL, hence different body) count separately.
    /// </summary>
    public int UniqueCount
    {
        get
        {
            lock (_gate)
            {
                return DistinctBodies();
            }
        }
    }

    public bool HasWarnings => Count > 0;

    /// <summary>
    /// The one-line summary printed to stderr before the results block, e.g.
    /// <c>"Warnings: 5 (2 unique) — run with --verbose for details."</c>. The hint is omitted when
    /// <paramref name="includeVerboseHint"/> is false (the caller is already printing the detail).
    /// </summary>
    public string SummaryLine(bool includeVerboseHint = true)
    {
        int count;
        int unique;
        lock (_gate)
        {
            // Snapshot both under one lock so the pair is always internally consistent.
            count = _entries.Count;
            unique = DistinctBodies();
        }

        string hint = includeVerboseHint ? " — run with --verbose for details" : string.Empty;
        return $"Warnings: {count} ({unique} unique){hint}.";
    }

    private int DistinctBodies() => _entries.Select(static e => e.Body).Distinct(StringComparer.Ordinal).Count();

    /// <summary>
    /// The grouped, deduplicated warning lines for <c>--verbose</c>. Audit warnings are grouped by
    /// package (advisory count, severities and URLs together, affected projects listed once); every
    /// other diagnostic is listed once with its affected projects. Each line is prefixed
    /// <c>"workspace warning:"</c> — these are warnings a design-time build reported, not load failures.
    /// </summary>
    public IReadOnlyList<string> RenderVerbose()
    {
        List<Entry> snapshot;
        lock (_gate)
        {
            snapshot = [.. _entries];
        }

        var lines = new List<string>();

        // Audit warnings, grouped by (package, version).
        var auditGroups = snapshot
            .Where(static e => e.Audit is not null)
            .GroupBy(static e => (e.Audit!.Package, e.Audit.Version))
            .OrderBy(static g => g.Key.Package, StringComparer.Ordinal)
            .ThenBy(static g => g.Key.Version, StringComparer.Ordinal);
        foreach (var group in auditGroups)
        {
            var advisories = group
                .Select(static e => e.Audit!)
                // Advisories are identified by URL; those without one fall back to severity, tagged so a
                // URL string can never collide with a severity word (e.g. an advisory literally at "low").
                .DistinctBy(static a => a.Url is { } url ? $"url:{url}" : $"sev:{a.Severity}", StringComparer.Ordinal)
                .ToList();
            var severities = advisories
                .Select(static a => a.Severity)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(SeverityRank)
                .ThenBy(static s => s, StringComparer.Ordinal);
            var urls = advisories
                .Select(static a => a.Url)
                .Where(static u => u is not null)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static u => u, StringComparer.Ordinal);
            string noun = advisories.Count == 1 ? "vulnerability" : "vulnerabilities";
            string urlPart = urls.Any() ? $": {string.Join(", ", urls)}" : string.Empty;
            lines.Add(
                $"workspace warning: Package {group.Key.Package} {group.Key.Version} — " +
                $"{advisories.Count} known {noun} ({string.Join(", ", severities)}){urlPart}");
            AppendProjects(lines, group);
        }

        // Everything else: distinct message, affected projects once.
        var otherGroups = snapshot
            .Where(static e => e.Audit is null)
            .GroupBy(static e => e.Body, StringComparer.Ordinal)
            .OrderBy(static g => g.Key, StringComparer.Ordinal);
        foreach (var group in otherGroups)
        {
            lines.Add($"workspace warning: {group.Key}");
            AppendProjects(lines, group);
        }

        return lines;
    }

    private static void AppendProjects(List<string> lines, IEnumerable<Entry> group)
    {
        var projects = group
            .Select(static e => e.Project)
            .Where(static p => p is not null)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static p => p, StringComparer.Ordinal)
            .ToList();
        if (projects.Count > 0)
        {
            lines.Add($"  affected projects: {string.Join(", ", projects)}");
        }
    }

    /// <summary>
    /// True for a diagnostic that only reports a project MSBuildWorkspace cannot map to a language
    /// (e.g. a <c>.dcproj</c> docker-compose project). There is no C# to analyze, so it is dropped
    /// entirely rather than surfaced as a warning.
    /// </summary>
    public static bool IsNonLanguageProjectDiagnostic(string message) =>
        message is not null && message.Contains("is not associated with a language", StringComparison.Ordinal);

    private static Entry Parse(string message)
    {
        string body = message;
        string? project = null;

        var wrapper = WrapperPattern().Match(message);
        if (wrapper.Success)
        {
            project = ProjectName(wrapper.Groups["path"].Value);
            body = wrapper.Groups["body"].Value;
        }

        Audit? audit = null;
        var vuln = AuditPattern().Match(body);
        if (vuln.Success)
        {
            audit = new Audit(
                vuln.Groups["pkg"].Value,
                vuln.Groups["ver"].Value,
                vuln.Groups["sev"].Value,
                vuln.Groups["url"].Success ? vuln.Groups["url"].Value : null);
        }

        return new Entry(body, project, audit);
    }

    private static string? ProjectName(string path)
    {
        // MSBuild embeds Windows paths verbatim, so a diagnostic parsed on Linux still contains '\'.
        // Split on both separators rather than Path.* (which honors only the running OS's separator),
        // then strip the extension.
        int cut = path.LastIndexOfAny(['/', '\\']);
        string fileName = cut >= 0 ? path[(cut + 1)..] : path;
        int dot = fileName.LastIndexOf('.');
        string name = dot > 0 ? fileName[..dot] : fileName;
        return string.IsNullOrEmpty(name) ? null : name;
    }

    private static int SeverityRank(string severity) => severity.ToLowerInvariant() switch
    {
        "critical" => 4,
        "high" => 3,
        "moderate" => 2,
        "low" => 1,
        _ => 0,
    };

    // "Msbuild failed when processing the file '<path>' with message: <body>"
    [GeneratedRegex(@"^Msbuild failed when processing the file '(?<path>.+?)' with message: (?<body>.+)$", RegexOptions.Singleline)]
    private static partial Regex WrapperPattern();

    // "Package '<pkg>' <ver> has a known <sev> severity vulnerability, <url>"
    [GeneratedRegex(@"^Package '(?<pkg>[^']+)' (?<ver>\S+) has a known (?<sev>\w+) severity vulnerability(?:, (?<url>\S+?))?\.?$")]
    private static partial Regex AuditPattern();

    private sealed record Entry(string Body, string? Project, Audit? Audit);

    private sealed record Audit(string Package, string Version, string Severity, string? Url);
}
