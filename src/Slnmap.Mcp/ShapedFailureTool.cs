using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Slnmap.Mcp;

/// <summary>
/// The single choke point every tool call flows through (all 13 tools are wrapped at registration
/// in <see cref="McpServerHost"/>): pre-dispatch argument validation against the tool's own
/// advertised schema, and a catch-all that converts any escaping exception into the canonical
/// failure payload. Both paths return NORMAL tool results (never protocol-level isError), so
/// sanitization is structural — no handler can leak a stack trace, file path, or internal type
/// name into a payload, because no handler exception ever reaches one. Full exception detail
/// still goes to stderr: stderr is for humans, the payload is the API.
/// </summary>
internal sealed class ShapedFailureTool : DelegatingMcpServerTool
{
    public ShapedFailureTool(McpServerTool innerTool)
        : base(innerTool)
    {
    }

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        if (ToolCallValidator.Validate(ProtocolTool.InputSchema, request.Params?.Arguments) is { } invalid)
        {
            return Failure(invalid);
        }

        try
        {
            return await base.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            // Humans tailing the server get the whole story; the model gets a sanitized,
            // actionable payload (the call was well-formed — environmental failures are worth
            // one retry; a persistent one usually means the graph db needs a rebuild).
            Console.Error.WriteLine($"[slnmap] tool '{ProtocolTool.Name}' failed: {e}");
            return Failure(ToolFailure.InternalError(
                $"'{ProtocolTool.Name}' failed unexpectedly while executing. The call was well-formed — retry once; "
                + "if it persists, the graph database may be missing or corrupt: re-run 'slnmap analyze' and try again."));
        }
    }

    private static CallToolResult Failure(string payload) => new()
    {
        Content = [new TextContentBlock { Text = payload }],
        // Deliberately NOT IsError: clients render protocol-level errors inconsistently; a normal
        // result with a machine-checkable status field always reaches the model.
    };
}
