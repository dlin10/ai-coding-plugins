using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using PlanForge.Diagnostics;
using PlanForge.Jobs;
using PlanForge.Mcp;

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
    .AddHostedService<JobRegistryHostedService>()
    .Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(2))
    .AddMcpServer(o => o.ServerInfo = new Implementation { Name = "planforge", Version = version })
    .WithStdioServerTransport()
    .WithTools<ForgeTools>();

await builder.Build().RunAsync();
