using System.Text.Json;
using System.Text.Json.Serialization;

namespace Slnmap.Mcp;

/// <summary>
/// The canonical machine-checkable failure payload every tool failure returns — as a NORMAL tool
/// result, never a protocol-level error (clients render isError inconsistently; a plain result
/// always reaches the model). The shape is the r/mcp community design: an explicit status/code a
/// model can branch on, actionable STATE (the offending parameter and the valid list — a model
/// can act on a list, not on an exception string), a CORRECTIVE message, and a hint that
/// distinguishes fix-the-call from retry-as-is. Success results are prose text and must never be
/// parseable as this shape — "a failure must never share a shape with a success".
/// </summary>
public static class ToolFailure
{
    /// <summary>Codes a model can branch on. Not-found is deliberately NOT here: no results is a valid answer, not a failure.</summary>
    public const string CodeInvalidParameter = "invalid_parameter";
    public const string CodeMissingParameter = "missing_parameter";
    public const string CodeInternalError = "internal_error";

    /// <summary>Remediation hints: <c>fix_call</c> = the call is malformed, retrying as-is can never succeed; <c>retry</c> = the call was fine, the failure is environmental.</summary>
    public const string HintFixCall = "fix_call";
    public const string HintRetry = "retry";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // The payload is read by a model inside a text content block — never an HTML context —
        // so keep apostrophes and punctuation literal instead of \u-escaped noise.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string InvalidParameter(string invalidParameter, IReadOnlyList<string> validParameters, string message) =>
        JsonSerializer.Serialize(
            new Payload("error", CodeInvalidParameter, message, HintFixCall)
            {
                InvalidParameter = invalidParameter,
                ValidParameters = validParameters,
            },
            SerializerOptions);

    public static string MissingParameter(string missingParameter, IReadOnlyList<string> validParameters, string message) =>
        JsonSerializer.Serialize(
            new Payload("error", CodeMissingParameter, message, HintFixCall)
            {
                MissingParameter = missingParameter,
                ValidParameters = validParameters,
            },
            SerializerOptions);

    public static string InternalError(string message) =>
        JsonSerializer.Serialize(new Payload("error", CodeInternalError, message, HintRetry), SerializerOptions);

    /// <summary>
    /// True when a tool result text IS the failure payload — the machine check clients and tests
    /// use to distinguish failures from successes (rule 2). Successes are prose; only payloads
    /// serialized by this class parse as a JSON object with status == "error".
    /// </summary>
    public static bool IsFailurePayload(string resultText)
    {
        string trimmed = resultText.TrimStart();
        if (!trimmed.StartsWith('{'))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && status.GetString() == "error";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record Payload(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("code")] string Code,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("hint")] string Hint)
    {
        [JsonPropertyName("invalid_parameter")]
        public string? InvalidParameter { get; init; }

        [JsonPropertyName("missing_parameter")]
        public string? MissingParameter { get; init; }

        [JsonPropertyName("valid_parameters")]
        public IReadOnlyList<string>? ValidParameters { get; init; }
    }
}
