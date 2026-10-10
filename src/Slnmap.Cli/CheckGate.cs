using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Slnmap.Core.Graph;
using Slnmap.Core.Storage;
using Slnmap.Mcp;

namespace Slnmap.Cli;

/// <summary>
/// `slnmap check` (v0.15.0): the findings a CI gate fails on — frontend call sites no endpoint
/// answers, project-level dependency cycles, and projects analyzed without a restore — compared
/// against a committed baseline, so an existing codebase adopts the gate as it is and only NEW
/// findings fail a build. Orphans are computed live, the same way find_orphan_calls does, so the
/// check never depends on a prior `slnmap link`. Pure: the verb in Program.cs does the I/O.
/// </summary>
internal static class CheckGate
{
    public const string DefaultBaselineFile = "slnmap-baseline.json";
    public const int BaselineSchemaVersion = 1;

    public const string OrphansCategory = "orphans";
    public const string CyclesCategory = "cycles";
    public const string UnrestoredCategory = "unrestored";
    public static readonly IReadOnlyList<string> AllCategories = [OrphansCategory, CyclesCategory, UnrestoredCategory];

    /// <summary>
    /// A frontend call site no endpoint answers. <paramref name="Key"/> is what the baseline
    /// stores: verb, file and route template, deliberately without the line number, so moving
    /// code around never turns a known orphan into a new one.
    /// </summary>
    public sealed record Orphan(string Key, string Verb, string File, int Line, string Template, string Category, string Detail);

    /// <summary><paramref name="Orphans"/> is null when the graph has no frontend data at all — nothing to check, not zero orphans.</summary>
    public sealed record Findings(IReadOnlyList<Orphan>? Orphans, IReadOnlyList<string> Cycles, int ProjectsNotRestored);

    public sealed record Baseline(IReadOnlySet<string> Orphans, IReadOnlySet<string> Cycles);

    /// <summary>Resolved counts are baseline entries the graph no longer has — informational, never a failure.</summary>
    public sealed record Comparison(IReadOnlyList<Orphan> NewOrphans, IReadOnlyList<string> NewCycles, int ResolvedOrphans, int ResolvedCycles);

    /// <summary>Thrown for a baseline file that exists but cannot be used; the message is the user-facing text.</summary>
    public sealed class BaselineException(string message) : Exception(message);

    public static Findings Compute(CodeGraph graph, IReadOnlyDictionary<string, string> meta, string? basePathOverride)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(meta);

        IReadOnlyList<Orphan>? orphans = null;
        var callSites = graph.Nodes.Where(static n => n.Kind == NodeKind.FrontendCallSite).ToList();
        if (callSites.Count > 0)
        {
            // The same inputs the MCP tools link with: endpoints + call sites, the base path the
            // last `slnmap link` used unless overridden, and token tolerance when a transformer
            // convention is registered.
            var linkable = new CodeGraph();
            foreach (var node in graph.Nodes.Where(static n => n.Kind is NodeKind.Endpoint or NodeKind.FrontendCallSite))
            {
                linkable.AddNode(node);
            }

            string basePath = basePathOverride
                ?? (meta.TryGetValue(MetaKeys.LinkerBasePathPrefix, out var stored) ? stored : CrossStackLinker.DefaultBasePathPrefix);
            var tolerance = CrossStackLinker.BuildTokenTolerance(graph.Disclosures, graph.Disclosures);
            orphans = CrossStackLinker.Link(linkable, basePath, tolerance)
                .Where(static r => r.Outcome is CallSiteLinkOutcome.NoSkeletonMatch or CallSiteLinkOutcome.VerbMismatch
                    or CallSiteLinkOutcome.UnknownVerb or CallSiteLinkOutcome.AmbiguousHost)
                .Select(ToOrphan)
                .OrderBy(static o => o.Key, StringComparer.Ordinal)
                .ThenBy(static o => o.Line)
                .ToList();
        }

