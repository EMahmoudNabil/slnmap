using System.Text;
using System.Text.Json;

namespace Slnmap.Mcp;

/// <summary>
/// Pre-dispatch argument validation, driven by the tool's own advertised input schema — the same
/// JSON the client was shown, so the validator can never drift from the contract. Runs BEFORE the
/// SDK's reflection marshaller (which throws an opaque ArgumentException the client only ever saw
/// as "An error occurred invoking 'X'"). Catches: unknown argument names (including extras
/// alongside valid ones — previously dropped SILENTLY, the worst cell of the audit), missing or
/// empty required arguments, and JSON-type mismatches.
/// </summary>
public static class ToolCallValidator
{
    /// <summary>
    /// Validates <paramref name="arguments"/> against <paramref name="inputSchema"/>
    /// (a standard {"type":"object","properties":{...},"required":[...]} schema).
    /// Returns the canonical failure payload, or null when the call is well-formed.
    /// </summary>
    public static string? Validate(JsonElement inputSchema, IDictionary<string, JsonElement>? arguments)
    {
        var properties = new List<(string Name, string? Type, string? Description, bool Required)>();
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (inputSchema.ValueKind == JsonValueKind.Object)
        {
            if (inputSchema.TryGetProperty("required", out var requiredElement) && requiredElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var name in requiredElement.EnumerateArray())
                {
                    if (name.ValueKind == JsonValueKind.String)
                    {
                        required.Add(name.GetString()!);
                    }
                }
            }

            if (inputSchema.TryGetProperty("properties", out var propertiesElement) && propertiesElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in propertiesElement.EnumerateObject())
                {
                    properties.Add((
                        property.Name,
                        SchemaTypeOf(property.Value),
                        property.Value.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
                        required.Contains(property.Name)));
                }
            }
        }

        var validNames = properties.Select(p => p.Name).ToList();

        if (arguments is not null)
        {
            foreach (var (name, value) in arguments)
            {
                var property = properties.FirstOrDefault(p => p.Name == name);
                if (property.Name is null)
                {
                    return ToolFailure.InvalidParameter(
                        name,
                        validNames,
                        $"unknown parameter '{name}' — this tool takes {Summarize(properties)}.");
                }

                if (!TypeMatches(property.Type, value.ValueKind, property.Required))
                {
                    return ToolFailure.InvalidParameter(
                        name,
                        validNames,
                        $"parameter '{name}' must be a {property.Type}, but a {Describe(value.ValueKind)} was given — this tool takes {Summarize(properties)}.");
                }
            }
        }

        foreach (var property in properties.Where(p => p.Required))
        {
            bool absent = arguments is null
                || !arguments.TryGetValue(property.Name, out var value)
                || value.ValueKind == JsonValueKind.Null
                || (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString()));
            if (absent)
            {
                string description = property.Description is { Length: > 0 } d ? $" — {char.ToLowerInvariant(d[0])}{d[1..].TrimEnd('.')}" : string.Empty;
                return ToolFailure.MissingParameter(
                    property.Name,
                    validNames,
                    $"missing required parameter '{property.Name}'{description}. Call again with {Summarize(properties)}.");
            }
        }

        return null;
    }

    /// <summary>Human summary of the parameter contract, e.g. "fqn (required, string), direction (optional, string)" — or "no parameters".</summary>
    private static string Summarize(List<(string Name, string? Type, string? Description, bool Required)> properties)
    {
        if (properties.Count == 0)
        {
            return "no parameters";
        }

        var builder = new StringBuilder();
        for (int i = 0; i < properties.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            var (name, type, _, isRequired) = properties[i];
            builder.Append(name).Append(" (").Append(isRequired ? "required" : "optional");
            if (type is not null)
            {
                builder.Append(", ").Append(type);
            }

            builder.Append(')');
        }

        return builder.ToString();
    }

    /// <summary>The schema's primary type: "string" from {"type":"string"} or from {"type":["string","null"]}.</summary>
    private static string? SchemaTypeOf(JsonElement property)
    {
        if (!property.TryGetProperty("type", out var type))
        {
            return null;
        }

        if (type.ValueKind == JsonValueKind.String)
        {
            return type.GetString();
        }

        if (type.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in type.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { } s && s != "null")
                {
                    return s;
                }
            }
        }

        return null;
    }

    private static bool TypeMatches(string? schemaType, JsonValueKind actual, bool required) => schemaType switch
    {
        // Null for an optional parameter means "use the default" — the marshaller accepts it.
        _ when actual == JsonValueKind.Null && !required => true,
        null => true,
        "string" => actual == JsonValueKind.String,
        "integer" or "number" => actual == JsonValueKind.Number,
        "boolean" => actual is JsonValueKind.True or JsonValueKind.False,
        "array" => actual == JsonValueKind.Array,
        "object" => actual == JsonValueKind.Object,
        _ => true,
    };

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        JsonValueKind.Null => "null",
        _ => kind.ToString().ToLowerInvariant(),
    };
}
