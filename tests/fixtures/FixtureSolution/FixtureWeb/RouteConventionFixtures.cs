using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Routing;

namespace Fixture.Web;

// v0.14.0 fixture (reports/gap-b-route-conventions-investigation.md): every way an MVC model
// convention that can rewrite routes gets registered, beyond the IApplicationModelConvention
// Conventions.Add(...) shape v0.13.1 already disclosed (ApplicationModelConventionFixture.cs).
// None of these is wired into a real pipeline: slnmap only ever looks at the registration.

/// <summary>Registered through ASP.NET's Add(this IList&lt;IApplicationModelConvention&gt;, IControllerModelConvention) extension.</summary>
public sealed class FixtureControllerConvention : IControllerModelConvention
{
    public void Apply(ControllerModel controller)
    {
    }
}

/// <summary>The slug transformer eShopOnWeb hands to RouteTokenTransformerConvention.</summary>
public sealed class FixtureSlugTransformer : IOutboundParameterTransformer
{
    public string? TransformOutbound(object? value) => value?.ToString()?.ToLowerInvariant();
}

/// <summary>An action-model convention applied as an attribute rather than through Conventions.Add.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class FixtureActionConventionAttribute : Attribute, IActionModelConvention
{
    public void Apply(ActionModel action)
    {
    }
}

public static class FixtureMoreConventionRegistrations
{
    public static void Register(MvcOptions options)
    {
        options.Conventions.Add(new FixtureControllerConvention());
        options.Conventions.Add(new RouteTokenTransformerConvention(new FixtureSlugTransformer()));
        options.Conventions.Insert(0, new FixtureRoutePrefixConvention());
    }
}

// Deliberately not a controller: the disclosure is about the attribute's type, and keeping it off
// a controller leaves every endpoint count other tests pin untouched.
[FixtureActionConvention]
public sealed class FixtureConventionDecorated
{
}