        var cycles = SlnmapQueries.FindCycles(graph, "project")
            .Select(static c => string.Join(" -> ", c.Path))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static c => c, StringComparer.Ordinal)
            .ToList();

        int notRestored = meta.TryGetValue(MetaKeys.ProjectsNotRestored, out var raw)
            && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : 0;

        return new Findings(orphans, cycles, notRestored);
    }

    /// <summary>A call site's FQN is "VERB file:line:column" (TsArtifactFacts.BuildNodes); the key drops the position.</summary>
    public static Orphan ToOrphan(CallSiteLinkResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        string fqn = result.CallSite.Fqn;
        int space = fqn.IndexOf(' ', StringComparison.Ordinal);
        string verb = space > 0 ? fqn[..space] : fqn;
        string location = space > 0 ? fqn[(space + 1)..] : string.Empty;

        string file = location;
        int line = 0;
        int lastColon = location.LastIndexOf(':');
        int secondLastColon = lastColon > 0 ? location.LastIndexOf(':', lastColon - 1) : -1;
        if (secondLastColon > 0
            && int.TryParse(location[(secondLastColon + 1)..lastColon], NumberStyles.None, CultureInfo.InvariantCulture, out int parsedLine)
            && int.TryParse(location[(lastColon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            file = location[..secondLastColon];
            line = parsedLine;
        }

        string category = result.Outcome switch
        {
            CallSiteLinkOutcome.VerbMismatch => "verb-mismatch",
            CallSiteLinkOutcome.UnknownVerb => "verb-unknown",
            CallSiteLinkOutcome.AmbiguousHost => "ambiguous-host",
            _ => "no-match",
        };
        string detail = result.ConflictingVerbEndpoints.Count > 0
            ? $"no {verb} registered; " + string.Join(", ", result.ConflictingVerbEndpoints.Select(static e => e.Fqn)) + " exists"
            : result.AmbiguityReason ?? string.Empty;

        return new Orphan($"{verb} {file} {result.CallSite.Name}", verb, file, line, result.CallSite.Name, category, detail);
    }

    public static Comparison Compare(Findings findings, Baseline? baseline)
    {
        ArgumentNullException.ThrowIfNull(findings);

        var knownOrphans = baseline?.Orphans ?? new HashSet<string>(StringComparer.Ordinal);
        var knownCycles = baseline?.Cycles ?? new HashSet<string>(StringComparer.Ordinal);
        var orphans = findings.Orphans ?? [];

        var newOrphans = orphans.Where(o => !knownOrphans.Contains(o.Key)).ToList();
        var newCycles = findings.Cycles.Where(c => !knownCycles.Contains(c)).ToList();
        // A baseline orphan is "resolved" only when frontend data is present and no longer has
        // it; with no frontend data at all, nothing can be said about it.
        var currentOrphanKeys = orphans.Select(static o => o.Key).ToHashSet(StringComparer.Ordinal);
        int resolvedOrphans = findings.Orphans is null ? 0 : knownOrphans.Count(k => !currentOrphanKeys.Contains(k));
        int resolvedCycles = knownCycles.Count(k => !findings.Cycles.Contains(k, StringComparer.Ordinal));
        return new Comparison(newOrphans, newCycles, resolvedOrphans, resolvedCycles);
    }

    /// <summary>Parses `--fail-on`; returns null and sets <paramref name="error"/> for an unknown category.</summary>
    public static IReadOnlySet<string>? ParseFailOn(string value, out string? error)
    {
        error = null;
        var categories = new HashSet<string>(StringComparer.Ordinal);
        foreach (string part in (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string category = part.ToLowerInvariant();
            if (!AllCategories.Contains(category))
            {
                error = $"Unknown --fail-on category '{part}'. Valid categories: {string.Join(", ", AllCategories)}.";
                return null;
            }

            categories.Add(category);
        }

        return categories;
    }

    public static bool Fails(Findings findings, Comparison comparison, IReadOnlySet<string> failOn)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(failOn);

        return (failOn.Contains(OrphansCategory) && comparison.NewOrphans.Count > 0)
            || (failOn.Contains(CyclesCategory) && comparison.NewCycles.Count > 0)
            || (failOn.Contains(UnrestoredCategory) && findings.ProjectsNotRestored > 0);
    }

    /// <summary>Null when no file exists at <paramref name="path"/>; throws <see cref="BaselineException"/> for one that cannot be used.</summary>
    public static Baseline? Load(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        BaselineDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<BaselineDocument>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException e)
        {
            throw new BaselineException($"The baseline file {path} is not valid JSON: {e.Message}");
        }

        if (document is null)
        {
            throw new BaselineException($"The baseline file {path} is empty.");
        }

        if (document.SchemaVersion != BaselineSchemaVersion)
        {
            throw new BaselineException(
                $"The baseline file {path} has schemaVersion {document.SchemaVersion}; this slnmap reads {BaselineSchemaVersion}. Re-create it with --update-baseline.");
        }

        return new Baseline(
            (document.Orphans ?? []).ToHashSet(StringComparer.Ordinal),
            (document.Cycles ?? []).ToHashSet(StringComparer.Ordinal));
    }

    public static void Save(string path, Findings findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        var document = new BaselineDocument
        {
            SchemaVersion = BaselineSchemaVersion,
            Orphans = (findings.Orphans ?? []).Select(static o => o.Key).Distinct(StringComparer.Ordinal).OrderBy(static k => k, StringComparer.Ordinal).ToList(),
            Cycles = findings.Cycles.OrderBy(static c => c, StringComparer.Ordinal).ToList(),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(document, JsonOptions) + Environment.NewLine);
    }

    /// <summary>
    /// GitHub Actions workflow commands for the new findings, one per line, so a failing check
    /// shows up as annotations on the pull request. File paths are as the frontend analysis
    /// recorded them (relative to the frontend root), which is the repository path when the
    /// frontend is the repository root.
    /// </summary>
    public static IEnumerable<string> GitHubAnnotations(Comparison comparison, Findings findings, IReadOnlySet<string> failOn)
    {
        ArgumentNullException.ThrowIfNull(comparison);
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(failOn);

        if (failOn.Contains(OrphansCategory))
        {
            foreach (var orphan in comparison.NewOrphans)
            {
                // ASCII only: the log viewer is not a terminal.
                string detail = orphan.Detail.Length > 0 ? $": {orphan.Detail}" : string.Empty;
                string location = orphan.Line > 0 ? $"file={EscapeProperty(orphan.File)},line={orphan.Line.ToString(CultureInfo.InvariantCulture)}," : string.Empty;
                yield return $"::error {location}title=slnmap check::New orphan frontend call: {EscapeData(orphan.Key)} ({orphan.Category}{EscapeData(detail)})";
            }
        }

        if (failOn.Contains(CyclesCategory))
        {
            foreach (string cycle in comparison.NewCycles)
            {
                yield return $"::error title=slnmap check::New project dependency cycle: {EscapeData(cycle)}";
            }
        }

        if (failOn.Contains(UnrestoredCategory) && findings.ProjectsNotRestored > 0)
        {
            yield return $"::error title=slnmap check::{findings.ProjectsNotRestored} project(s) were analyzed with missing dependencies; run 'dotnet restore' before 'slnmap analyze'";
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private sealed class BaselineDocument
    {
        [JsonPropertyName("schemaVersion")]
        public int SchemaVersion { get; set; }

        [JsonPropertyName("orphans")]
        public List<string>? Orphans { get; set; }

        [JsonPropertyName("cycles")]
        public List<string>? Cycles { get; set; }
    }

    // Workflow-command escaping, per GitHub's documented rules.
    private static string EscapeData(string value) =>
        value.Replace("%", "%25", StringComparison.Ordinal).Replace("\r", "%0D", StringComparison.Ordinal).Replace("\n", "%0A", StringComparison.Ordinal);

    private static string EscapeProperty(string value) =>
        EscapeData(value).Replace(":", "%3A", StringComparison.Ordinal).Replace(",", "%2C", StringComparison.Ordinal);
}
