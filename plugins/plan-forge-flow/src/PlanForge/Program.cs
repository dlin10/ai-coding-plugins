using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PlanForge.Diagnostics;
using PlanForge.Jobs;
using PlanForge.Mcp;
using PlanForge.Run;
using PlanForge.Vendors;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();   // stdout carries the protocol

// The one remaining sink is the run folder: the SDK's own dispatch and transport entries are the
// only record of a call that dies before any act of ours writes anything.
builder.Logging.AddProvider(new RunFileLoggerProvider());

// Read rather than written down: package.ps1 stamps the assembly with the manifest version, so this
// is the one spelling of it that cannot drift. A literal here stayed at "2.0.0" across three
// releases, advertising a version that never shipped. Debug builds pass no version and report 1.0.0.
var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

builder.Services
    .AddSingleton<JobRegistry>()
    .AddSingleton<CatalogCache>()
    // Resolved from the connection's own server, which the stdio transport registers as a singleton
    // beside these. A tool takes it the way it takes the two above — bound from services, so it
    // never reaches the published schema.
    .AddSingleton(services => new SessionRoots(services.GetRequiredService<McpServer>()))
    .AddHostedService<JobRegistryHostedService>()
    .Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(2))
    .AddMcpServer(o =>
    {
        o.ServerInfo = new Implementation { Name = "planforge", Version = version };
        o.Filters.Request.CallToolFilters.Add(ToolErrors.Surfaced);
    })
    .WithStdioServerTransport()
    // The SDK's own options plus this assembly's contract for the two structured arguments of
    // forge.begin; without it the server fails at startup describing a Dictionary it cannot see.
    .WithTools<ForgeTools>(ToolArgumentJson.ArgumentOptions)
    .WithResources<PlanCanvas>()
    // Reads the [McpAppUi] attributes the tools already carry and turns them into _meta.ui, so it
    // has to come after WithTools. Only forge.plan.show carries one; every other tool is untouched,
    // and a host that never negotiated the capability sees the same surface it always did.
    .WithMcpApps();

await builder.Build().RunAsync();
