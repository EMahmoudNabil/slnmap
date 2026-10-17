namespace Fixture.Web;

/// <summary>
/// Minimal-API fixture for the Endpoint-node investigation (see
/// reports/endpoint-nodes-investigation.md): a route group with a literal prefix, a
/// group-root route, a parameterized route, a pattern supplied via a constant, and one
/// handler registered for two routes. Mirrors the dominant production backend shapes in the
/// smallest form the v1 design must support.
/// </summary>
public static class SellerEndpoints
{
    /// <summary>A route pattern supplied via a constant — a webhook-route shape seen in production.</summary>
    public const string ArchivePattern = "/archive";

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/sellers");

        group.MapGet("/", ListSellers);
        group.MapPost("/{id}", UpdateSeller);

        // One handler serving two routes (verb fan-in): the designed shape is two distinct
        // Endpoint nodes whose HandledBy edges converge on the same Method node.
        group.MapGet(ArchivePattern, ArchiveSnapshot);
        group.MapPost(ArchivePattern, ArchiveSnapshot);

        // cross-stack-linker-investigation.md §Q5: the literal-vs-param sibling shape that
        // produces the real, currently-unresolved row-4 ambiguity (mirrors a production backend's
        // live AccountProfiles group: GET "current" alongside GET "{id}" — confirmed a real,
        // present-day ambiguous pair by this investigation's production survey, not a synthetic
        // worst case).
        group.MapGet("/current", GetSellerContext);
        group.MapGet("/{sellerId}", GetSeller);

        // A true fan-out target set (mirrors a production backend's OrderQueue shape, §2 of the
        // feasibility spike): one frontend call site with a hole in the differing position
        // reaches all three of these, and only these, deterministically.
        group.MapPost("/notify/email", NotifyByEmail);
        group.MapPost("/notify/sms", NotifyBySms);
        group.MapPost("/notify/push", NotifyByPush);
    }

    public static string Ping() => "ok";

    public static string ListSellers() => "sellers";

    public static string UpdateSeller(string id) => $"updated {id}";

    public static string ArchiveSnapshot() => "archive";

    public static string GetSellerContext() => "current-seller-context";

    public static string GetSeller(string sellerId) => $"seller {sellerId}";

    public static string NotifyByEmail() => "notified by email";

    public static string NotifyBySms() => "notified by sms";

    public static string NotifyByPush() => "notified by push";
}
