using Microsoft.AspNetCore.Mvc;

namespace Fixture.Web;

/// <summary>
/// v0.13.1 (reports/v0131-poco-controller-investigation.md): a real ASP.NET Core "POCO
/// controller" — no <c>ControllerBase</c>/<c>Controller</c> inheritance at all, discovered by
/// ASP.NET's own name/attribute convention instead. Modeled directly on
/// `gothinkster/aspnetcore-realworld-example-app`'s actual `UserController`
/// (`[Route("user")]`, no base list, `[HttpGet]`/`[HttpPut]` actions) — the exact shape that was
/// entirely invisible to controller-endpoint extraction before this fix (a 0/22 cross-stack-
/// linking regression traced back to here, not to the linker).
/// </summary>
[Route("pocouser")]
public class PocoUserController(object dependency)
{
    [HttpGet]
    public string GetCurrent() => dependency.ToString() ?? string.Empty;

    [HttpPut]
    public void UpdateUser() { }
}

/// <summary>
/// A second POCO controller with an action-level template — mirrors `UsersController`'s
/// `[HttpPost("login")]` shape (a templated action on a class-templated POCO controller).
/// </summary>
[Route("pocousers")]
public class PocoUsersController
{
    [HttpPost]
    public void Create() { }

    [HttpPost("login")]
    public void Login() { }
}

/// <summary>
/// Looks controller-ish (name ends in "Controller", has an [HttpGet] action) but is <c>internal</c>
/// — ASP.NET's real POCO-controller discovery requires a PUBLIC class, so this must NOT be
/// recognized as a controller. Confirms the "looks controller-ish but fails semantic
/// classification" case is DISCLOSED (a counted category with a reason), never silently skipped —
/// the actual bug this fix closes, independent of detection coverage.
/// </summary>
internal class InternalPocoController
{
    [HttpGet]
    public void Ping() { }
}

/// <summary>
/// An ordinary, unrelated class that happens to implement an interface (so it HAS a base list,
/// exercising the pre-existing prefilter path unchanged) but has no controller-ish shape at all —
/// a no-false-positive control alongside the OSSUS-shape check below. Deliberately named to NOT
/// end in "Controller" at all (a class ending in "...AController" would itself satisfy the
/// name-suffix signal, per ASP.NET's own real, un-nuanced string-suffix convention — that would
/// be a self-inflicted test bug, not a detection bug).
/// </summary>
public interface IWorkPerformer
{
    void DoWork();
}

public class UnrelatedServiceWithAnInterface : IWorkPerformer
{
    public void DoWork() { }
}

/// <summary>
/// OSSUS-shape no-false-positive control: an ordinary POCO class (no base list, no
/// controller-ish signal of any kind — no "Controller" suffix, no [ApiController]/[Route], no
/// [Http*] on any member) — the vast majority shape in a real Minimal-API-only codebase like
/// OSSUS_BE (measured: 0 controller-ish classes of any kind). Must stay completely untouched by
/// the widened prefilter — zero semantic cost, exactly as before.
/// </summary>
public class PlainServiceHelper
{
    public void DoSomething() { }
}
