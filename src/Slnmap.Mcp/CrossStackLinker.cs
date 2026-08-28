using Slnmap.Core.Graph;

namespace Slnmap.Mcp;

/// <summary>
/// The disclosure taxonomy every <see cref="NodeKind.FrontendCallSite"/> lands in exactly one
/// of, per cross-stack-linker-investigation.md §Q2 (the six named outcomes). Not persisted —
/// this is <see cref="CrossStackLinker"/>'s own computation result, consumed by the `slnmap
/// link` verb's summary and the MCP tools, never written to the graph itself.
/// </summary>
public enum CallSiteLinkOutcome
{
    /// <summary>Exactly one same-verb endpoint skeleton-matches — one edge, no ambiguity.</summary>
    Unique,

    /// <summary>
    /// Several same-verb endpoints skeleton-matched, but the call site's own differing segment
    /// is a concrete literal, so route precedence (literal beats parameter) resolves it to
    /// exactly one edge (§Q2.2, the "row 4" gap, scoped).
    /// </summary>
    PrecedenceResolved,

    /// <summary>
    /// Several same-verb endpoints skeleton-match and precedence cannot disambiguate — either
    /// because the call site's own differing segment is itself a hole (no known runtime value
    /// to apply precedence to), or because two candidates are equally specific. A truthful set
    /// edge to every match, never a guessed single edge (§Q1/§Q2.2) — the same outcome covers
    /// genuine runtime fan-out and irreducible ambiguity; the linker does not distinguish them.
    /// </summary>
    SetEdge,

    /// <summary>No endpoint shares this skeleton at any verb — nothing on the backend resembles this path.</summary>
    NoSkeletonMatch,

    /// <summary>An endpoint shares this exact skeleton, but under a different HTTP verb.</summary>
    VerbMismatch,

    /// <summary>
    /// The call site's own verb is the extractor's honest "can't tell" sentinel (a bare
    /// `fetch(url, { method: computeMethod() })` with a non-literal method — walk.ts's
    /// `resolveFetchVerb`). Never guessed as GET or as whatever verb happens to skeleton-match.
    /// </summary>
    UnknownVerb,

    /// <summary>
    /// v0.13.0: the call site's own template starts with a <c>scheme://</c> prefix (an absolute
    /// URL — the real-world <c>API_ROOT = 'https://host/api'</c> shape) but <see
    /// cref="RouteTemplate.TrySplitAbsoluteUrl"/> could not cleanly isolate its host (e.g. an
    /// empty host, <c>https:///foo</c>). Never guessed at either way — not matched against any
    /// endpoint, disclosed via <see cref="CallSiteLinkResult.AmbiguityReason"/> instead.
    /// </summary>
    AmbiguousHost,
}

/// <summary>
/// One call site's linking outcome and, when linked, the endpoint(s) it hits.
/// <paramref name="ConflictingVerbEndpoints"/> is populated only for
/// <see cref="CallSiteLinkOutcome.VerbMismatch"/> — the endpoint(s) that share this exact
/// skeleton under a different verb, so a disclosure message can name them ("no POST registered;
/// GET /api/OrganizationUsers exists") instead of just saying "no match" — empty otherwise.
/// <paramref name="AmbiguityReason"/> is populated only for the base-path double-match case of
/// <see cref="CallSiteLinkOutcome.SetEdge"/> (v0.12.3) — distinguishes "this call site's raw
/// path AND its base-path-prefixed path both independently matched a real endpoint" from
/// genuine runtime fan-out/irreducible ambiguity, which leaves this null. Null for every other
/// outcome, including the ordinary SetEdge case.
/// <paramref name="Host"/> is populated (v0.13.0) whenever the call site's own template was a
/// well-formed absolute URL — regardless of match outcome, and regardless of whether that host
/// has anything to do with the analyzed backend. A call site to a genuinely different, external
/// API still links purely by path when its path skeleton-matches; the host is carried here so a
/// human can see it was external, not silently hidden. Null for every ordinary relative-path call
/// site (the overwhelming majority).
/// </summary>
public sealed record CallSiteLinkResult(
    SymbolNode CallSite,
    CallSiteLinkOutcome Outcome,
    IReadOnlyList<SymbolNode> Endpoints,
    IReadOnlyList<SymbolNode> ConflictingVerbEndpoints,
    string? AmbiguityReason = null,
    string? Host = null);

