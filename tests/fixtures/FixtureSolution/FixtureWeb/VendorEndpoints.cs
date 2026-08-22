namespace Fixture.Web;

/// <summary>
/// Minimal-API fixture for the Endpoint-node investigation (see
/// reports/endpoint-nodes-investigation.md): a route group with a literal prefix, a
/// group-root route, a parameterized route, a pattern supplied via a constant, and one
/// handler registered for two routes. Mirrors the dominant OSSUS_Backend shapes in the
/// smallest form the v1 design must support.
/// </summary>
public static class VendorEndpoints
{
    /// <summary>A route pattern supplied via a constant — the QuossusWebhook shape in OSSUS.</summary>
    public const string ArchivePattern = "/archive";

    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/vendors");

        group.MapGet("/", ListVendors);
        group.MapPost("/{id}", UpdateVendor);

        // One handler serving two routes (verb fan-in): the designed shape is two distinct
        // Endpoint nodes whose HandledBy edges converge on the same Method node.
        group.MapGet(ArchivePattern, ArchiveSnapshot);
        group.MapPost(ArchivePattern, ArchiveSnapshot);

        // cross-stack-linker-investigation.md §Q5: the literal-vs-param sibling shape that
        // produces the real, currently-unresolved row-4 ambiguity (mirrors OSSUS_Backend's
        // live UserProfiles group: GET "current" alongside GET "{id}" — confirmed a real,
        // present-day ambiguous pair by this investigation's OSSUS survey, not a synthetic
        // worst case).
        group.MapGet("/current", GetVendorContext);
        group.MapGet("/{vendorId}", GetVendor);

        // A true fan-out target set (mirrors OSSUS_Backend's TaskCenter shape, §2 of the
        // feasibility spike): one frontend call site with a hole in the differing position
        // reaches all three of these, and only these, deterministically.
        group.MapPost("/notify/email", NotifyByEmail);
        group.MapPost("/notify/sms", NotifyBySms);
        group.MapPost("/notify/push", NotifyByPush);
    }

    public static string Ping() => "ok";

    public static string ListVendors() => "vendors";

    public static string UpdateVendor(string id) => $"updated {id}";

    public static string ArchiveSnapshot() => "archive";

    public static string GetVendorContext() => "current-vendor-context";

    public static string GetVendor(string vendorId) => $"vendor {vendorId}";

    public static string NotifyByEmail() => "notified by email";

    public static string NotifyBySms() => "notified by sms";

    public static string NotifyByPush() => "notified by push";
}
