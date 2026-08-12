using Slnmap.Analysis;
using Slnmap.Core.Storage;
using Slnmap.Mcp;
using Slnmap.Storage;
using Xunit;

namespace Slnmap.Tests;

/// <summary>Analyzes the fixture solution once and persists it to a temp database for the MCP tool tests.</summary>
public sealed class AnalyzedFixtureGraphStore : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "slnmap-mcp-tests", Guid.NewGuid().ToString("N"));

    public SqliteGraphStore Store { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        DotNet.Run($"restore \"{TestPaths.FixtureSolution}\"", TestPaths.RepoRoot);
        var snapshot = await new RoslynSolutionAnalyzer().AnalyzeAsync(TestPaths.FixtureSolution);
        Store = new SqliteGraphStore(Path.Combine(_directory, "graph.db"));
        var meta = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MetaKeys.SolutionPath] = TestPaths.FixtureSolution,
            [MetaKeys.LastAnalyzed] = "test",
            // Mirrors what `analyze` persists — the fixture has designed-unresolved registrations
            // (UnresolvedRegistrations.cs) and a conventionally-routed controller
            // (LegacyPagesController), and list_endpoints must disclose both.
            [MetaKeys.UnresolvedEndpoints] = snapshot.Stats.UnresolvedEndpoints.ToString(System.Globalization.CultureInfo.InvariantCulture),
            [MetaKeys.ConventionalControllers] = snapshot.Stats.ConventionalControllers.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        await Store.SaveAsync(snapshot.Graph, snapshot.Files, meta);
    }

    public async Task DisposeAsync()
    {
        await Store.DisposeAsync();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}

public sealed class McpToolTests : IClassFixture<AnalyzedFixtureGraphStore>
{
    private const string InterfaceAreaFqn = "Fixture.Lib.IShape.Area()";
    private const string TotalAreaFqn =
        "Fixture.Lib.Geometry.TotalArea(System.Collections.Generic.IEnumerable<Fixture.Lib.IShape>)";

    private readonly SlnmapQueries _queries;

    public McpToolTests(AnalyzedFixtureGraphStore fixture) => _queries = new SlnmapQueries(fixture.Store);

