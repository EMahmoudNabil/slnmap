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
    }

    public static string Ping() => "ok";

    public static string ListVendors() => "vendors";

    public static string UpdateVendor(string id) => $"updated {id}";

    public static string ArchiveSnapshot() => "archive";
}
