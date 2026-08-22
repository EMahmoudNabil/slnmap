using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Slnmap.Core.Storage;
using Slnmap.Mcp;
using Slnmap.Storage;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// The r/mcp failure-shape contract (reports/mcp-error-shape-report.md), unit level: for every
/// tool's REAL advertised schema, a wrong parameter name, a missing/empty required parameter,
/// and a wrong JSON type each produce the canonical machine-checkable payload — corrective
/// message, offending parameter, valid list, code + hint — while each tool's own description
/// example validates clean. Sanitization is asserted by pattern: no stack frames, no absolute
/// paths, no exception type names in any payload.
/// </summary>
public sealed class McpFailureShapeTests
{
    /// <summary>All 15 tools with their real schemas, exactly as the server advertises them.</summary>
    private static readonly IReadOnlyList<(string Name, string Description, JsonElement InputSchema)> Tools = BuildTools();

    private static IReadOnlyList<(string, string, JsonElement)> BuildTools()
    {
        // Schema generation must see IGraphStore as DI-resolved (as the real host does), so it is
        // excluded from the advertised parameters. The store is never opened here.
        var services = new ServiceCollection();
        services.AddSingleton<IGraphStore>(new SqliteGraphStore(Path.Combine(Path.GetTempPath(), "never-opened.db")));
        var provider = services.BuildServiceProvider();

        var tools = new List<(string, string, JsonElement)>();
        foreach (var method in typeof(SlnmapTools).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            var attribute = method.GetCustomAttribute<McpServerToolAttribute>();
            if (attribute?.Name is null)
            {
                continue;
            }

            var tool = McpServerTool.Create(method, target: null, new McpServerToolCreateOptions { Services = provider });
            string description = method.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description ?? string.Empty;
            tools.Add((attribute.Name, description, tool.ProtocolTool.InputSchema));
        }

        return tools;
    }

