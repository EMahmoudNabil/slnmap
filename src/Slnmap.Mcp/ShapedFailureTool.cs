using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Slnmap.Core.Storage;

namespace Slnmap.Mcp;

/// <summary>
/// The single choke point every tool call flows through (all 15 tools are wrapped at registration
/// in <see cref="McpServerHost"/>): pre-dispatch argument validation against the tool's own
/// advertised schema, and a catch-all that converts any escaping exception into the canonical
/// failure payload. Both paths return NORMAL tool results (never protocol-level isError), so
/// sanitization is structural — no handler can leak a stack trace, file path, or internal type
/// name into a payload, because no handler exception ever reaches one. Full exception detail
/// still goes to stderr: stderr is for humans, the payload is the API.
/// </summary>
internal sealed class ShapedFailureTool : DelegatingMcpServerTool
{
    private readonly IGraphStore? _store;

    /// <param name="store">
    /// The graph store, used to prefix every successful answer with the not-restored warning when
    /// the graph has one (v0.14.0). Null skips the note.
    /// </param>
    public ShapedFailureTool(McpServerTool innerTool, IGraphStore? store = null)
        : base(innerTool)
    {
        _store = store;
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
            var result = await base.InvokeAsync(request, cancellationToken).ConfigureAwait(false);
            return await WithNotRestoredNoteAsync(result, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// Prefixes the not-restored warning to a successful prose answer. Failure payloads are left
    /// untouched (they must stay machine-parseable), and a failure to read the note never fails the
    /// call — the answer is still returned.
    /// </summary>
    private async Task<CallToolResult> WithNotRestoredNoteAsync(CallToolResult result, CancellationToken cancellationToken)
    {
        if (_store is null
            || result.IsError == true
            || result.Content is not [TextContentBlock first, ..]
            || ToolFailure.IsFailurePayload(first.Text))
        {
            return result;
        }

        string? note;
        try
        {
            note = await new SlnmapQueries(_store).NotRestoredNoteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Console.Error.WriteLine($"[slnmap] could not read the not-restored note: {e.Message}");
            return result;
        }

        if (note is not null)
        {
            first.Text = note + Environment.NewLine + Environment.NewLine + first.Text;
        }

        return result;
    }

    private static CallToolResult Failure(string payload) => new()
    {
        Content = [new TextContentBlock { Text = payload }],
        // Deliberately NOT IsError: clients render protocol-level errors inconsistently; a normal
        // result with a machine-checkable status field always reaches the model.
    };
}
