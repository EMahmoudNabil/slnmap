using Slnmap.Analysis;
using Slnmap.Core.Graph;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// v0.14.1 (BACKLOG MED, reports/v0140-gate3-token-tolerance.md): a controller whose
/// [Route]/[Http*] attributes are written but whose attribute types don't resolve was reported as
/// "conventionally routed (no route attributes)", a false statement about the code. It is now
/// disclosed as having unresolved route attributes. The project here is restored but has no
/// ASP.NET Core reference, so the attributes are unresolved under every SDK (an unrestored web
/// project resolves them under SDK 9 but not SDK 10).
/// </summary>
public sealed class RouteAttributesUnresolvedTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "slnmap-route-attrs", Guid.NewGuid().ToString("N"));

    public RouteAttributesUnresolvedTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort.
        }
    }

    [Fact]
    public async Task ControllerWithUnresolvableRouteAttributes_IsDisclosed_NotCalledConventional()
    {
        string csproj = Path.Combine(_directory, "NoMvc.csproj");
        File.WriteAllText(csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_directory, "OrdersController.cs"), """
            using Microsoft.AspNetCore.Mvc;

            namespace NoMvc;

            [Route("orders")]
            public class OrdersController
            {
                [HttpGet("{id}")]
                public int Get(int id) => id;
            }
            """);
        DotNet.Run($"restore \"{csproj}\"", _directory);

        var warnings = new List<string>();
        var snapshot = await new RoslynSolutionAnalyzer(warnings.Add).AnalyzeAsync(csproj);

        var disclosure = Assert.Single(snapshot.Graph.Disclosures, d => d.Kind == DisclosureKinds.RouteAttributesUnresolved);
        Assert.Equal("NoMvc.OrdersController", disclosure.Detail);
        Assert.DoesNotContain(snapshot.Graph.Disclosures, d => d.Kind == DisclosureKinds.ConventionalController);
        Assert.Equal(1, snapshot.Stats.ControllersRouteAttributesUnresolved);
        Assert.Equal(0, snapshot.Stats.ConventionalControllers);
        Assert.Contains(warnings, w => w.Contains("'OrdersController' has route attributes whose types don't resolve", StringComparison.Ordinal));
        Assert.DoesNotContain(warnings, w => w.Contains("conventionally routed", StringComparison.Ordinal));
        Assert.DoesNotContain(snapshot.Graph.Nodes, n => n.Kind == NodeKind.Endpoint);
    }

    [Fact]
    public async Task MethodOnlyRoutes_WithAHelperMethod_TheClassIsNeverAlsoCalledConventional()
    {
        // QA review of v0.14.1: with routes on the methods only, an attribute-less helper (and an
        // unresolved [NonAction]) made the same class "conventionally routed" too. Also: a
        // refusal that DOES resolve (a stubbed MVC [HttpHead]) beside an unresolved [HttpGet] is
        // still counted.
        string csproj = Path.Combine(_directory, "MethodRoutes.csproj");
        File.WriteAllText(csproj, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_directory, "ItemsController.cs"), """
            using Microsoft.AspNetCore.Mvc;

            namespace Microsoft.AspNetCore.Mvc
            {
                // A stand-in so this one MVC attribute resolves while [HttpGet]/[NonAction] don't.
                public sealed class HttpHeadAttribute : System.Attribute
                {
                }
            }

            namespace MethodRoutes
            {
                public class ItemsController
                {
                    [HttpGet("items/{id}")]
                    [HttpHead]
                    public int Get(int id) => id;

                    public int Helper() => 0;

                    [NonAction]
                    public int Hidden() => 1;
                }
            }
            """);
        DotNet.Run($"restore \"{csproj}\"", _directory);

        var warnings = new List<string>();
        var snapshot = await new RoslynSolutionAnalyzer(warnings.Add).AnalyzeAsync(csproj);

        Assert.Single(snapshot.Graph.Disclosures, d => d.Kind == DisclosureKinds.RouteAttributesUnresolved);
        Assert.DoesNotContain(snapshot.Graph.Disclosures, d => d.Kind == DisclosureKinds.ConventionalController);
        Assert.Equal(0, snapshot.Stats.ConventionalControllers);
        Assert.DoesNotContain(warnings, w => w.Contains("conventionally routed", StringComparison.Ordinal));
        Assert.Equal(1, snapshot.Stats.UnresolvedEndpoints); // the HEAD refusal
    }
}
