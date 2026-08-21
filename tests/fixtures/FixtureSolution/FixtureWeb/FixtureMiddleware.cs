namespace Fixture.Web;

/// <summary>
/// Exercises issue #9's reconstructed repro: a first-party class registered via the FRAMEWORK's
/// generic UseMiddleware&lt;T&gt;() — the generic type argument must produce a References edge
/// even though the containing generic method is external. (The issue itself flags its repro as
/// reconstructed-and-unverified; the gap test doubles as the verification probe.)
/// </summary>
public sealed class FixtureMiddleware
{
    private readonly RequestDelegate _next;

    public FixtureMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context) => _next(context);
}
