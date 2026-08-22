using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
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

        // The error-shape choke point: rewrap every registered tool in ShapedFailureTool, so
        // argument validation and exception sanitization are structural (one class, 15 tools) —
        // never per-tool discipline. See reports/mcp-error-shape-report.md.
        foreach (var descriptor in builder.Services.Where(d => d.ServiceType == typeof(McpServerTool)).ToList())
        {
            builder.Services.Remove(descriptor);
            builder.Services.Add(ServiceDescriptor.Describe(
                typeof(McpServerTool),
                provider => new ShapedFailureTool((McpServerTool)CreateInner(provider, descriptor)),
                descriptor.Lifetime));
        }

        // stdout is reserved for the JSON-RPC protocol; this readiness note goes to stderr so a human
        // who runs `slnmap serve` by hand sees the process is listening rather than a silent hang.
        Console.Error.WriteLine("Slnmap MCP server ready on stdio; waiting for a client...");

        await builder.Build().RunAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Materializes the tool a service descriptor would have produced, whatever form the SDK registered it in.</summary>
    private static object CreateInner(IServiceProvider provider, ServiceDescriptor descriptor) =>
        descriptor.ImplementationInstance
        ?? descriptor.ImplementationFactory?.Invoke(provider)
        ?? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType!);
}
