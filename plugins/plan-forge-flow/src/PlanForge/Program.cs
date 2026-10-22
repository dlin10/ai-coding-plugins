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
using PlanForge.Orchestration;
using PlanForge.Run;
using PlanForge.Vendors;

// The binary's second entry point, the Telemetry collector (docs/adr/0032), serves no MCP, so it
// branches off before the host is built.
if (TelemetryCollector.IsCommand(args)) return await TelemetryCollector.RunAsync(args);

// The third, the resume card (docs/adr/0040), only reads a Run's files for the host's SessionStart hook.
if (ResumeCard.IsCommand(args)) return ResumeCard.Run(args, Console.In, Console.Out, Environment.GetEnvironmentVariable);

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();   // stdout carries the protocol

// The one remaining sink is the run folder: the SDK's own dispatch and transport entries are the
// only record of a call that dies before any act of ours writes anything.
builder.Logging.AddProvider(new RunFileLoggerProvider());

// Read rather than written down: package.ps1 stamps the assembly with the manifest version, so this
// is the one spelling of it that cannot drift. A literal here stayed at "2.0.0" across three
// releases, advertising a version that never shipped. Debug builds pass no version and report 1.0.0.
var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

// The one owner of Orchestrator phases and the call clock in this process. The server is the only
// place that reads the real environment and profile for the Host session and its Usage source; see
// docs/adr/0032.
var phases = new OrchestratorPhases(TimeProvider.System, UsageSourceChoice.ForProcess(), Environment.GetEnvironmentVariable);
OrchestratorPhases.Process = phases;

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
        // Outermost, so every call counts in the Run's clock from its arrival, however it ends.
        o.Filters.Request.CallToolFilters.Add(OrchestratorCall.Filter(phases));
        o.Filters.Request.CallToolFilters.Add(ToolErrors.Surfaced);
        o.Filters.Request.CallToolFilters.Add(ToolResultText.Compact);
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
return 0;
