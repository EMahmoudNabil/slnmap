using Microsoft.AspNetCore.Mvc;

namespace Fixture.Web;

/// <summary>
/// The count-or-ignore side of the v1.1 controller design (deterministic-or-declared): shapes the
/// extractor must refuse or skip — each with the exact reason the investigation assigns.
/// </summary>
///
/// <remarks>
/// <para><b>Conventional routing (no route attributes anywhere):</b> routed by
/// <c>MapControllerRoute</c> patterns, a different routing system — out of scope for v1.1
/// (ignored, not counted; the eShop Identity Quickstart shape).</para>
/// </remarks>
public class LegacyPagesController : ControllerBase
{
    public string Index() => "conventionally routed";

    public string Details(int id) => $"details {id}";
}

/// <summary>
/// The MVC token convention: [Route("[controller]/[action]")] + bare verbs — the dominant
/// eShopOnWeb shape. [action] must strip a trailing "Async" from the method name, matching MVC's
/// own action-name convention.
/// </summary>
[Route("[controller]/[action]")]
public class ReportsController : ControllerBase
{
    /// <summary>GET /Reports/Monthly.</summary>
    [HttpGet]
    public string Monthly() => "monthly";

    /// <summary>[action] strips the Async suffix → POST /Reports/Rebuild.</summary>
    [HttpPost]
    public Task<string> RebuildAsync() => Task.FromResult("rebuilt");
}

/// <summary>
/// An action with a route template but NO verb attribute matches every HTTP verb — v1.1 refuses
/// to pick one (counted as unresolved, never guessed).
/// </summary>
[Route("api/[controller]")]
public class AmbiguousVerbController : ControllerBase
{
    [Route("ping")]
    public string Ping() => "any verb";
}

/// <summary>
/// An abstract controller's actions produce one route per derived type (the [controller] token
/// binds to the runtime type) — v1.1 counts the declared action out rather than enumerating
/// derivatives (the dormant eShopOnWeb BaseApiController shape).
/// </summary>
[Route("api/[controller]")]
public abstract class SharedBaseController : ControllerBase
{
    [HttpGet("shared")]
    public string Shared() => "declared on an abstract controller";
}

// InheritedRouteController (the derived-type side of this shape) lives in its own file —
// InheritedRouteController.cs — so the cross-FILE inheritance dependency is real: its composed
// route depends on THIS file's [Route] attribute, and the incremental planner must re-derive it
// when this file changes (via the derived→base Inherits edge, the v0.7.0 proviso-2 mechanism).
