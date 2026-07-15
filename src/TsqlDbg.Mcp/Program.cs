using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TsqlDbg.Mcp;

// DESIGN §24.2: the MCP stdio host. stdout is the JSON-RPC wire — like the DAP adapter's
// stdout (§3/§19), NOTHING may Console.WriteLine here. All logging goes to stderr.
var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

// DESIGN §24.9: server-level config from args + environment (allowlist location, limits,
// trace dir). Registered as a singleton so the SessionRegistry and tools share it.
var config = McpServerConfig.FromArgsAndEnvironment(args, Environment.GetEnvironmentVariable);
builder.Services.AddSingleton(config);

// DESIGN §24.2: the session registry — a singleton IAsyncDisposable, so the DI container
// tears every live session down (unconditional rollback) on host shutdown / lost stdio.
builder.Services.AddSingleton(sp => new SessionRegistry(sp.GetRequiredService<McpServerConfig>()));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync().ConfigureAwait(false);
