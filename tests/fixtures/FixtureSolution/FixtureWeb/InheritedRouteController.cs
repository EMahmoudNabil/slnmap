using Microsoft.AspNetCore.Mvc;

namespace Fixture.Web;

/// <summary>
/// Class-level [Route] is inherited when the derived type declares none of its own — and the
/// [controller] token binds to THIS class, not the declaring base. Deliberately in a separate
/// file from SharedBaseController (LegacyControllers.cs): the composed route depends on another
/// file's attribute, exercising the cross-file incremental dependency (rides the Inherits edge).
/// </summary>
public sealed class InheritedRouteController : SharedBaseController
{
    /// <summary>Inherited [Route("api/[controller]")] + [controller] = this class → GET /api/InheritedRoute/own.</summary>
    [HttpGet("own")]
    public string Own() => "own action";
}
