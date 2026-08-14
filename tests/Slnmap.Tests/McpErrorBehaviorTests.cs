using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Server;
using Slnmap.Mcp;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// The failure-shape contract at the query layer, against the real analyzed fixture graph:
/// in-handler VALUE guards emit the canonical shape (they used to be prose-only — a failure
/// shaped exactly like a success), not-found stays a normal prose answer (no results is a valid
/// answer, not a malfunction), success payloads never parse as the failure shape, and every tool
/// description's example invocation actually executes cleanly — a description example can never
/// rot into a lie.
/// </summary>
public sealed class McpErrorBehaviorTests : IClassFixture<AnalyzedFixtureGraphStore>
{
    private readonly SlnmapQueries _queries;

    public McpErrorBehaviorTests(AnalyzedFixtureGraphStore fixture) => _queries = new SlnmapQueries(fixture.Store);

    [Fact]
    public async Task ValueGuards_EmitTheCanonicalShape()
    {
        var invalidKind = await _queries.FindSymbolAsync("Shape", "Widget");
        var kindPayload = McpFailureShapeTests.AssertFailureShape(invalidKind, ToolFailure.CodeInvalidParameter, ToolFailure.HintFixCall);
        Assert.Equal("kind", kindPayload.GetProperty("invalid_parameter").GetString());
        Assert.Contains("Unknown kind 'Widget'", kindPayload.GetProperty("message").GetString(), StringComparison.Ordinal);

        var invalidDirection = await _queries.GetDependenciesAsync("Fixture.Lib.Circle", "sideways", 1);
        var directionPayload = McpFailureShapeTests.AssertFailureShape(invalidDirection, ToolFailure.CodeInvalidParameter, ToolFailure.HintFixCall);
        Assert.Equal("direction", directionPayload.GetProperty("invalid_parameter").GetString());

        var invalidHierarchyDirection = await _queries.GetTypeHierarchyAsync("Fixture.Lib.ShapeBase", "diagonal", 3);
        McpFailureShapeTests.AssertFailureShape(invalidHierarchyDirection, ToolFailure.CodeInvalidParameter, ToolFailure.HintFixCall);

        var invalidVerb = await _queries.ListEndpointsAsync("FETCH", null);
        var verbPayload = McpFailureShapeTests.AssertFailureShape(invalidVerb, ToolFailure.CodeInvalidParameter, ToolFailure.HintFixCall);
        Assert.Equal("verb", verbPayload.GetProperty("invalid_parameter").GetString());

        var emptyQuery = await _queries.FindSymbolAsync("   ", null);
        var queryPayload = McpFailureShapeTests.AssertFailureShape(emptyQuery, ToolFailure.CodeMissingParameter, ToolFailure.HintFixCall);
        Assert.Equal("query", queryPayload.GetProperty("missing_parameter").GetString());

        var emptyRoute = await _queries.FindEndpointAsync("  ", null);
        var routePayload = McpFailureShapeTests.AssertFailureShape(emptyRoute, ToolFailure.CodeMissingParameter, ToolFailure.HintFixCall);
        Assert.Equal("route", routePayload.GetProperty("missing_parameter").GetString());
    }

    [Fact]
    public async Task NotFound_StaysAProseAnswer_NeverTheFailureShape()
    {
        // No results is a valid answer, not a failure — giving it the error shape would train
        // models to treat "0 usages" as a malfunction.
        foreach (string result in new[]
        {
            await _queries.FindSymbolAsync("Nonexistoid", null),
            await _queries.FindUsagesAsync("Fixture.Lib.Circle.Are()"),
            await _queries.ImpactAnalysisAsync("Fixture.Lib.Nothing"),
            await _queries.GetProjectDependenciesAsync("NoSuchProject"),
            await _queries.FindEndpointAsync("/nope/xyz", null),
        })
        {
            Assert.False(ToolFailure.IsFailurePayload(result), $"not-found came back failure-shaped: {result}");
        }
    }

    [Fact]
    public async Task SuccessResults_NeverParseAsTheFailureShape()
    {
        foreach (string result in new[]
        {
            await _queries.FindSymbolAsync("IShape", null),
            await _queries.FindUsagesAsync("Fixture.Lib.IShape.Area()"),
            await _queries.ImpactAnalysisAsync("Fixture.Lib.IShape.Area()"),
            await _queries.GetArchitectureOverviewAsync(),
            await _queries.ListEndpointsAsync(null, null),
            await _queries.FindEndpointAsync("/api/vendors/42", null),
        })
        {
            Assert.False(ToolFailure.IsFailurePayload(result), $"success came back failure-shaped: {result}");
        }
    }

    [Fact]
    public async Task EveryDescriptionExample_ExecutesCleanlyAgainstTheFixtureGraph()
    {
        foreach (var method in typeof(SlnmapTools).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            var attribute = method.GetCustomAttribute<McpServerToolAttribute>();
            if (attribute?.Name is null)
            {
                continue;
            }

            string description = method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()!.Description;
            var args = McpFailureShapeTests.ParseExample(description);
            string result = await InvokeAsync(attribute.Name, args);

            Assert.False(string.IsNullOrWhiteSpace(result), $"{attribute.Name} example returned nothing");
            Assert.False(ToolFailure.IsFailurePayload(result), $"{attribute.Name} example failed: {result}");
        }
    }

    private Task<string> InvokeAsync(string tool, Dictionary<string, JsonElement> args)
    {
        string? Str(string name) => args.TryGetValue(name, out var v) ? v.GetString() : null;
        int Int(string name, int fallback) => args.TryGetValue(name, out var v) ? v.GetInt32() : fallback;

        return tool switch
        {
            "find_symbol" => _queries.FindSymbolAsync(Str("query")!, Str("kind")),
            "get_dependencies" => _queries.GetDependenciesAsync(Str("fqn")!, Str("direction") ?? "outgoing", Int("depth", 1)),
            "impact_analysis" => _queries.ImpactAnalysisAsync(Str("fqn")!),
            "get_architecture_overview" => _queries.GetArchitectureOverviewAsync(),
            "find_usages" => _queries.FindUsagesAsync(Str("fqn")!),
            "find_implementations" => _queries.FindImplementationsAsync(Str("fqn")!),
            "get_type_hierarchy" => _queries.GetTypeHierarchyAsync(Str("fqn")!, Str("direction") ?? "both", Int("depth", 5)),
            "find_tests_for_symbol" => _queries.FindTestsForSymbolAsync(Str("fqn")!),
            "get_project_dependencies" => _queries.GetProjectDependenciesAsync(Str("project") ?? "all"),
            "find_circular_dependencies" => _queries.FindCircularDependenciesAsync(Str("scope") ?? "project"),
            "get_symbol_source" => _queries.GetSymbolSourceAsync(Str("fqn")!, Int("context_lines", 5)),
            "list_endpoints" => _queries.ListEndpointsAsync(Str("verb"), Str("prefix")),
            "find_endpoint" => _queries.FindEndpointAsync(Str("route")!, Str("verb")),
            _ => throw new InvalidOperationException($"example dispatch is missing tool '{tool}'"),
        };
    }
}
