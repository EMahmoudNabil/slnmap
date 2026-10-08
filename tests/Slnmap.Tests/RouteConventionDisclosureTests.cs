using Slnmap.Analysis;
using Slnmap.Core.Analysis;
using Slnmap.Core.Graph;
using Slnmap.Core.Storage;
using Slnmap.Mcp;
using Slnmap.Storage;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// v0.14.0 Phase 2 (RELEASE-PLAN-v0.14.0.md; reports/gap-b-route-conventions-investigation.md):
/// every route-rewriting MVC convention registration is disclosed, the disclosure reaches the MCP
/// tools, every disclosure counter survives incremental runs (the pre-existing reset-to-zero bug,
/// reports/v0140-gate1-schema-v2.md §4.1), and --route-prefix is applied and always marked.
/// Fixtures: FixtureWeb/ApplicationModelConventionFixture.cs + FixtureWeb/RouteConventionFixtures.cs.
/// </summary>
public sealed class RouteConventionDisclosureTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "slnmap-tests", Guid.NewGuid().ToString("N"));

    public RouteConventionDisclosureTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort; the OS temp cleaner will get it eventually.
        }
    }

    [Fact]
    public async Task Analyze_DisclosesEveryConventionRegistrationShape_WithItsTypeAndInterface()
    {
        DotNet.Run($"restore \"{TestPaths.FixtureSolution}\"", TestPaths.RepoRoot);
        var warnings = new List<string>();
        var snapshot = await new RoslynSolutionAnalyzer(warnings.Add).AnalyzeAsync(TestPaths.FixtureSolution);

        var details = snapshot.Graph.Disclosures
            .Where(d => d.Kind == DisclosureKinds.RouteConvention)
            .Select(d => d.Detail)
            .ToList();

        // The v0.13.1 shape, plus its Insert(...) form.
        Assert.Equal(2, details.Count(d => d == "Fixture.Web.FixtureRoutePrefixConvention (IApplicationModelConvention)"));
        // The controller-model extension overload v0.13.1 missed.
        Assert.Contains("Fixture.Web.FixtureControllerConvention (IControllerModelConvention)", details);
        // The framework action-model convention eShopOnWeb registers (also missed by v0.13.1).
        Assert.Contains("Microsoft.AspNetCore.Mvc.ApplicationModels.RouteTokenTransformerConvention (IActionModelConvention)", details);
        // A convention applied as an attribute.
        Assert.Contains("Fixture.Web.FixtureActionConventionAttribute (IActionModelConvention, applied as an attribute)", details);

        Assert.Equal(5, details.Count);
        Assert.Equal(5, snapshot.Stats.RouteConventionsRegistered);
        Assert.Equal(5, warnings.Count(w => w.StartsWith("Route convention ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task IncrementalRun_OnAnUnrelatedFile_KeepsEveryDisclosureCounter()
    {
        // Regression for the counter reset shipped through v0.13.1: counters were summed over the
        // re-walked documents only, so touching any unrelated file zeroed every one of them.
        string solutionPath = CopyFixtureSolution();
        var analyzer = new RoslynSolutionAnalyzer();
        var cold = await analyzer.AnalyzeAsync(solutionPath);
        Assert.True(cold.Stats.UnresolvedEndpoints > 0);
        Assert.True(cold.Stats.ConventionalControllers > 0);
        Assert.True(cold.Stats.RazorPagesNotModeled > 0);
        Assert.True(cold.Stats.ControllerLikeClassesUnrecognized > 0);
        Assert.True(cold.Stats.RouteConventionsRegistered > 0);

        File.AppendAllText(Path.Combine(_root, "FixtureLib", "Shapes.cs"), "\n");
        var incremental = await analyzer.AnalyzeAsync(solutionPath, cold);

        Assert.True(incremental.Stats.DocumentsSkipped > 0, "expected an incremental run, not a full rebuild");
        Assert.Equal(cold.Stats.UnresolvedEndpoints, incremental.Stats.UnresolvedEndpoints);
        Assert.Equal(cold.Stats.ConventionalControllers, incremental.Stats.ConventionalControllers);
        Assert.Equal(cold.Stats.RazorPagesNotModeled, incremental.Stats.RazorPagesNotModeled);
        Assert.Equal(cold.Stats.ControllerLikeClassesUnrecognized, incremental.Stats.ControllerLikeClassesUnrecognized);
        Assert.Equal(cold.Stats.RouteConventionsRegistered, incremental.Stats.RouteConventionsRegistered);
        Assert.True(incremental.Graph.FactsEqual(cold.Graph));
    }

    [Fact]
    public async Task IncrementalRun_EditingTheRegistrationFile_UpdatesTheDisclosuresFromIt()
    {
        string solutionPath = CopyFixtureSolution();
        var analyzer = new RoslynSolutionAnalyzer();
        var cold = await analyzer.AnalyzeAsync(solutionPath);

        string registrations = Path.Combine(_root, "FixtureWeb", "RouteConventionFixtures.cs");
        string text = File.ReadAllText(registrations);
        File.WriteAllText(
            registrations,
            text.Replace("        options.Conventions.Add(new FixtureControllerConvention());\n", string.Empty, StringComparison.Ordinal)
                .Replace("        options.Conventions.Add(new FixtureControllerConvention());\r\n", string.Empty, StringComparison.Ordinal));

        var incremental = await analyzer.AnalyzeAsync(solutionPath, cold);

        Assert.Equal(cold.Stats.RouteConventionsRegistered - 1, incremental.Stats.RouteConventionsRegistered);
        Assert.DoesNotContain(
            incremental.Graph.Disclosures,
            d => d.Detail.StartsWith("Fixture.Web.FixtureControllerConvention", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RoutePrefix_PrefixesControllerEndpointsOnly_AndMarksEachOne()
    {
        DotNet.Run($"restore \"{TestPaths.FixtureSolution}\"", TestPaths.RepoRoot);
        var plain = await new RoslynSolutionAnalyzer().AnalyzeAsync(TestPaths.FixtureSolution);
        var prefixed = await new RoslynSolutionAnalyzer(options: new AnalysisOptions("api/")).AnalyzeAsync(TestPaths.FixtureSolution);

        var plainEndpoints = plain.Graph.Nodes.Where(n => n.Kind == NodeKind.Endpoint).Select(n => n.Fqn).ToHashSet(StringComparer.Ordinal);
        var prefixedEndpoints = prefixed.Graph.Nodes.Where(n => n.Kind == NodeKind.Endpoint).Select(n => n.Fqn).ToHashSet(StringComparer.Ordinal);
        var marked = prefixed.Graph.Disclosures
            .Where(d => d.Kind == DisclosureKinds.RoutePrefixApplied)
            .Select(d => d.Detail)
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(marked);

        // Every plain endpoint survives, as-is (Minimal API) or prefixed (controller). Counts can
        // legitimately differ: the fixture has a controller route and a Minimal API route that
        // share "GET /api/Status" — one node without a prefix, two once the controller's moves.
        Assert.All(plainEndpoints, f =>
        {
            int space = f.IndexOf(' ', StringComparison.Ordinal);
            string withPrefix = $"{f[..space]} /api{(f[(space + 1)..] == "/" ? string.Empty : f[(space + 1)..])}";
            Assert.True(
                prefixedEndpoints.Contains(f) || marked.Contains(withPrefix),
                $"plain endpoint '{f}' is missing from the prefixed run (looked for '{f}' and marked '{withPrefix}')");
        });
        foreach (string fqn in marked)
        {
            // "VERB /api/rest" — the user's prefix ("api/", normalized) in front of a template that
            // exists unprefixed in the plain run.
            Assert.Contains(fqn, prefixedEndpoints);
            int space = fqn.IndexOf(' ', StringComparison.Ordinal);
            string rest = fqn[(space + "/api".Length + 1)..];
            string unprefixed = $"{fqn[..space]} {(rest.Length == 0 ? "/" : rest)}";
            Assert.Contains(unprefixed, plainEndpoints);
        }

        // Every endpoint not marked (Minimal API Map* routes) is identical to the plain run.
        var unmarked = prefixedEndpoints.Where(f => !marked.Contains(f)).ToList();
        Assert.NotEmpty(unmarked);
        Assert.All(unmarked, f => Assert.Contains(f, plainEndpoints));
    }

    [Fact]
    public async Task McpTools_SurfaceTheConventionNote_AndTheUserSuppliedPrefixMarker()
    {
        string dbPath = Path.Combine(_root, "graph.db");
        string programFile = Path.Combine(_root, "Program.cs");
        File.WriteAllText(programFile, "line one\noptions.Conventions.Add(new ApiRoutePrefixConvention(\"api\"));\n");

        var graph = new CodeGraph();
        var handler = SymbolNode.Create(NodeKind.Method, "Get", "Demo.ArticlesController.Get()", programFile, new SourceSpan(0, 1), "Public");
        var endpoint = SymbolNode.Create(NodeKind.Endpoint, "/api/articles", "GET /api/articles", programFile, new SourceSpan(0, 1));
        var callSite = SymbolNode.Create(NodeKind.FrontendCallSite, "/api/articles", "GET src/agent.js:1:1", Path.Combine(_root, "agent.js"), new SourceSpan(0, 1));
        graph.AddNode(handler);
        graph.AddNode(endpoint);
        graph.AddNode(callSite);
        graph.AddEdge(new RelationshipEdge(endpoint.Id, handler.Id, RelationshipKind.HandledBy));
        graph.AddEdge(new RelationshipEdge(callSite.Id, endpoint.Id, RelationshipKind.CallsEndpoint));
        graph.AddDisclosure(new Disclosure(DisclosureKinds.RouteConvention, "Demo.ApiRoutePrefixConvention (IApplicationModelConvention)", programFile, 9));
        graph.AddDisclosure(new Disclosure(
            DisclosureKinds.RouteConvention,
            "Microsoft.AspNetCore.Mvc.ApplicationModels.RouteTokenTransformerConvention (IActionModelConvention)",
            programFile,
            9));
        graph.AddDisclosure(new Disclosure(DisclosureKinds.RoutePrefixApplied, endpoint.Fqn, programFile, 0));

        await using (var seed = new SqliteGraphStore(dbPath))
        {
            await seed.SaveAsync(graph, [], new Dictionary<string, string>(StringComparer.Ordinal) { [MetaKeys.LastAnalyzed] = "test" });
        }

        await using var store = new SqliteGraphStore(dbPath);
        var queries = new SlnmapQueries(store);

        string list = await queries.ListEndpointsAsync(null, null);
        Assert.Contains("2 route convention(s) can rewrite controller routes at startup", list, StringComparison.Ordinal);
        Assert.Contains($"Demo.ApiRoutePrefixConvention (IApplicationModelConvention) — {programFile}:2", list, StringComparison.Ordinal);
        Assert.Contains("'ChangePassword' served as 'change-password'", list, StringComparison.Ordinal);
        Assert.Contains("GET /api/articles [prefix: user-supplied]", list, StringComparison.Ordinal);

        string found = await queries.FindEndpointAsync("/api/articles", null);
        Assert.Contains("[prefix: user-supplied]", found, StringComparison.Ordinal);
        Assert.Contains("route convention(s) can rewrite", found, StringComparison.Ordinal);

        // The no-match path is where a convention most often explains the miss.
        string missed = await queries.FindEndpointAsync("/nothing/here", null);
        Assert.Contains("route convention(s) can rewrite", missed, StringComparison.Ordinal);

        string callSites = await queries.ListFrontendCallSitesAsync(null, null);
        Assert.Contains("route convention(s) can rewrite", callSites, StringComparison.Ordinal);

        string orphans = await queries.FindOrphanCallsAsync(null);
        Assert.Contains("route convention(s) can rewrite", orphans, StringComparison.Ordinal);

        string impact = await queries.ImpactAnalysisAsync(handler.Fqn);
        Assert.Contains("GET /api/articles", impact, StringComparison.Ordinal);
        Assert.Contains("route convention(s) can rewrite", impact, StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpTools_NoConventions_NoNote()
    {
        string dbPath = Path.Combine(_root, "graph.db");
        var graph = new CodeGraph();
        graph.AddNode(SymbolNode.Create(NodeKind.Endpoint, "/api/articles", "GET /api/articles"));
        await using (var seed = new SqliteGraphStore(dbPath))
        {
            await seed.SaveAsync(graph, [], new Dictionary<string, string>(StringComparer.Ordinal) { [MetaKeys.LastAnalyzed] = "test" });
        }

        await using var store = new SqliteGraphStore(dbPath);
        string list = await new SlnmapQueries(store).ListEndpointsAsync(null, null);
        Assert.DoesNotContain("route convention", list, StringComparison.Ordinal);
        Assert.DoesNotContain("[prefix: user-supplied]", list, StringComparison.Ordinal);
    }

    private string CopyFixtureSolution()
    {
        Directory.CreateDirectory(_root);
        foreach (string file in Directory.EnumerateFiles(TestPaths.FixtureSolutionDirectory, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(TestPaths.FixtureSolutionDirectory, file);
            if (relative.StartsWith("bin", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("obj", StringComparison.OrdinalIgnoreCase)
                || relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string target = Path.Combine(_root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        DotNet.Run("restore FixtureSolution.sln", _root);
        return Path.Combine(_root, "FixtureSolution.sln");
    }
}
