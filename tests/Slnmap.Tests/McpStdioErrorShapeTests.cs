using System.Diagnostics;
using System.Text.Json;
using Slnmap.Mcp;
using Xunit;

namespace Slnmap.Tests;

/// <summary>
/// The office-audit repro, replayed end-to-end through a real stdio JSON-RPC handshake against
/// the built CLI: `find_usages(symbol: …)` — which used to return the opaque protocol-level
/// `isError` "An error occurred invoking 'find_usages'." — must now reach the client as a NORMAL
/// result whose payload is the canonical corrective shape. Also proves the choke point end to
/// end for genuinely unexpected in-handler exceptions (the graph db corrupted mid-session):
/// sanitized internal_error to the client, never a stack trace in the payload.
/// </summary>
public sealed class McpStdioErrorShapeTests : IClassFixture<AnalyzedFixtureGraphStore>, IDisposable
{
    private readonly AnalyzedFixtureGraphStore _fixture;
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "slnmap-stdio-shape", Guid.NewGuid().ToString("N"));

    public McpStdioErrorShapeTests(AnalyzedFixtureGraphStore fixture)
    {
        _fixture = fixture;
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    [Fact]
    public async Task OfficeRepro_WrongParameterName_ReachesTheClientAsTheCorrectiveShape()
    {
        using var server = StartServer(_fixture.Store.DatabasePath);
        await server.InitializeAsync();

        var response = await server.CallToolAsync("find_usages", "{\"symbol\": \"Fixture.Lib.Circle.Area()\"}");

        // Rule 1: a NORMAL result — no protocol-level error flag.
        Assert.False(response.TryGetProperty("error", out _), "got a JSON-RPC error instead of a result");
        var result = response.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean(), "failure still uses isError");

        string text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        var payload = McpFailureShapeTests.AssertFailureShape(text, ToolFailure.CodeInvalidParameter, ToolFailure.HintFixCall);

        // Rules 3/4: the offending parameter, the valid list, and a corrective sentence.
        Assert.Equal("symbol", payload.GetProperty("invalid_parameter").GetString());
        Assert.Equal("fqn", Assert.Single(payload.GetProperty("valid_parameters").EnumerateArray()).GetString());
        Assert.Contains("unknown parameter 'symbol'", payload.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains("fqn (required", payload.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownParameterOnOptionalOnlyTool_IsNoLongerSilentlyIgnored()
    {
        using var server = StartServer(_fixture.Store.DatabasePath);
        await server.InitializeAsync();

        // Used to return the FULL unfiltered map as if the filter had applied — the field story's
        // "failure shaped exactly like a success".
        var response = await server.CallToolAsync("get_project_dependencies", "{\"projekt\": \"FixtureLib\"}");

        string text = response.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        var payload = McpFailureShapeTests.AssertFailureShape(text, ToolFailure.CodeInvalidParameter, ToolFailure.HintFixCall);
        Assert.Equal("projekt", payload.GetProperty("invalid_parameter").GetString());
    }

    [Fact]
    public async Task InHandlerException_ReachesTheClientAsSanitizedInternalError()
    {
        // A private copy of the analyzed db, corrupted AFTER the server has started: the store
        // opens connections per operation, so the next call throws deep inside the handler.
        string db = Path.Combine(_directory, "midcorrupt.db");
        File.Copy(_fixture.Store.DatabasePath, db);
        using var server = StartServer(db);
        await server.InitializeAsync();

        var healthy = await server.CallToolAsync("find_symbol", "{\"query\": \"Circle\"}");
        Assert.False(ToolFailure.IsFailurePayload(
            healthy.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!));

        File.WriteAllBytes(db, "garbage, not a sqlite database"u8.ToArray());

        var response = await server.CallToolAsync("find_usages", "{\"fqn\": \"Fixture.Lib.Circle\"}");
        var result = response.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean(), "failure still uses isError");

        string text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        var payload = McpFailureShapeTests.AssertFailureShape(text, ToolFailure.CodeInternalError, ToolFailure.HintRetry);
        Assert.Contains("re-run 'slnmap analyze'", payload.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    private static StdioServer StartServer(string databasePath)
    {
        string config = AppContext.BaseDirectory.Replace('\\', '/')
            .Contains("/Release/", StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";
        string cliDll = Path.Combine(TestPaths.RepoRoot, "src", "Slnmap.Cli", "bin", config, "net9.0", "slnmap.dll");
        Assert.True(File.Exists(cliDll), $"CLI not built at {cliDll}");

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(cliDll);
        psi.ArgumentList.Add("serve");
        psi.ArgumentList.Add("--db");
        psi.ArgumentList.Add(databasePath);

        return new StdioServer(Process.Start(psi) ?? throw new InvalidOperationException("Failed to start slnmap serve."));
    }

    /// <summary>Minimal line-delimited JSON-RPC client over the server's stdio (issue-#10-safe: bounded reads).</summary>
    private sealed class StdioServer(Process process) : IDisposable
    {
        private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(120);
        private int _nextId;

        public async Task InitializeAsync()
        {
            await SendAsync("""{"jsonrpc":"2.0","id":0,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"shape-test","version":"1.0"}}}""");
            await ReadResponseAsync(0);
            await SendAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        }

        public async Task<JsonElement> CallToolAsync(string tool, string argumentsJson)
        {
            int id = ++_nextId;
            await SendAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"tools/call\",\"params\":{{\"name\":\"{tool}\",\"arguments\":{argumentsJson}}}}}");
            return await ReadResponseAsync(id);
        }

        private async Task SendAsync(string json)
        {
            await process.StandardInput.WriteLineAsync(json);
            await process.StandardInput.FlushAsync();
        }

        private async Task<JsonElement> ReadResponseAsync(int id)
        {
            string output = await ProcessOutput.ReadUntilAsync(
                process.StandardOutput,
                line => line.Contains($"\"id\":{id}", StringComparison.Ordinal),
                ReadTimeout);
            string line = output.Split('\n')[^1];
            return JsonDocument.Parse(line).RootElement.Clone();
        }

        public void Dispose()
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }

            process.Dispose();
        }
    }
}