/// <summary>
/// Phase 3: joins <see cref="NodeKind.FrontendCallSite"/> nodes to <see cref="NodeKind.Endpoint"/>
/// nodes already in the graph, per cross-stack-linker-investigation.md — a fully reviewed design,
/// executed here without re-deciding. Pure and stateless: given a graph, produces the linking
/// outcome for every call site; callers (the `slnmap link` verb, the MCP query tools) decide what
/// to do with the result (write <see cref="RelationshipKind.CallsEndpoint"/> edges, print a
/// summary, render a listing). No I/O here.
///
/// Lives alongside <see cref="RouteTemplate"/> rather than in Slnmap.Analysis (where
/// EndpointFacts/TsArtifactFacts live) specifically to avoid a circular project reference: this
/// class calls RouteTemplate directly (same assembly, no InternalsVisibleTo needed), and the new
/// MCP query tools that consume its results already live in this project too — Slnmap.Mcp only
/// references Slnmap.Core, so keeping both linker-adjacent pieces here needs zero new
/// ProjectReferences in either direction.
/// </summary>
public static class CrossStackLinker
{
    /// <summary>
    /// The extractor's honest "the real verb is genuinely unknown" sentinel
    /// (`slnmap-ts/src/walk.ts` `resolveFetchVerb`) — never guessed at, never linked.
    /// </summary>
    private const string UnknownVerb = "UNKNOWN";

    /// <summary>
    /// The linker's default base-path prefix (investigation §Q4 of
    /// ts-extractor-investigation.md): <see cref="NodeKind.FrontendCallSite"/>.Name stores the
    /// call site's literal/resolved path with no invented prefix; prepended before calling the
    /// shipped, unmodified <see cref="RouteTemplate"/>. Configurable per <see cref="Link"/>'s
    /// <c>basePathPrefix</c> parameter (`slnmap link --base-path`, v0.12.3) — this is only the
    /// back-compat default when the caller doesn't override it.
    /// </summary>
    public const string DefaultBasePathPrefix = "/api";

    /// <summary>
    /// Links every <see cref="NodeKind.FrontendCallSite"/> node in <paramref name="graph"/>
    /// against every <see cref="NodeKind.Endpoint"/> node in it. Order is undefined only in the
    /// sense that ties are broken deterministically by node id (SymbolNode's own ordinal-stable
    /// identity), never by insertion order — a `link` run on the same graph is idempotent by
    /// construction (§Q3/§Q6).
    /// </summary>
    public static IReadOnlyList<CallSiteLinkResult> Link(CodeGraph graph, string basePathPrefix = DefaultBasePathPrefix)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(basePathPrefix);

