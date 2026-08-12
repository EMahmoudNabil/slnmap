namespace Fixture.Web;

/// <summary>
/// The count-never-guess fixtures: registrations the v1 design must refuse to resolve — each must
/// produce NO Endpoint node and increment the unresolved counter with a reason — plus the
/// lambda-handler case (node emitted, HandledBy omitted).
/// </summary>
public class NonLeafHooks : EndpointGroupBase
{
    public override void Map(WebApplication app)
    {
        // GetType().Name on a NON-leaf receiver: DerivedHooks below derives from this class, so
        // the runtime name may differ from the static one. The guard must refuse → unresolved.
        var group = app.MapGroup(this);
        group.MapGet(Ping, "ping");
    }

    public static string Ping() => "pong";
}

/// <summary>Exists only to make <see cref="NonLeafHooks"/> a non-leaf class.</summary>
public sealed class DerivedHooks : NonLeafHooks
{
}

public static class UnresolvedRegistrations
{
    /// <summary>Not a const — GetConstantValue fails, and it is not the string.Empty well-known member.</summary>
    private static readonly string EchoPattern = "echo";

    public static void Map(WebApplication app)
    {
        // Non-constant pattern → unresolved, counted.
        app.MapGet(EchoPattern, Echo);

        // Lambda handler: the endpoint itself resolves (a node is emitted), but there is no
        // method group to hang a HandledBy edge on — declared, not guessed.
        app.MapGet("/inline", () => "inline");
    }

    /// <summary>
    /// A wrapper that hides registrations from the Map*-name prefilter: its body's pattern is a
    /// parameter of a non-Map* method, so the body itself is counted as the unresolvable surface.
    /// </summary>
    public static void RegisterPing(IEndpointRouteBuilder builder, string pattern)
    {
        builder.MapGet(pattern, Echo);
    }

    public static string Echo() => "echo";
}
