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
}

/// <summary>
/// One call site's linking outcome and, when linked, the endpoint(s) it hits.
/// <paramref name="ConflictingVerbEndpoints"/> is populated only for
/// <see cref="CallSiteLinkOutcome.VerbMismatch"/> — the endpoint(s) that share this exact
/// skeleton under a different verb, so a disclosure message can name them ("no POST registered;
/// GET /api/OrganizationUsers exists") instead of just saying "no match" — empty otherwise.
/// </summary>
public sealed record CallSiteLinkResult(
    SymbolNode CallSite,
    CallSiteLinkOutcome Outcome,
    IReadOnlyList<SymbolNode> Endpoints,
    IReadOnlyList<SymbolNode> ConflictingVerbEndpoints);

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
    /// The linker's configured base-path prefix (investigation §Q4 of
    /// ts-extractor-investigation.md): <see cref="NodeKind.FrontendCallSite"/>.Name stores the
    /// call site's literal/resolved path with no invented prefix; prepended here before calling
    /// the shipped, unmodified <see cref="RouteTemplate"/>. A single, explicit, project-level
    /// convention — not per-call-site, not inferred (§Q6: multi-base-path config is explicitly
    /// out of v1 scope).
    /// </summary>
    private const string BasePathPrefix = "/api";

    /// <summary>
    /// Links every <see cref="NodeKind.FrontendCallSite"/> node in <paramref name="graph"/>
    /// against every <see cref="NodeKind.Endpoint"/> node in it. Order is undefined only in the
    /// sense that ties are broken deterministically by node id (SymbolNode's own ordinal-stable
    /// identity), never by insertion order — a `link` run on the same graph is idempotent by
    /// construction (§Q3/§Q6).
    /// </summary>
    public static IReadOnlyList<CallSiteLinkResult> Link(CodeGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var endpoints = graph.Nodes.Where(n => n.Kind == NodeKind.Endpoint).ToList();
        return graph.Nodes
            .Where(n => n.Kind == NodeKind.FrontendCallSite)
            .OrderBy(n => n.Id, StringComparer.Ordinal)
            .Select(callSite => LinkOne(callSite, endpoints))
            .ToList();
    }

    /// <summary>Flattens link results into the edges a `link` run should write.</summary>
    public static IReadOnlyList<RelationshipEdge> ToEdges(IEnumerable<CallSiteLinkResult> results) =>
        results
            .SelectMany(r => r.Endpoints.Select(e => new RelationshipEdge(r.CallSite.Id, e.Id, RelationshipKind.CallsEndpoint)))
            .ToList();

    private static CallSiteLinkResult LinkOne(SymbolNode callSite, IReadOnlyList<SymbolNode> endpoints)
    {
        string verb = VerbOf(callSite.Fqn);
        if (verb == UnknownVerb)
        {
            return new CallSiteLinkResult(callSite, CallSiteLinkOutcome.UnknownVerb, [], []);
        }

        string skeleton = RouteTemplate.Normalize(BasePathPrefix + callSite.Name);
        var sameVerbMatches = endpoints
            .Where(e => VerbOf(e.Fqn) == verb && RouteTemplate.Matches(RouteTemplate.Normalize(e.Name), skeleton))
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .ToList();

        if (sameVerbMatches.Count == 0)
        {
            var otherVerbMatches = endpoints
                .Where(e => RouteTemplate.Matches(RouteTemplate.Normalize(e.Name), skeleton))
                .OrderBy(e => e.Id, StringComparer.Ordinal)
                .ToList();
            var outcome = otherVerbMatches.Count > 0 ? CallSiteLinkOutcome.VerbMismatch : CallSiteLinkOutcome.NoSkeletonMatch;
            return new CallSiteLinkResult(callSite, outcome, [], otherVerbMatches);
        }

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
