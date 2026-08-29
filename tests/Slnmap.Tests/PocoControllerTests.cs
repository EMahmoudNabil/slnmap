using Slnmap.Analysis;
using Slnmap.Core.Analysis;
using Slnmap.Core.Graph;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// v0.13.1 (reports/v0131-poco-controller-investigation.md,
/// reports/v0131-poco-controller-fix-report.md): the controller-action syntactic prefilter
/// widened to also admit a class with NO base list that looks controller-ish some other way
/// (name ends in "Controller", an [ApiController]/[Route]/[Controller] attribute on the class,
/// or an [Http*] attribute on a member) — real ASP.NET Core "POCO controllers" (no
/// ControllerBase inheritance at all) were previously invisible to controller-endpoint
/// extraction with zero trace of any kind. A class that looks controller-ish but still fails
/// semantic classification is now disclosed, never silently skipped.
/// Fixture: tests/fixtures/FixtureSolution/FixtureWeb/PocoControllers.cs.
/// </summary>
public sealed class PocoControllerTests
{
    private static async Task<(AnalysisSnapshot Snapshot, List<string> Warnings)> AnalyzeWithWarningsAsync()
    {
        // Restore is deduplicated per (directory, command) by TestInfrastructure.DotNet — safe
        // and cheap even if another test class's fixture already restored this solution.
        DotNet.Run($"restore \"{TestPaths.FixtureSolution}\"", TestPaths.RepoRoot);

        var warnings = new List<string>();
        var analyzer = new RoslynSolutionAnalyzer(warnings.Add);
        var snapshot = await analyzer.AnalyzeAsync(TestPaths.FixtureSolution);
        return (snapshot, warnings);
    }

    [Fact]
    public async Task PocoController_NoBaseListAtAll_ProducesEndpointNodesJustLikeAControllerBaseOne()
    {
        // The permanent regression fixture: PocoUserController has NO base list whatsoever
        // (modeled directly on the real gothinkster/aspnetcore-realworld-example-app
        // UserController) -- must produce real Endpoint nodes now.
        var (snapshot, _) = await AnalyzeWithWarningsAsync();

        // EndpointFacts.ComposeTemplate is always rooted (leading '/'), same as every other
        // controller-endpoint template in this codebase.
        GraphAssert.Node(snapshot.Graph, NodeKind.Endpoint, "GET /pocouser");
        GraphAssert.Node(snapshot.Graph, NodeKind.Endpoint, "PUT /pocouser");
    }

    [Fact]
    public async Task PocoController_TemplatedAction_ComposesWithTheClassTemplate()
    {
        // PocoUsersController: bare POST uses the class template alone; [HttpPost("login")]
        // composes class + action templates -- the same MVC selector semantics ControllerBase
        // controllers already get, now reachable for a base-list-less class too.
        var (snapshot, _) = await AnalyzeWithWarningsAsync();

        GraphAssert.Node(snapshot.Graph, NodeKind.Endpoint, "POST /pocousers");
        GraphAssert.Node(snapshot.Graph, NodeKind.Endpoint, "POST /pocousers/login");
    }

    [Fact]
    public async Task InternalControllerLikeClass_LooksControllerish_ButFailsSemanticCheck_IsDisclosed_NeverSilentlySkipped()
    {
        // InternalPocoController: name ends in "Controller", has [HttpGet] -- looks
        // controller-ish syntactically, but is NOT public, so it doesn't match ASP.NET's real
        // POCO-controller discovery rule. The actual bug this fix closes: this must be
        // DISCLOSED (a counted category with a reason), never silently invisible, and it must
        // NOT fabricate an endpoint that doesn't really exist.
        var (snapshot, warnings) = await AnalyzeWithWarningsAsync();

        Assert.True(
            snapshot.Stats.ControllerLikeClassesUnrecognized > 0,
            "expected at least one controller-like-but-unrecognized class to be counted");
        Assert.Contains(
            warnings,
            w => w.Contains("InternalPocoController", StringComparison.Ordinal)
                && w.Contains("not recognized", StringComparison.Ordinal));
        Assert.DoesNotContain(
            snapshot.Graph.Nodes,
            n => n.Kind == NodeKind.Endpoint && n.FilePath is { } path && path.EndsWith("PocoControllers.cs", StringComparison.Ordinal)
                && n.Fqn.Contains("Ping", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OrdinaryClassWithAnUnrelatedBaseList_IsNotTreatedAsAController_NoFalsePositive()
    {
        // UnrelatedServiceWithAnInterface implements IWorkPerformer (so it HAS a base list --
        // exercises the pre-existing prefilter path unchanged) but has no controller-ish shape
        // at all and doesn't derive from ControllerBase. Must not be modeled, and must not
        // trigger the new disclosure either (it never looked controller-ish to begin with).
        var (snapshot, warnings) = await AnalyzeWithWarningsAsync();

        Assert.DoesNotContain(
            snapshot.Graph.Nodes,
            n => n.Kind == NodeKind.Endpoint && n.FilePath is { } path && path.EndsWith("PocoControllers.cs", StringComparison.Ordinal)
                && n.Fqn.Contains("DoWork", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(warnings, w => w.Contains("UnrelatedServiceWithAnInterface", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PlainServiceClass_NoControllerishSignalOfAnyKind_OssusShape_UnaffectedAndUncounted()
    {
        // PlainServiceHelper: no base list, no "Controller" suffix, no [ApiController]/[Route],
        // no [Http*] anywhere -- the OSSUS_BE.sln shape (measured: 0 controller-ish classes of
        // any kind across the entire real solution, reports/v0131-poco-controller-investigation.md
        // §2). Must stay completely untouched: no endpoint, no disclosure, no semantic cost paid.
        var (snapshot, warnings) = await AnalyzeWithWarningsAsync();

        Assert.DoesNotContain(
            snapshot.Graph.Nodes,
            n => n.Kind == NodeKind.Endpoint && n.FilePath is { } path && path.EndsWith("PocoControllers.cs", StringComparison.Ordinal)
                && n.Fqn.Contains("DoSomething", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(warnings, w => w.Contains("PlainServiceHelper", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExistingControllerBaseControllers_AreCompletelyUnaffected()
    {
        // StatusController (ControllerBase-derived, pre-existing fixture) must still classify
        // exactly as before -- the widened prefilter/semantic check is purely additive.
        var (snapshot, _) = await AnalyzeWithWarningsAsync();

        GraphAssert.Node(snapshot.Graph, NodeKind.Endpoint, "GET /api/Status");
    }
}
