using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Slnmap.Core.Storage;

namespace Slnmap.Mcp;

/// <summary>Hosts the Slnmap MCP server over stdio.</summary>
public static class McpServerHost
{
    /// <summary>Runs the server until <paramref name="cancellationToken"/> is signalled or stdin closes.</summary>
    public static async Task RunAsync(IGraphStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);

        // Ensure the database and schema exist so tools return a clean "not analyzed yet" message
        // rather than failing on a missing table when the graph has never been built.
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);

        var builder = Host.CreateApplicationBuilder();

        // stdout carries the MCP protocol; all logging must go to stderr. Default to Warning so the
        // server is quiet in normal use (the SDK logs each request at Information otherwise).
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddSingleton(store);
        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        // stdout is reserved for the JSON-RPC protocol; this readiness note goes to stderr so a human
        // who runs `slnmap serve` by hand sees the process is listening rather than a silent hang.
        Console.Error.WriteLine("Slnmap MCP server ready on stdio; waiting for a client...");

        await builder.Build().RunAsync(cancellationToken).ConfigureAwait(false);
    }
}
