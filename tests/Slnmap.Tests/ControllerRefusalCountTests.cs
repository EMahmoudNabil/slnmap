using Slnmap.Analysis;
using Slnmap.Core.Graph;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// v0.14.1 (Gate 8 QA #6): each refusal is one route ASP.NET registers but slnmap doesn't model,
/// so two identical refusals on one action (two [HttpHead] attributes) are two unresolved
/// registrations. v0.14.0 stored both at the declaration's location with the same reason, and the
/// fact set merged them into one.
/// </summary>
public sealed class ControllerRefusalCountTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "slnmap-refusals", Guid.NewGuid().ToString("N"));

    public ControllerRefusalCountTests() => Directory.CreateDirectory(_directory);

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
    public async Task IdenticalRefusalsOnOneAction_AreCountedSeparately()
    {
        string csproj = Path.Combine(_directory, "Refusals.csproj");
        File.WriteAllText(csproj, """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_directory, "PingController.cs"), """
            using Microsoft.AspNetCore.Mvc;

            namespace Refusals;

            [ApiController]
            [Route("ping")]
            public class PingController : ControllerBase
            {
                [HttpHead("a")]
                [HttpHead("b")]
                public IActionResult Head() => Ok();

                [HttpOptions]
                public IActionResult Options() => Ok();
            }
            """);
        DotNet.Run($"restore \"{csproj}\"", _directory);

        var snapshot = await new RoslynSolutionAnalyzer().AnalyzeAsync(csproj);

        Assert.Equal(3, snapshot.Stats.UnresolvedEndpoints);
        Assert.Equal(3, snapshot.Graph.Disclosures.Count(d => d.Kind == DisclosureKinds.UnresolvedEndpoint));
    }
}
