using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using PlanForge.Mcp;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();   // stdout carries the protocol

builder.Services
    .AddMcpServer(o => o.ServerInfo = new Implementation { Name = "planforge", Version = "2.0.0" })
    .WithStdioServerTransport()
    .WithTools<ForgeTools>();

await builder.Build().RunAsync();
