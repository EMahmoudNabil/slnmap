using System.Globalization;
using System.Text;
using Slnmap.Core.Graph;
using Slnmap.Core.Storage;

namespace Slnmap.Mcp;

public sealed partial class SlnmapQueries
{
    private const int EndpointListCap = 100;
    private const int EndpointFindCap = 25;
    private static readonly string[] KnownVerbs = ["GET", "POST", "PUT", "DELETE", "PATCH"];

    /// <summary>
    /// Lists Endpoint nodes grouped by project, each with its handler and registration file:line,
    /// optionally filtered by HTTP verb and/or route prefix. Appends the analysis's
    /// unresolved-registration count when non-zero, so coverage is never overstated.
    /// </summary>
    public async Task<string> ListEndpointsAsync(string? verb, string? prefix, CancellationToken cancellationToken = default)
    {
        if (await NotAnalyzedAsync(cancellationToken).ConfigureAwait(false) is { } notReady)
        {
            return notReady;
        }

        string? verbFilter = null;
        if (!string.IsNullOrWhiteSpace(verb))
        {
            verbFilter = verb.Trim().ToUpperInvariant();
            if (!KnownVerbs.Contains(verbFilter, StringComparer.Ordinal))
            {
                return ToolFailure.InvalidParameter(
                    "verb",
                    ["verb", "prefix"],
                    $"Unknown verb '{verb}'. Valid verbs: {string.Join(", ", KnownVerbs)}.");
            }
        }

        var endpoints = await _store.GetNodesByKindAsync(NodeKind.Endpoint, cancellationToken).ConfigureAwait(false);
        if (endpoints.Count == 0)
        {
            return await NoEndpointsMessageAsync(cancellationToken).ConfigureAwait(false);
        }

        var filtered = endpoints
            .Where(e => verbFilter is null || VerbOf(e).Equals(verbFilter, StringComparison.Ordinal))
            .Where(e => prefix is null || string.IsNullOrWhiteSpace(prefix)
                || RouteTemplate.Normalize(e.Name).StartsWith(RouteTemplate.Normalize(prefix), StringComparison.Ordinal))
            .OrderBy(e => e.Name, StringComparer.Ordinal)
            .ThenBy(e => VerbOf(e), StringComparer.Ordinal)
            .ToList();

        string filterNote = (verbFilter, prefix) switch
        {
            (null, null or "") => string.Empty,
            (not null, null or "") => $" with verb {verbFilter}",
            (null, _) => $" under '{prefix}'",
            _ => $" with verb {verbFilter} under '{prefix}'",
        };
        if (filtered.Count == 0)
        {
            return $"0 of {endpoints.Count} endpoint(s) match{filterNote}. "
                + "The prefix is compared against the composed route template (e.g. '/api/vendors'); call list_endpoints without filters to see everything.";
        }

        var builder = new StringBuilder();
        builder.AppendLine($"{filtered.Count} endpoint(s){filterNote}:");
        await AppendEndpointLinesAsync(builder, filtered.Take(EndpointListCap).ToList(), cancellationToken).ConfigureAwait(false);
        if (filtered.Count > EndpointListCap)
        {
            builder.AppendLine($"  ...and {filtered.Count - EndpointListCap} more — filter by prefix or verb.");
        }

        await AppendUnresolvedNoteAsync(builder, cancellationToken).ConfigureAwait(false);
        return builder.ToString().TrimEnd();
    }