    public static TheoryData<string> ToolNames()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _, _) in Tools)
        {
            data.Add(name);
        }

        return data;
    }

    private static (string Name, string Description, JsonElement InputSchema) Tool(string name) =>
        Tools.Single(t => t.Name == name);

    [Fact]
    public void AllFifteenToolsAreUnderTest()
    {
        Assert.Equal(15, Tools.Count);
    }

    // ---- case (a): wrong parameter name ---------------------------------------------------------

    [Theory]
    [MemberData(nameof(ToolNames))]
    public void WrongParameterName_ReturnsInvalidParameterShape(string toolName)
    {
        var (_, _, schema) = Tool(toolName);
        var arguments = new Dictionary<string, JsonElement> { ["symbol"] = JsonDocument.Parse("\"X\"").RootElement };

        string? failure = ToolCallValidator.Validate(schema, arguments);

        Assert.NotNull(failure);
        var payload = AssertFailureShape(failure!, ToolFailure.CodeInvalidParameter, ToolFailure.HintFixCall);
        Assert.Equal("symbol", payload.GetProperty("invalid_parameter").GetString());
        Assert.Contains("unknown parameter 'symbol'", payload.GetProperty("message").GetString(), StringComparison.Ordinal);
        AssertValidParametersMatchSchema(payload, schema);
    }

    // ---- case (b): missing / empty required parameter -------------------------------------------

    public static TheoryData<string, string> RequiredParameterTools()
    {
        var data = new TheoryData<string, string>();
        foreach (var (name, _, schema) in Tools)
        {
            if (schema.TryGetProperty("required", out var required) && required.GetArrayLength() > 0)
            {
                data.Add(name, required[0].GetString()!);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RequiredParameterTools))]
    public void MissingRequiredParameter_ReturnsMissingParameterShape(string toolName, string requiredName)
    {
        var (_, _, schema) = Tool(toolName);

        string? failure = ToolCallValidator.Validate(schema, new Dictionary<string, JsonElement>());

        Assert.NotNull(failure);
        var payload = AssertFailureShape(failure!, ToolFailure.CodeMissingParameter, ToolFailure.HintFixCall);
        Assert.Equal(requiredName, payload.GetProperty("missing_parameter").GetString());
        Assert.Contains($"missing required parameter '{requiredName}'", payload.GetProperty("message").GetString(), StringComparison.Ordinal);
        AssertValidParametersMatchSchema(payload, schema);
    }

    [Theory]
    [MemberData(nameof(RequiredParameterTools))]
    public void EmptyRequiredParameter_ReturnsMissingParameterShape(string toolName, string requiredName)
    {
        var (_, _, schema) = Tool(toolName);
        var arguments = new Dictionary<string, JsonElement> { [requiredName] = JsonDocument.Parse("\"  \"").RootElement };

        string? failure = ToolCallValidator.Validate(schema, arguments);

        Assert.NotNull(failure);
        var payload = AssertFailureShape(failure!, ToolFailure.CodeMissingParameter, ToolFailure.HintFixCall);
        Assert.Equal(requiredName, payload.GetProperty("missing_parameter").GetString());
    }

    [Theory]
    [MemberData(nameof(RequiredParameterTools))]
    public void WrongParameterType_ReturnsInvalidParameterShape(string toolName, string requiredName)
    {
        var (_, _, schema) = Tool(toolName);
        // Every required parameter across the 15 tools is a string; a number is always the wrong type.
        var arguments = new Dictionary<string, JsonElement> { [requiredName] = JsonDocument.Parse("42").RootElement };

        string? failure = ToolCallValidator.Validate(schema, arguments);

        Assert.NotNull(failure);
        var payload = AssertFailureShape(failure!, ToolFailure.CodeInvalidParameter, ToolFailure.HintFixCall);
        Assert.Equal(requiredName, payload.GetProperty("invalid_parameter").GetString());
        Assert.Contains("must be a string", payload.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    // ---- prevention rule: every description carries a working example ---------------------------

    [Theory]
    [MemberData(nameof(ToolNames))]
    public void EveryDescription_CarriesAnExampleInvocation_ThatValidatesCleanly(string toolName)
    {
        var (_, description, schema) = Tool(toolName);
        var arguments = ParseExample(description);

        Assert.Null(ToolCallValidator.Validate(schema, arguments));
    }

    /// <summary>Extracts the trailing <c>Example: {...}</c> from a tool description.</summary>
    internal static Dictionary<string, JsonElement> ParseExample(string description)
    {
        var match = Regex.Match(description, @"Example: (\{.*\})", RegexOptions.Singleline);
        Assert.True(match.Success, "description has no 'Example: {...}' invocation");
        using var document = JsonDocument.Parse(match.Groups[1].Value);
        var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            arguments[property.Name] = property.Value.Clone();
        }

        return arguments;
    }

    // ---- sanitization + shape helpers ------------------------------------------------------------

    [Fact]
    public void InternalError_IsSanitizedAndRetryHinted()
    {
        string payload = ToolFailure.InternalError(
            "'find_usages' failed unexpectedly while executing. The call was well-formed — retry once; "
            + "if it persists, the graph database may be missing or corrupt: re-run 'slnmap analyze' and try again.");

        var root = AssertFailureShape(payload, ToolFailure.CodeInternalError, ToolFailure.HintRetry);
        Assert.Contains("re-run 'slnmap analyze'", root.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SuccessProse_IsNeverTheFailureShape()
    {
        Assert.False(ToolFailure.IsFailurePayload("2 usage(s) of Fixture.Lib.Circle (by containing member, up to 50):"));
        Assert.False(ToolFailure.IsFailurePayload("No symbol with FQN 'X'. Did you mean:"));
        Assert.False(ToolFailure.IsFailurePayload("{\"not\": \"an error\"}"));
    }

    /// <summary>Asserts the canonical shape and rule-6 sanitization; returns the parsed payload.</summary>
    internal static JsonElement AssertFailureShape(string payload, string expectedCode, string expectedHint)
    {
        Assert.True(ToolFailure.IsFailurePayload(payload), $"not the failure shape: {payload}");
        var root = JsonDocument.Parse(payload).RootElement.Clone();
        Assert.Equal("error", root.GetProperty("status").GetString());
        Assert.Equal(expectedCode, root.GetProperty("code").GetString());
        Assert.Equal(expectedHint, root.GetProperty("hint").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("message").GetString()));
        AssertSanitized(payload);
        return root;
    }

    /// <summary>Rule 6, asserted by pattern: no stack frames, no absolute/internal paths, no exception type names.</summary>
    internal static void AssertSanitized(string payload)
    {
        Assert.DoesNotContain("   at ", payload, StringComparison.Ordinal);
        Assert.DoesNotContain(".cs:", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", payload, StringComparison.Ordinal);
        Assert.DoesNotContain(":\\", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Slnmap.Storage", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("Slnmap.Mcp", payload, StringComparison.Ordinal);
    }

    private static void AssertValidParametersMatchSchema(JsonElement payload, JsonElement schema)
    {
        var expected = new List<string>();
        if (schema.TryGetProperty("properties", out var properties))
        {
            foreach (var property in properties.EnumerateObject())
            {
                expected.Add(property.Name);
            }
        }

        var actual = payload.GetProperty("valid_parameters").EnumerateArray().Select(e => e.GetString()!).ToList();
        Assert.Equal(expected, actual);
    }
}
