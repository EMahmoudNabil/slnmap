using System.Globalization;
using System.Text;
using Slnmap.Core.Graph;
using Slnmap.Core.Storage;

namespace Slnmap.Mcp;

public sealed partial class SlnmapQueries
{
    private const int CrossStackListCap = 100;

    private static readonly (string Key, CallSiteLinkOutcome Outcome)[] OrphanCategories =
    [
        ("no-match", CallSiteLinkOutcome.NoSkeletonMatch),
        ("verb-mismatch", CallSiteLinkOutcome.VerbMismatch),
        ("verb-unknown", CallSiteLinkOutcome.UnknownVerb),
    ];

    /// <summary>
    /// Builds the minimal graph <see cref="CrossStackLinker"/> needs — just the Endpoint and
    /// FrontendCallSite nodes, no edges — without loading the full (potentially large) graph.
    /// Recomputed live on every call: deliberately NOT read from the stored CallsEndpoint edges
    /// `slnmap link` last wrote, so these two tools can never show a classification that has
    /// gone stale relative to the current node set (cross-stack-linker-implementation.md Part 3).
    /// Only `impact_analysis`/`find_usages` depend on the persisted edges, since those need a
    /// real row to walk via SQL; a flat classification listing has no such requirement.
    /// </summary>
    private async Task<IReadOnlyList<CallSiteLinkResult>> ComputeLiveLinkResultsAsync(CancellationToken cancellationToken)
    {
        var graph = new CodeGraph();
        foreach (var node in await _store.GetNodesByKindAsync(NodeKind.Endpoint, cancellationToken).ConfigureAwait(false))
        {
            graph.AddNode(node);
        }

        foreach (var node in await _store.GetNodesByKindAsync(NodeKind.FrontendCallSite, cancellationToken).ConfigureAwait(false))
        {
            graph.AddNode(node);
        }

        return CrossStackLinker.Link(graph);
    }

    /// <summary>
    /// Appends the note when `slnmap link`'s last stored edges (the ones `impact_analysis`/
    /// `find_usages` actually walk) may be older than the current graph — the listing above this
    /// note is always fresh regardless (§ComputeLiveLinkResultsAsync), so this is about a
    /// DIFFERENT consumer's staleness, honestly scoped as such.
    /// </summary>
    private async Task AppendLinkerStalenessNoteAsync(StringBuilder builder, CancellationToken cancellationToken)
    {
        var meta = await _store.GetMetaAsync(cancellationToken).ConfigureAwait(false);
        if (!meta.ContainsKey(MetaKeys.LinkerLastRun))
        {
            builder.AppendLine("note: 'slnmap link' has never run — impact_analysis/find_usages won't see CallsEndpoint edges yet (this listing is unaffected; it is computed live).");
        }
        else if (meta.TryGetValue(MetaKeys.LastAnalyzed, out var lastAnalyzedRaw)
            && DateTimeOffset.TryParse(lastAnalyzedRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lastAnalyzed)
            && meta.TryGetValue(MetaKeys.LinkerLastRun, out var linkerLastRunRaw)
            && DateTimeOffset.TryParse(linkerLastRunRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var linkerLastRun)
            && lastAnalyzed > linkerLastRun)
        {
            builder.AppendLine("note: the graph changed since the last 'slnmap link' run — impact_analysis/find_usages may be missing recent CallsEndpoint edges (this listing is unaffected; it is computed live).");
        }
    }

