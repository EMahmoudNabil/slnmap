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

    /// <summary>
    /// Normalizes a route or template to its comparable skeleton: query string and hash fragment
    /// stripped, case-folded, every <c>{...}</c> hole collapsed to <c>{x}</c>, duplicate slashes
    /// collapsed, leading/trailing slashes trimmed. The root route normalizes to "".
    /// (Constraints with braces of their own, e.g. <c>{id:regex(^\d{3}$)}</c>, are not supported —
    /// no such constraint has appeared in the field; only <c>:int</c> has.)
    /// </summary>
    public static string Normalize(string route)
    {
        if (string.IsNullOrWhiteSpace(route))
        {
            return string.Empty;
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
