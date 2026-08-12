using Microsoft.AspNetCore.Mvc;

namespace Fixture.Web;

/// <summary>
/// Attribute-routed controller fixture for the v1.1 controller-endpoints investigation, mirroring
/// the eShopOnWeb census shapes at minimum size: a class-level token route, bare verb attributes
/// (class template alone), a verb attribute with its own template (appended), a combined
/// [Route]+[HttpPost] action, and an absolute-template override.
/// </summary>
[Route("api/[controller]")]
public class StatusController : ControllerBase
{
    /// <summary>Bare verb attribute: effective route = the class template → GET /api/Status.</summary>
    [HttpGet]
    public string GetStatus() => "ok";

    /// <summary>Verb attribute with template: appended → GET /api/Status/history/{days:int}.</summary>
    [HttpGet("history/{days:int}")]
    public string GetHistory(int days) => $"history {days}";

    /// <summary>[Route] supplies the template, the verb attribute the verb → POST /api/Status/reset.</summary>
    [Route("reset")]
    [HttpPost]
    public string Reset() => "reset";

    /// <summary>A leading "/" makes the action template absolute — the class prefix is ignored → DELETE /maintenance/purge.</summary>
    [HttpDelete("/maintenance/purge")]
    public string Purge() => "purged";

    /// <summary>[NonAction] public methods are not actions — no endpoint node.</summary>
    [NonAction]
    public string Helper() => "not an action";
}

/// <summary>
/// A Minimal-API registration of the SAME verb+template as StatusController's bare [HttpGet]
/// (GET /api/Status): duplicate routes collapse to ONE node ACROSS extractors, each contributing
/// its own HandledBy edge — the honest superposition (they would collide at runtime too).
/// Kept in this file so both contributions share one eviction scope (see Program.cs note).
/// </summary>
public static class StatusCompat
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/Status", VendorEndpoints.Ping);
    }
}
