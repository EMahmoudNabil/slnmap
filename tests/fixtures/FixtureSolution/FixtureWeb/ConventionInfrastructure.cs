using System.Diagnostics.CodeAnalysis;

namespace Fixture.Web;

/// <summary>
/// The CleanArchitecture-template routing convention at minimum size (Tier-2 fixture for the
/// endpoint-nodes implementation, mirroring OSSUS_Backend's Web/Infrastructure): an abstract group
/// base, a MapGroup forwarder whose prefix is computed from <c>GetType().Name</c>, and
/// reversed-argument Map* extensions that forward to the framework order.
/// </summary>
public abstract class EndpointGroupBase
{
    public abstract void Map(WebApplication app);
}

public static class ConventionWebApplicationExtensions
{
    /// <summary>
    /// The convention prefix forwarder: never a literal — the prefix is interpolated from the
    /// group instance's runtime type name. Sound to fold statically only when the argument's
    /// static type is a leaf class (the guarded GetType().Name equivalence).
    /// </summary>
    public static RouteGroupBuilder MapGroup(this WebApplication app, EndpointGroupBase group)
    {
        var groupName = group.GetType().Name;

        return app
            .MapGroup($"/api/{groupName}")
            .WithTags(groupName);
    }
}

public static class ConventionRouteBuilderExtensions
{
    /// <summary>Reversed argument order (handler first, pattern second, defaulted) — overload resolution, not position, decides the roles.</summary>
    public static RouteHandlerBuilder MapGet(this IEndpointRouteBuilder builder, Delegate handler, [StringSyntax("Route")] string pattern = "")
    {
        return builder.MapGet(pattern, handler)
            .WithName(handler.Method.Name);
    }

    public static RouteHandlerBuilder MapPost(this IEndpointRouteBuilder builder, Delegate handler, [StringSyntax("Route")] string pattern = "")
    {
        return builder.MapPost(pattern, handler)
            .WithName(handler.Method.Name);
    }

    public static RouteHandlerBuilder MapPut(this IEndpointRouteBuilder builder, Delegate handler, [StringSyntax("Route")] string pattern)
    {
        return builder.MapPut(pattern, handler)
            .WithName(handler.Method.Name);
    }

    public static RouteHandlerBuilder MapDelete(this IEndpointRouteBuilder builder, Delegate handler, [StringSyntax("Route")] string pattern)
    {
        return builder.MapDelete(pattern, handler)
            .WithName(handler.Method.Name);
    }
}
