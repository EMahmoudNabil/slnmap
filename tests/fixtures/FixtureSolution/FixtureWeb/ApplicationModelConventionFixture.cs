using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace Fixture.Web;

/// <summary>
/// v0.13.1 fixture (reports/v0130-regression-investigation-0of22-realworld.md): a real
/// <c>IApplicationModelConvention</c> registered via <c>MvcOptions.Conventions.Add(...)</c> — the
/// exact shape the real `gothinkster/aspnetcore-realworld-example-app`'s `ApiRoutePrefixConvention`
/// uses to inject a base-path prefix at runtime, invisible to static endpoint extraction. This
/// fixture doesn't need to be wired into a real DI pipeline to exercise the detection — the
/// registration call site alone is what `DocumentWalker` recognizes (by the parameter's real
/// type, never the receiver's variable name).
/// </summary>
public sealed class FixtureRoutePrefixConvention : IApplicationModelConvention
{
    public void Apply(ApplicationModel application)
    {
        // Deliberately empty -- slnmap never interprets what a convention does, only that one
        // was registered.
    }
}

public static class FixtureConventionRegistration
{
    public static void Register(MvcOptions options)
    {
        options.Conventions.Add(new FixtureRoutePrefixConvention());
    }
}