    [Fact]
    public async Task FindSymbol_MatchesByName()
    {
        string result = await _queries.FindSymbolAsync("IShape", null);
        Assert.Contains("Fixture.Lib.IShape", result, StringComparison.Ordinal);
        Assert.Contains("[Interface]", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindSymbol_KindFilter_RestrictsResults()
    {
        string result = await _queries.FindSymbolAsync("Area", "Method");
        Assert.Contains("Fixture.Lib.IShape.Area()", result, StringComparison.Ordinal);
        Assert.DoesNotContain("[Interface]", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetDependencies_Outgoing_ListsCallEdge()
    {
        string result = await _queries.GetDependenciesAsync(TotalAreaFqn, "outgoing", 1);
        Assert.Contains("Calls", result, StringComparison.Ordinal);
        Assert.Contains("Fixture.Lib.IShape.Area()", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetDependencies_Incoming_FindsCaller()
    {
        string result = await _queries.GetDependenciesAsync(InterfaceAreaFqn, "incoming", 1);
        Assert.Contains("TotalArea", result, StringComparison.Ordinal);
    }

    // The defining requirement: impact of an interface member must reach the concrete
    // implementations/overrides (Implements + Inherits), not just direct callers of the interface.
    [Fact]
    public async Task ImpactAnalysis_InterfaceMember_IncludesImplementations()
    {
        string result = await _queries.ImpactAnalysisAsync(InterfaceAreaFqn);

        Assert.Contains("Fixture.Lib.Circle.Area()", result, StringComparison.Ordinal);
        Assert.Contains("Fixture.Lib.Square.Area()", result, StringComparison.Ordinal);
        // ...and the call-site chain through the interface.
        Assert.Contains("Fixture.Lib.Geometry.TotalArea", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ArchitectureOverview_ListsProjectsAndKinds()
    {
        string result = await _queries.GetArchitectureOverviewAsync();
        Assert.Contains("FixtureLib", result, StringComparison.Ordinal);
        Assert.Contains("FixtureApp", result, StringComparison.Ordinal);
        Assert.Contains("Interface", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindUsages_ReportsReferencingMemberAndFile()
    {
        // The entry point does `new Circle(...)`, producing a References edge into Circle.
        string result = await _queries.FindUsagesAsync("Fixture.Lib.Circle");
        Assert.Contains("Program.cs", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownFqn_ReturnsDidYouMeanSuggestions()
    {
        // "Are" is a typo of "Area"; suggestions should surface the real members.
        string result = await _queries.FindUsagesAsync("Fixture.Lib.Circle.Are()");
        Assert.Contains("Did you mean", result, StringComparison.Ordinal);
        Assert.Contains("Area", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindUsages_GenericTypeArgumentOnlyReference_FindsIt()
    {
        // The false-"safe-to-delete" case: a class referenced only via
        // Registrar.Register<GenericMethodArgOnly>() must not report "no usages found".
        string result = await _queries.FindUsagesAsync("Fixture.Lib.GenericMethodArgOnly");
        Assert.Contains("Fixture.Lib.GenericRefs.UseAll()", result, StringComparison.Ordinal);
        Assert.DoesNotContain("No usages found", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImpactAnalysis_GenericTypeArgumentOnlyReference_ReportsReferencingMember()
    {
        string result = await _queries.ImpactAnalysisAsync("Fixture.Lib.GenericMethodArgOnly");
        Assert.Contains("Fixture.Lib.GenericRefs.UseAll()", result, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing else in the graph depends on it", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindUsages_BareTypeofOnlyReference_FindsIt()
    {
        string result = await _queries.FindUsagesAsync("Fixture.Lib.TypeofOnly");
        Assert.Contains("Fixture.Lib.GenericRefs.UseAll()", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindUsages_AttributeArgumentOnlyReference_FindsIt()
    {
        string result = await _queries.FindUsagesAsync("Fixture.Lib.AttributeArgOnly");
        Assert.Contains("Fixture.Lib.Marked", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindSymbol_FieldByName_ReturnsFieldKind()
    {
        string result = await _queries.FindSymbolAsync("KnownTypes", null);
        Assert.Contains("[Field]", result, StringComparison.Ordinal);
        Assert.Contains("Fixture.Lib.FieldHolder.KnownTypes", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindSymbol_FieldKindFilter_RestrictsResults()
    {
        string result = await _queries.FindSymbolAsync("_a", "Field");
        Assert.Contains("Fixture.Lib.MultiDeclaratorFields._a", result, StringComparison.Ordinal);
    }

    // ---- endpoint tools (v0.7.0) --------------------------------------------------------------

    [Fact]
    public async Task ListEndpoints_ListsRoutesWithHandlersAndLocations()
    {
        string result = await _queries.ListEndpointsAsync(null, null);
        Assert.Contains("GET /api/vendors", result, StringComparison.Ordinal);
        Assert.Contains("Fixture.Web.VendorEndpoints.ListVendors()", result, StringComparison.Ordinal);
        Assert.Contains("VendorEndpoints.cs:", result, StringComparison.Ordinal);
        // Grouped by project, like the other relationship tools.
        Assert.Contains("FixtureWeb:", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListEndpoints_DisclosesTheUnresolvedCount()
    {
        // The fixture ships designed-unresolved registrations; the listing must say so rather
        // than read as complete coverage.
        string result = await _queries.ListEndpointsAsync(null, null);
        Assert.Contains("could not be resolved statically", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListEndpoints_DisclosesConventionallyRoutedControllers()
    {
        // The v1.1 amended decision: a conventionally-routed controller (LegacyPagesController)
        // is disclosed as a different routing system — so "why is my MVC controller missing?" is
        // never a silent mystery — without polluting the unresolved count.
        string result = await _queries.ListEndpointsAsync(null, null);
        Assert.Contains("conventionally-routed controller", result, StringComparison.Ordinal);
        Assert.Contains("not an extraction failure", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListEndpoints_IncludesControllerEndpoints()
    {
        string result = await _queries.ListEndpointsAsync("delete", null);
        Assert.Contains("DELETE /maintenance/purge", result, StringComparison.Ordinal);
        Assert.Contains("Fixture.Web.StatusController.Purge()", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImpactAnalysis_ControllerAction_SurfacesItsEndpoint()
    {
        string result = await _queries.ImpactAnalysisAsync("Fixture.Web.ReportsController.RebuildAsync()");
        Assert.Contains("POST /Reports/Rebuild", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListEndpoints_VerbFilter_RestrictsResults()
    {
        string result = await _queries.ListEndpointsAsync("post", null);
        Assert.Contains("POST /api/vendors/{id}", result, StringComparison.Ordinal);
        Assert.DoesNotContain("GET /", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListEndpoints_PrefixFilter_RestrictsResults()
    {
        string result = await _queries.ListEndpointsAsync(null, "/api/reminders");
        Assert.Contains("/api/Reminders", result, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/vendors", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListEndpoints_UnknownVerb_NamesTheValidOnes()
    {
        // Issue #15-era lesson: a wrong input must produce a self-correcting message.
        string result = await _queries.ListEndpointsAsync("FETCH", null);
        Assert.Contains("Unknown verb 'FETCH'", result, StringComparison.Ordinal);
        Assert.Contains("GET, POST, PUT, DELETE, PATCH", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindEndpoint_ExactTemplate_ReturnsEndpointAndHandler()
    {
        string result = await _queries.FindEndpointAsync("/api/vendors/{id}", null);
        Assert.Contains("POST /api/vendors/{id}", result, StringComparison.Ordinal);
        Assert.Contains("Fixture.Web.VendorEndpoints.UpdateVendor(string)", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindEndpoint_ConcretePath_BindsTheParameterHole()
    {
        // A real request path matches its template: {id} binds "42".
        string result = await _queries.FindEndpointAsync("/api/vendors/42", null);
        Assert.Contains("POST /api/vendors/{id}", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindEndpoint_MatchesCaseInsensitively()
    {
        string result = await _queries.FindEndpointAsync("/API/VENDORS", "get");
        Assert.Contains("GET /api/vendors", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindEndpoint_Miss_SuggestsNearRoutes()
    {
        string result = await _queries.FindEndpointAsync("/vendors/archive", null);
        Assert.Contains("No endpoint matches", result, StringComparison.Ordinal);
        Assert.Contains("Did you mean", result, StringComparison.Ordinal);
        Assert.Contains("/api/vendors/archive", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindEndpoint_EmptyRoute_ExplainsTheParameter()
    {
        string result = await _queries.FindEndpointAsync("  ", null);
        Assert.Contains("Provide a route", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindUsages_Handler_SurfacesItsEndpoint()
    {
        // The one-line allow-list change: HandledBy is a usage kind, so a handler's endpoints
        // appear in find_usages with no traversal special-casing.
        string result = await _queries.FindUsagesAsync("Fixture.Web.VendorEndpoints.ListVendors()");
        Assert.Contains("[Endpoint] GET /api/vendors", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImpactAnalysis_Handler_SurfacesItsEndpoint()
    {
        // Edge orientation Endpoint —HandledBy→ Method: the endpoint is what breaks when the
        // handler changes, and the incoming walk finds it with zero special-casing.
        string result = await _queries.ImpactAnalysisAsync("Fixture.Web.VendorEndpoints.ArchiveSnapshot()");
        Assert.Contains("GET /api/vendors/archive", result, StringComparison.Ordinal);
        Assert.Contains("POST /api/vendors/archive", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindSymbol_EndpointKindFilter_Works()
    {
        // kind:Endpoint works the moment the enum member exists (TryParse ignoreCase).
        string result = await _queries.FindSymbolAsync("vendors", "Endpoint");
        Assert.Contains("[Endpoint] GET /api/vendors", result, StringComparison.Ordinal);
        Assert.DoesNotContain("[Class]", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotAnalyzedDatabase_ReturnsActionableMessage()
    {
        string directory = Path.Combine(Path.GetTempPath(), "slnmap-mcp-empty", Guid.NewGuid().ToString("N"));
        await using var store = new SqliteGraphStore(Path.Combine(directory, "empty.db"));
        await store.InitializeAsync();
        try
        {
            string result = await new SlnmapQueries(store).FindSymbolAsync("anything", null);
            Assert.Contains("slnmap analyze", result, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }
}