    /// <summary>
    /// Frontend call sites with no matching endpoint, grouped by the two-plus-one disclosed
    /// reasons (cross-stack-linker-investigation.md §Q1/§Q2.1): no skeleton match at any verb,
    /// a skeleton match under a different verb (named, with the conflicting endpoint), or an
    /// honestly-unknown verb. Never a guess.
    /// </summary>
    public async Task<string> FindOrphanCallsAsync(string? category, CancellationToken cancellationToken = default)
    {
        if (await NotAnalyzedAsync(cancellationToken).ConfigureAwait(false) is { } notReady)
        {
            return notReady;
        }

        CallSiteLinkOutcome? outcomeFilter = null;
        if (!string.IsNullOrWhiteSpace(category))
        {
            var match = OrphanCategories.FirstOrDefault(c => c.Key.Equals(category, StringComparison.OrdinalIgnoreCase));
            if (match.Key is null)
            {
                return ToolFailure.InvalidParameter(
                    "category",
                    ["category"],
                    $"Unknown category '{category}'. Valid categories: {string.Join(", ", OrphanCategories.Select(c => c.Key))}.");
            }

            outcomeFilter = match.Outcome;
        }

        var results = await ComputeLiveLinkResultsAsync(cancellationToken).ConfigureAwait(false);
        if (results.Count == 0)
        {
            return "The graph contains no FrontendCallSite nodes. Run 'slnmap analyze-ts <frontend-root>' first.";
        }

        var disclosed = results
            .Where(r => r.Outcome is CallSiteLinkOutcome.NoSkeletonMatch or CallSiteLinkOutcome.VerbMismatch or CallSiteLinkOutcome.UnknownVerb)
            .Where(r => outcomeFilter is null || r.Outcome == outcomeFilter)
            .ToList();

        var builder = new StringBuilder();
        if (disclosed.Count == 0)
        {
            builder.AppendLine(outcomeFilter is null
                ? $"0 orphaned call sites — all {results.Count} link to at least one endpoint."
                : $"0 call sites in category '{category}'.");
            await AppendLinkerStalenessNoteAsync(builder, cancellationToken).ConfigureAwait(false);
            return builder.ToString().TrimEnd();
        }

        builder.AppendLine($"{disclosed.Count} orphaned call site(s){(outcomeFilter is null ? string.Empty : $" ({category})")}:");
        foreach (var group in disclosed.GroupBy(r => r.Outcome).OrderBy(g => g.Key.ToString(), StringComparer.Ordinal))
        {
            string categoryKey = OrphanCategories.First(c => c.Outcome == group.Key).Key;
            builder.AppendLine($"  {categoryKey} ({group.Count()}):");
            foreach (var result in group.OrderBy(r => r.CallSite.Fqn, StringComparer.Ordinal).Take(CrossStackListCap))
            {
                string conflict = result.ConflictingVerbEndpoints.Count > 0
                    ? $" — no {VerbOfCallSite(result.CallSite)} registered; " + string.Join(", ", result.ConflictingVerbEndpoints.Select(e => e.Fqn)) + " exists"
                    : string.Empty;
                builder.AppendLine($"    {result.CallSite.Fqn} ({result.CallSite.Name}){conflict}");
            }
        }

        if (disclosed.Count > CrossStackListCap)
        {
            builder.AppendLine($"  ...and {disclosed.Count - CrossStackListCap} more — filter by category or refine.");
        }

        await AppendLinkerStalenessNoteAsync(builder, cancellationToken).ConfigureAwait(false);
        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Lists frontend HTTP call sites (analyze-ts-verb-report.md §7's named UX gap), each with its
    /// live linking status, optionally filtered by verb and/or route prefix — the same filter
    /// shape as list_endpoints.
    /// </summary>
    public async Task<string> ListFrontendCallSitesAsync(string? verb, string? prefix, CancellationToken cancellationToken = default)
    {
        if (await NotAnalyzedAsync(cancellationToken).ConfigureAwait(false) is { } notReady)
        {
            return notReady;
        }

        string? verbFilter = null;
        if (!string.IsNullOrWhiteSpace(verb))
        {
            verbFilter = verb.Trim().ToUpperInvariant();
        }

        var results = await ComputeLiveLinkResultsAsync(cancellationToken).ConfigureAwait(false);
        if (results.Count == 0)
        {
            return "The graph contains no FrontendCallSite nodes. Run 'slnmap analyze-ts <frontend-root>' first.";
        }

        var filtered = results
            .Where(r => verbFilter is null || VerbOfCallSite(r.CallSite).Equals(verbFilter, StringComparison.Ordinal))
            .Where(r => string.IsNullOrWhiteSpace(prefix)
                || RouteTemplate.Normalize(r.CallSite.Name).StartsWith(RouteTemplate.Normalize(prefix), StringComparison.Ordinal))
            .OrderBy(r => r.CallSite.Fqn, StringComparer.Ordinal)
            .ToList();

        var builder = new StringBuilder();
        if (filtered.Count == 0)
        {
            builder.AppendLine($"0 of {results.Count} call site(s) match. Call list_frontend_callsites without filters to see everything.");
            return builder.ToString().TrimEnd();
        }

        builder.AppendLine($"{filtered.Count} call site(s):");
        foreach (var result in filtered.Take(CrossStackListCap))
        {
            string status = result.Outcome switch
            {
                CallSiteLinkOutcome.Unique or CallSiteLinkOutcome.PrecedenceResolved => $"-> {result.Endpoints[0].Fqn}",
                CallSiteLinkOutcome.SetEdge => $"-> {result.Endpoints.Count} endpoints: " + string.Join(", ", result.Endpoints.Select(e => e.Fqn)),
                _ => $"[{result.Outcome}]",
            };
            builder.AppendLine($"  {result.CallSite.Fqn} ({result.CallSite.Name}) {status}");
        }

        if (filtered.Count > CrossStackListCap)
        {
            builder.AppendLine($"  ...and {filtered.Count - CrossStackListCap} more — filter by verb or prefix.");
        }

        await AppendLinkerStalenessNoteAsync(builder, cancellationToken).ConfigureAwait(false);
        return builder.ToString().TrimEnd();
    }

    private static string VerbOfCallSite(SymbolNode callSite) =>
        callSite.Fqn[..callSite.Fqn.IndexOf(' ', StringComparison.Ordinal)];
}
