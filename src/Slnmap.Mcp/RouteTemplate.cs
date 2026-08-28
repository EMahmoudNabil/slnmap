using System.Text.RegularExpressions;

namespace Slnmap.Mcp;

/// <summary>
/// Query-time route-template normalization and matching — never node identity (endpoints store the
/// template as authored; see the endpoint-nodes design §2.2). The rules are the framework's own
/// semantics, salvaged from the old prototype's PathSkeleton golden suite: ASP.NET matches routes
/// case-insensitively, and a <c>{param}</c> or <c>{param:constraint}</c> hole binds any single
/// concrete segment.
/// </summary>
internal static partial class RouteTemplate
{
    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex RouteParameter();

    [GeneratedRegex(@"/{2,}")]
    private static partial Regex MultipleSlashes();

    /// <summary>Anchor-only check: does <c>route</c> START with a <c>scheme://</c> prefix at all?
    /// Deliberately narrower than "contains '://' anywhere" — a perfectly ordinary relative call
    /// site can carry an embedded absolute URL inside its OWN query string (e.g.
    /// <c>/redirect?to=http://evil.com</c>); that must normalize exactly as it always has, not get
    /// diverted into absolute-URL handling. Only a string that itself BEGINS with a scheme is a
    /// candidate at all.</summary>
    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.\-]*://")]
    private static partial Regex AbsoluteUrlPrefix();

    /// <summary>The full split: scheme + non-empty host (stops at the first <c>/</c>, <c>?</c>, or
    /// <c>#</c>) + everything after. A route whose host would be empty (<c>https:///foo</c>) does
    /// NOT match this — see <see cref="TrySplitAbsoluteUrl"/>.</summary>
    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9+.\-]*://(?<host>[^/?#]+)(?<rest>[/?#].*)?$")]
    private static partial Regex AbsoluteUrl();

    /// <summary>
    /// Outcome of <see cref="TrySplitAbsoluteUrl"/>: whether <c>route</c> is an ordinary relative
    /// path/template (the overwhelming majority — including every backend <see
    /// cref="Slnmap.Core.Graph.NodeKind.Endpoint"/> template, which is never absolute), a
    /// cleanly-split absolute URL, or one that LOOKS like an attempted absolute URL (starts with
    /// <c>scheme://</c>) but whose host could not be cleanly isolated (e.g. an empty host).
    /// </summary>
    public enum AbsoluteUrlSplitResult
    {
        NotAbsolute,
        Clean,
        Ambiguous,
    }

    /// <summary>
    /// Splits a resolved frontend call-site template into its host (when it is a well-formed
    /// absolute URL — the real-world `API_ROOT = 'https://host/api'` shape, v0.13.0) and the
    /// path-and-rest remainder that skeleton-matching should actually operate on. Never guesses:
    /// an ordinary relative path is untouched (<see cref="AbsoluteUrlSplitResult.NotAbsolute"/>,
    /// <paramref name="pathAndRest"/> echoes <paramref name="route"/> verbatim), and a string that
    /// only LOOKS absolute (starts with <c>scheme://</c>) but has an empty or otherwise
    /// unparseable host is reported as <see cref="AbsoluteUrlSplitResult.Ambiguous"/> rather than
    /// silently guessed at either way — <paramref name="host"/> is null and <paramref
    /// name="pathAndRest"/> echoes the original string in that case too, so a caller that ignores
    /// the return value still gets the previous (pre-this-feature) behavior, not a crash or blank.
    /// </summary>
    public static AbsoluteUrlSplitResult TrySplitAbsoluteUrl(string? route, out string? host, out string pathAndRest)
    {
        host = null;
        pathAndRest = route ?? string.Empty;
        if (string.IsNullOrEmpty(route) || !AbsoluteUrlPrefix().IsMatch(route))
        {
            return AbsoluteUrlSplitResult.NotAbsolute;
        }

        var match = AbsoluteUrl().Match(route);
        if (!match.Success || match.Groups["host"].Value.Contains("://", StringComparison.Ordinal))
        {
            // Starts with a scheme-shaped prefix but the host segment is empty (`https:///foo`)
            // or itself contains another `://` (a pathologically-nested case the permissive
            // `[^/?#]+` host class would otherwise silently swallow) — a genuine "looks like a
            // URL but isn't cleanly one" case, not a guess either way.
            return AbsoluteUrlSplitResult.Ambiguous;
        }

        host = match.Groups["host"].Value;
        pathAndRest = match.Groups["rest"].Success ? match.Groups["rest"].Value : string.Empty;
        return AbsoluteUrlSplitResult.Clean;
    }

    /// <summary>
    /// Normalizes a route or template to its comparable skeleton: a leading <c>scheme://host</c>
    /// stripped first when cleanly separable (v0.13.0 — see <see cref="TrySplitAbsoluteUrl"/>;
    /// callers that need the host itself, e.g. <see cref="CrossStackLinker"/>, call that directly
    /// — this method's contract stays "string in, comparable skeleton string out"), then query
    /// string and hash fragment stripped, case-folded, every <c>{...}</c> hole collapsed to
    /// <c>{x}</c>, duplicate slashes collapsed, leading/trailing slashes trimmed. The root route
    /// normalizes to "". (Constraints with braces of their own, e.g. <c>{id:regex(^\d{3}$)}</c>,
    /// are not supported — no such constraint has appeared in the field; only <c>:int</c> has.)
    /// </summary>
    public static string Normalize(string route)
    {
        if (string.IsNullOrWhiteSpace(route))
        {
            return string.Empty;
        }

        if (TrySplitAbsoluteUrl(route, out _, out string pathOnly) == AbsoluteUrlSplitResult.Clean)
        {
            route = pathOnly;
        }

        int query = route.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
        {
            route = route[..query];
        }

        int hash = route.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
        {
            route = route[..hash];
        }

        route = route.ToLowerInvariant();
        route = RouteParameter().Replace(route, "{x}");
        route = MultipleSlashes().Replace(route, "/");
        return route.Trim('/');
    }

    /// <summary>
    /// True when the normalized skeletons describe the same route: equal segment counts, and each
    /// segment pair is equal or either side is a <c>{x}</c> hole (a hole matches a hole or a
    /// concrete segment — so a query may use a concrete value where the template has a parameter,
    /// and vice versa).
    ///
    /// v0.12.1 fix (a real, field-trial-found false-ambiguity bug, cross-stack-linker-v0121-fix
    /// report): a hole absorbing the OTHER side's literal is only trustworthy when it happens in
    /// ONE direction for the whole comparison. Two segments each independently excusing a
    /// mismatch — template's hole absorbing the query's literal at one position, AND the query's
    /// hole absorbing the template's literal at a different position — means neither side's fixed
    /// segments actually line up with anything on the other side; the only "overlap" is a
    /// coincidence of two unrelated literals happening to both be strings. A frontend call site's
    /// hole matching several sibling endpoints that all share ITS OWN fixed segments (real
    /// fan-out — e.g. TaskCenter's `{*}/{*}/reminder` against three `compliances|risks|
    /// governances/{taskId}/reminder` endpoints, where only the call site's holes ever do any
    /// absorbing) is unaffected: that is a single, consistent direction. So is the reverse — a
    /// literal call site matching several parameterized endpoint siblings (route precedence's own
    /// "row 4" shape) — the template's holes absorb the query's literals, one direction only.
    /// Criss-crossed, both-direction absorption is specifically the shape that lets two
    /// completely unrelated literal-anchored routes (e.g. `/Vendors/{*}/profile` and
    /// `/Vendors/haris-summary/{analysisId}`) coincidentally skeleton-match — rejected here.
    /// </summary>
    public static bool Matches(string normalizedTemplate, string normalizedQuery)
    {
        if (normalizedTemplate == normalizedQuery)
        {
            return true;
        }

        string[] templateSegments = normalizedTemplate.Split('/');
        string[] querySegments = normalizedQuery.Split('/');
        if (templateSegments.Length != querySegments.Length)
        {
            return false;
        }

        bool templateHoleAbsorbedQueryLiteral = false;
        bool queryHoleAbsorbedTemplateLiteral = false;

        for (int i = 0; i < templateSegments.Length; i++)
        {
            if (templateSegments[i] == querySegments[i])
            {
                continue;
            }

            bool templateIsHole = templateSegments[i] == "{x}";
            bool queryIsHole = querySegments[i] == "{x}";
            if (!templateIsHole && !queryIsHole)
            {
                // Two different literal segments — never bind to each other, no matter what
                // happens elsewhere in the route.
                return false;
            }

            if (templateIsHole)
            {
                templateHoleAbsorbedQueryLiteral = true;
            }

            if (queryIsHole)
            {
                queryHoleAbsorbedTemplateLiteral = true;
            }
        }

        return !(templateHoleAbsorbedQueryLiteral && queryHoleAbsorbedTemplateLiteral);
    }
}
