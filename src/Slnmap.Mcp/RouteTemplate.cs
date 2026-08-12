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

        for (int i = 0; i < templateSegments.Length; i++)
        {
            if (templateSegments[i] != querySegments[i] && templateSegments[i] != "{x}" && querySegments[i] != "{x}")
            {
                return false;
            }
        }

        return true;
    }
}