        var endpoints = graph.Nodes.Where(n => n.Kind == NodeKind.Endpoint).ToList();
        return graph.Nodes
            .Where(n => n.Kind == NodeKind.FrontendCallSite)
            .OrderBy(n => n.Id, StringComparer.Ordinal)
            .Select(callSite => LinkOne(callSite, endpoints, basePathPrefix))
            .ToList();
    }

    /// <summary>Flattens link results into the edges a `link` run should write.</summary>
    public static IReadOnlyList<RelationshipEdge> ToEdges(IEnumerable<CallSiteLinkResult> results) =>
        results
            .SelectMany(r => r.Endpoints.Select(e => new RelationshipEdge(r.CallSite.Id, e.Id, RelationshipKind.CallsEndpoint)))
            .ToList();

    /// <summary>
    /// v0.12.3 fix (reports/link-noskeletonmatch-investigation-report.md): the linker used to
    /// unconditionally match against <c>basePathPrefix + callSite.Name</c> alone. A call site
    /// whose OWN literal already includes the prefix (e.g. a bare `fetch('/api/orders')`, with
    /// no axios `baseURL` absorbing it) got double-prefixed to `/api/api/orders`, which can
    /// never skeleton-match the real `/api/orders` endpoint — <see cref="RouteTemplate.Matches"/>
    /// rejects on segment count before any string comparison. Fixed by trying BOTH the raw path
    /// and the prefixed path as independent candidate skeletons: if exactly one yields same-verb
    /// matches, resolve normally against that one (this is what makes OSSUS's own convention —
    /// axios `baseURL` absorbs the prefix, call sites never carry it literally — keep working
    /// unchanged); if BOTH independently match, that is a genuine new ambiguity (not the same
    /// shape as ordinary fan-out, so disclosed with its own <see cref="CallSiteLinkResult.AmbiguityReason"/>
    /// rather than silently preferring one); if NEITHER matches, <see cref="CallSiteLinkOutcome.NoSkeletonMatch"/>
    /// as before. When <paramref name="basePathPrefix"/> is empty (`--base-path ""`) the two
    /// candidates are identical by construction — collapses to the single-candidate path with no
    /// possibility of a spurious ambiguity verdict.
    /// </summary>
    private static CallSiteLinkResult LinkOne(SymbolNode callSite, IReadOnlyList<SymbolNode> endpoints, string basePathPrefix)
    {
        string verb = VerbOf(callSite.Fqn);
        if (verb == UnknownVerb)
        {
            return new CallSiteLinkResult(callSite, CallSiteLinkOutcome.UnknownVerb, [], []);
        }

        // v0.13.0: an absolute-URL call site (`https://host/api/orders`) is handled entirely
        // separately from the base-path-prefix dance below — it already specifies its own root,
        // so prepending an ASSUMED base path to a fully-qualified URL makes no sense the way it
        // does for a bare relative path. Matched by PATH ONLY; the host is carried on the result
        // regardless of match outcome (CallSiteLinkResult.Host's own doc comment) so a call site
        // that genuinely hits a different, external API still links by path but stays visibly
        // external, never silently treated as if it were this backend's own host.
        var split = RouteTemplate.TrySplitAbsoluteUrl(callSite.Name, out string? host, out string pathOnly);
        if (split == RouteTemplate.AbsoluteUrlSplitResult.Ambiguous)
        {
            return new CallSiteLinkResult(
                callSite, CallSiteLinkOutcome.AmbiguousHost, [], [],
                AmbiguityReason: $"'{callSite.Name}' starts with a scheme (looks like an absolute URL) but its host could not be cleanly separated from its path — not matched against any endpoint");
        }

        if (split == RouteTemplate.AbsoluteUrlSplitResult.Clean)
        {
            string absoluteSkeleton = RouteTemplate.Normalize(pathOnly);
            var sameVerbMatches = MatchSameVerb(endpoints, verb, absoluteSkeleton);
            var result = sameVerbMatches.Count > 0
                ? ResolveForSkeleton(callSite, absoluteSkeleton, sameVerbMatches)
                : NoMatchResult(callSite, absoluteSkeleton, endpoints);
            return result with { Host = host };
        }

        string rawSkeleton = RouteTemplate.Normalize(callSite.Name);
        string prefixedSkeleton = RouteTemplate.Normalize(basePathPrefix + callSite.Name);
        bool singleCandidate = rawSkeleton == prefixedSkeleton;

        var rawMatches = MatchSameVerb(endpoints, verb, rawSkeleton);
        var prefixedMatches = singleCandidate ? rawMatches : MatchSameVerb(endpoints, verb, prefixedSkeleton);

        if (!singleCandidate && rawMatches.Count > 0 && prefixedMatches.Count > 0)
        {
            var combined = rawMatches.Concat(prefixedMatches)
                .GroupBy(e => e.Id, StringComparer.Ordinal)
                .Select(g => g.First())
                .OrderBy(e => e.Id, StringComparer.Ordinal)
                .ToList();
            return new CallSiteLinkResult(
                callSite, CallSiteLinkOutcome.SetEdge, combined, [],
                AmbiguityReason: $"prefix-ambiguous: '{callSite.Name}' matches an endpoint both as-is and with the '{basePathPrefix}' prefix applied — not resolved automatically");
        }

        if (rawMatches.Count > 0)
        {
            return ResolveForSkeleton(callSite, rawSkeleton, rawMatches);
        }

        if (prefixedMatches.Count > 0)
        {
            return ResolveForSkeleton(callSite, prefixedSkeleton, prefixedMatches);
        }

        var otherVerbMatches = MatchAnyVerb(endpoints, rawSkeleton);
        if (!singleCandidate)
        {
            otherVerbMatches = otherVerbMatches
                .Concat(MatchAnyVerb(endpoints, prefixedSkeleton))
                .GroupBy(e => e.Id, StringComparer.Ordinal)
                .Select(g => g.First())
                .OrderBy(e => e.Id, StringComparer.Ordinal)
                .ToList();
        }

        var outcome = otherVerbMatches.Count > 0 ? CallSiteLinkOutcome.VerbMismatch : CallSiteLinkOutcome.NoSkeletonMatch;
        return new CallSiteLinkResult(callSite, outcome, [], otherVerbMatches);
    }

    /// <summary>The single-candidate-skeleton "nothing matched at this verb" tail — used only by
    /// the absolute-URL path above, which (unlike the dual raw/prefixed-candidate path below it)
    /// has exactly one skeleton to check other verbs against.</summary>
    private static CallSiteLinkResult NoMatchResult(SymbolNode callSite, string skeleton, IReadOnlyList<SymbolNode> endpoints)
    {
        var otherVerbMatches = MatchAnyVerb(endpoints, skeleton);
        var outcome = otherVerbMatches.Count > 0 ? CallSiteLinkOutcome.VerbMismatch : CallSiteLinkOutcome.NoSkeletonMatch;
        return new CallSiteLinkResult(callSite, outcome, [], otherVerbMatches);
    }

    private static List<SymbolNode> MatchSameVerb(IReadOnlyList<SymbolNode> endpoints, string verb, string skeleton) =>
        endpoints
            .Where(e => VerbOf(e.Fqn) == verb && RouteTemplate.Matches(RouteTemplate.Normalize(e.Name), skeleton))
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .ToList();

    private static List<SymbolNode> MatchAnyVerb(IReadOnlyList<SymbolNode> endpoints, string skeleton) =>
        endpoints
            .Where(e => RouteTemplate.Matches(RouteTemplate.Normalize(e.Name), skeleton))
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .ToList();

    private static CallSiteLinkResult ResolveForSkeleton(SymbolNode callSite, string skeleton, IReadOnlyList<SymbolNode> sameVerbMatches)
    {
        if (sameVerbMatches.Count == 1)
        {
            return new CallSiteLinkResult(callSite, CallSiteLinkOutcome.Unique, sameVerbMatches, []);
        }

        return TryResolveByLiteralPrecedence(skeleton, sameVerbMatches) is { } winner
            ? new CallSiteLinkResult(callSite, CallSiteLinkOutcome.PrecedenceResolved, [winner], [])
            : new CallSiteLinkResult(callSite, CallSiteLinkOutcome.SetEdge, sameVerbMatches, []);
    }

    /// <summary>
    /// The scoped route-precedence rule (§Q2.2): applies ONLY when the call site's own segment
    /// at every position where the candidates differ is a concrete literal — that literal IS the
    /// value ASP.NET would receive at runtime, so "literal beats parameter" can be legitimately
    /// applied. If the call site's own segment there is itself a hole, no concrete value is
    /// known and precedence has nothing to compare against — this deliberately returns null
    /// (the caller falls through to a set edge), the same outcome as true fan-out. This is NOT a
    /// general ASP.NET route-precedence engine (no catch-all/custom-constraint ranking) — the
    /// measured real data never needed one (investigation §Q6).
    /// </summary>
    private static SymbolNode? TryResolveByLiteralPrecedence(string callSkeleton, IReadOnlyList<SymbolNode> candidates)
    {
        string[] callSegments = callSkeleton.Split('/');
        var withSegments = candidates
            .Select(c => (Node: c, Segments: RouteTemplate.Normalize(c.Name).Split('/')))
            .ToList();

        var diffPositions = Enumerable.Range(0, callSegments.Length)
            .Where(i => withSegments.Select(c => c.Segments[i]).Distinct().Count() > 1)
            .ToList();
        if (diffPositions.Count == 0)
        {
            // All candidates are identical at every segment (e.g. duplicate Endpoint
            // registrations of the same route) -- nothing to disambiguate; a set edge to all of
            // them is correct, since they genuinely are indistinguishable in the graph.
            return null;
        }

        bool callIsLiteralAtEveryDiff = diffPositions.All(i => callSegments[i] != "{x}");
        if (!callIsLiteralAtEveryDiff)
        {
            return null;
        }

        // Score each candidate 0 (literal) / 1 (hole) at each differing position; the candidate
        // with the lexicographically smallest score wins IF it is the unique minimum.
        var scored = withSegments
            .Select(c => (c.Node, ScoreKey: string.Concat(diffPositions.Select(i => c.Segments[i] == "{x}" ? '1' : '0'))))
            .OrderBy(c => c.ScoreKey, StringComparer.Ordinal)
            .ToList();

        var winners = scored.Where(c => c.ScoreKey == scored[0].ScoreKey).ToList();
        return winners.Count == 1 ? winners[0].Node : null;
    }

    /// <summary>The verb is the FQN's first token — "GET /api/vendors" -> "GET" (matches SlnmapQueries.Endpoints.cs's own convention).</summary>
    private static string VerbOf(string fqn) => fqn[..fqn.IndexOf(' ', StringComparison.Ordinal)];
}
