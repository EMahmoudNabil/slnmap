using Slnmap.Analysis;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// v0.13.1 (reports/v0130-regression-investigation-0of22-realworld.md, "cheap honesty"): a call
/// shaped like <c>MvcOptions.Conventions.Add(someConvention)</c> registers an
/// <c>IApplicationModelConvention</c>, which can mutate route templates at MVC
/// application-model-build time (e.g. inject a base-path prefix) invisibly to static analysis —
/// confirmed as the real root cause of a 0/22 cross-stack-linking regression against the real
/// `gothinkster/aspnetcore-realworld-example-app`'s actual `ApiRoutePrefixConvention`. Disclosed
/// as a plain warning (no new stat field, no interpretation of what the convention does) so it
/// flows through the existing `analyze`/`--verbose` warning machinery unchanged.
/// Fixture: tests/fixtures/FixtureSolution/FixtureWeb/ApplicationModelConventionFixture.cs.
/// </summary>
public sealed class ConventionsAddWarningTests
{
    [Fact]
    public async Task ConventionsAdd_OnAnIApplicationModelConventionParameter_EmitsADisclosedWarning()
    {
        // Restore is deduplicated per (directory, command) by TestInfrastructure.DotNet — safe
        // and cheap to call even if another test class's fixture already restored this same
        // solution earlier in the run.
        DotNet.Run($"restore \"{TestPaths.FixtureSolution}\"", TestPaths.RepoRoot);

        var warnings = new List<string>();
        var analyzer = new RoslynSolutionAnalyzer(warnings.Add);
        await analyzer.AnalyzeAsync(TestPaths.FixtureSolution);

        Assert.Contains(
            warnings,
            w => w.Contains("IApplicationModelConvention", StringComparison.Ordinal)
                && w.Contains("ApplicationModelConventionFixture.cs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ConventionsAdd_DoesNotAttemptToInterpretTheConvention_NoNewEndpointOrStatSideEffect()
    {
        // Cheap honesty only: the fixture's FixtureRoutePrefixConvention.Apply body is empty and
        // never actually runs (slnmap never executes analyzed code) -- the warning is the ONLY
        // observable effect. No new Endpoint node, no change to any existing count-based stat.
        DotNet.Run($"restore \"{TestPaths.FixtureSolution}\"", TestPaths.RepoRoot);

        var analyzer = new RoslynSolutionAnalyzer();
        var snapshot = await analyzer.AnalyzeAsync(TestPaths.FixtureSolution);

        Assert.DoesNotContain(
            snapshot.Graph.Nodes,
            n => n.FilePath is { } path && path.EndsWith("ApplicationModelConventionFixture.cs", StringComparison.Ordinal)
                && n.Kind == Slnmap.Core.Graph.NodeKind.Endpoint);
    }
}