    /// <summary>
    /// Finds endpoints whose route template matches <paramref name="route"/> — exact, or by the
    /// framework's own matching semantics (case-insensitive; a {param} hole binds a concrete
    /// segment, in either direction). Optionally narrowed by verb.
    /// </summary>
    public async Task<string> FindEndpointAsync(string route, string? verb, CancellationToken cancellationToken = default)
    {
        if (await NotAnalyzedAsync(cancellationToken).ConfigureAwait(false) is { } notReady)
        {
            return notReady;
        }

        if (string.IsNullOrWhiteSpace(route))
        {
            return ToolFailure.MissingParameter(
                "route",
                ["route", "verb"],
                "Provide a route in 'route', e.g. {\"route\": \"/api/vendors/{id}\"} — a concrete path like \"/api/vendors/42\" also matches its template.");
        }

        string? verbFilter = null;
        if (!string.IsNullOrWhiteSpace(verb))
        {
            verbFilter = verb.Trim().ToUpperInvariant();
            if (!KnownVerbs.Contains(verbFilter, StringComparer.Ordinal))
            {
                return ToolFailure.InvalidParameter(
                    "verb",
                    ["route", "verb"],
                    $"Unknown verb '{verb}'. Valid verbs: {string.Join(", ", KnownVerbs)}.");
            }
        }

        var endpoints = await _store.GetNodesByKindAsync(NodeKind.Endpoint, cancellationToken).ConfigureAwait(false);
        if (endpoints.Count == 0)
        {
            return await NoEndpointsMessageAsync(cancellationToken).ConfigureAwait(false);
        }

        string normalizedQuery = RouteTemplate.Normalize(route);
        var matches = endpoints
            .Where(e => verbFilter is null || VerbOf(e).Equals(verbFilter, StringComparison.Ordinal))
            .Where(e => RouteTemplate.Matches(RouteTemplate.Normalize(e.Name), normalizedQuery))
            .OrderBy(e => e.Fqn, StringComparer.Ordinal)
            .ToList();

        var builder = new StringBuilder();
        if (matches.Count == 0)
        {
            string verbNote = verbFilter is null ? string.Empty : $" with verb {verbFilter}";
            builder.AppendLine($"No endpoint matches '{route}'{verbNote} (compared case-insensitively, {{param}} holes match concrete segments).");
            var near = NearMisses(endpoints, normalizedQuery);
            if (near.Count > 0)
            {
                builder.AppendLine("Did you mean:");
                await AppendEndpointLinesAsync(builder, near, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                builder.AppendLine("Use list_endpoints to browse all routes, optionally filtered by prefix.");
            }

            return builder.ToString().TrimEnd();
        }

        builder.AppendLine($"{matches.Count} endpoint(s) match '{route}':");
        await AppendEndpointLinesAsync(builder, matches.Take(EndpointFindCap).ToList(), cancellationToken, includeFrontendCallers: true).ConfigureAwait(false);
        if (matches.Count > EndpointFindCap)
        {
            builder.AppendLine($"  ...and {matches.Count - EndpointFindCap} more — give a more specific route or a verb.");
        }

        return builder.ToString().TrimEnd();
    }

    private async Task<string> NoEndpointsMessageAsync(CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.Append(
            "The graph contains no Endpoint nodes. Endpoints are extracted at analyze time from ASP.NET Core "
            + "Minimal API registrations (MapGet/MapPost/...) and attribute-routed controllers ([Route]/[HttpGet]) — "
            + "re-run 'slnmap analyze' with a current slnmap if this graph predates them.");
        var meta = await _store.GetMetaAsync(cancellationToken).ConfigureAwait(false);
        if (meta.TryGetValue(MetaKeys.ConventionalControllers, out var raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int conventional)
            && conventional > 0)
        {
            builder.Append(
                $" Note: this solution has {conventional} conventionally-routed controller(s) (no route attributes; "
                + "routed by MapControllerRoute patterns) — those are a different routing system and are not modeled.");
        }

        return builder.ToString();
    }

    /// <summary>The verb is the FQN's first token — "GET /api/vendors" → "GET" (design §2.3: no extra columns).</summary>
    private static string VerbOf(SymbolNode endpoint)
    {
        int space = endpoint.Fqn.IndexOf(' ', StringComparison.Ordinal);
        return space > 0 ? endpoint.Fqn[..space] : endpoint.Fqn;
    }

    /// <summary>
    /// Renders endpoints grouped by owning project as "VERB template → handler — file:line".
    /// <paramref name="includeFrontendCallers"/> (find_endpoint only — list_endpoints stays as
    /// dense as it already is) appends a "Called from the frontend by:" line naming every
    /// FrontendCallSite with an incoming CallsEndpoint edge, per
    /// cross-stack-linker-implementation.md Part 3.3.
    /// </summary>
    private async Task AppendEndpointLinesAsync(
        StringBuilder builder, IReadOnlyList<SymbolNode> endpoints, CancellationToken cancellationToken, bool includeFrontendCallers = false)
    {
        var attributor = ProjectAttributor.From(
            await _store.GetNodesByKindAsync(NodeKind.Project, cancellationToken).ConfigureAwait(false));
        var resolver = new LineResolver();

        foreach (var group in endpoints
            .GroupBy(e => attributor.ProjectOf(e.FilePath) ?? "(unknown project)")
            .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            builder.AppendLine($"{group.Key}:");
            foreach (var endpoint in group)
            {
                var handlerEdges = await _store.GetEdgesAsync(endpoint.Id, EdgeDirection.Outgoing, RelationshipKind.HandledBy, cancellationToken).ConfigureAwait(false);
                var handlers = await _store.GetNodesByIdsAsync(handlerEdges.Select(e => e.TargetId), cancellationToken).ConfigureAwait(false);
                string handlerLabel = handlers.Count switch
                {
                    0 => "(no resolvable handler — lambda or local function)",
                    1 => handlers[0].Fqn,
                    _ => string.Join(" | ", handlers.Select(h => h.Fqn).OrderBy(f => f, StringComparer.Ordinal)),
                };
                string location = endpoint.FilePath is { } file
                    ? $" — {file}:{resolver.LineOf(file, endpoint.Span?.Start ?? 0)}"
                    : string.Empty;
                builder.AppendLine($"  {endpoint.Fqn} → {handlerLabel}{location}");

                if (includeFrontendCallers)
                {
                    var callerEdges = await _store.GetEdgesAsync(endpoint.Id, EdgeDirection.Incoming, RelationshipKind.CallsEndpoint, cancellationToken).ConfigureAwait(false);
                    if (callerEdges.Count > 0)
                    {
                        var callers = await _store.GetNodesByIdsAsync(callerEdges.Select(e => e.SourceId), cancellationToken).ConfigureAwait(false);
                        string callerList = string.Join(", ", callers.Select(c => c.Fqn).OrderBy(f => f, StringComparer.Ordinal));
                        builder.AppendLine($"    Called from the frontend by: {callerList}");
                    }
                }
            }
        }
    }

    private async Task AppendUnresolvedNoteAsync(StringBuilder builder, CancellationToken cancellationToken)
    {
        var meta = await _store.GetMetaAsync(cancellationToken).ConfigureAwait(false);
        if (meta.TryGetValue(MetaKeys.UnresolvedEndpoints, out var raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int unresolved)
            && unresolved > 0)
        {
            builder.AppendLine($"note: {unresolved} registration(s) could not be resolved statically and are not listed — 'slnmap analyze --verbose' prints each with its location and reason.");
        }

        if (meta.TryGetValue(MetaKeys.ConventionalControllers, out var conventionalRaw)
            && int.TryParse(conventionalRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int conventional)
            && conventional > 0)
        {
            builder.AppendLine($"note: {conventional} conventionally-routed controller(s) (no route attributes) are not modeled — a different routing system, not an extraction failure.");
        }
    }

    /// <summary>Closest templates by shared trailing segment, so a near-miss query gets pointed somewhere real.</summary>
    private static List<SymbolNode> NearMisses(IReadOnlyList<SymbolNode> endpoints, string normalizedQuery)
    {
        string lastSegment = normalizedQuery.Split('/').LastOrDefault(s => s.Length > 0 && s != "{x}") ?? string.Empty;
        if (lastSegment.Length == 0)
        {
            return [];
        }

        return endpoints
            .Where(e => RouteTemplate.Normalize(e.Name).Contains(lastSegment, StringComparison.Ordinal))
            .OrderBy(e => e.Name.Length)
            .ThenBy(e => e.Fqn, StringComparer.Ordinal)
            .Take(SuggestionCap)
            .ToList();
    }
}
