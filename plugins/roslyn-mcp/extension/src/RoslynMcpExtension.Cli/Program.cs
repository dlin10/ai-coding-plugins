using System.CommandLine;
using RoslynMcpExtension.Cli;

var pipeOption = new Option<string>("--pipe")
{
	Description = "Named pipe the shim and the daemon meet on; by default one per plugin version and Windows user",
	DefaultValueFactory = _ => DaemonPipe.DefaultName,
	Recursive = true
};

var mcpCommand = new Command("mcp", "Serves MCP over stdio and forwards the analysis to the per-user daemon");
mcpCommand.SetAction((parseResult, _) => ShimHost.RunAsync(parseResult.GetValue(pipeOption)!));

var daemonCommand = new Command("daemon", "Runs the per-user daemon that loads solutions and answers the shims");
daemonCommand.SetAction((parseResult, _) => DaemonServer.RunAsync(parseResult.GetValue(pipeOption)!));

var startDaemonCommand = new Command("start-daemon", "Starts the daemon and exits, which leaves the daemon outside its caller's process tree");
startDaemonCommand.SetAction(parseResult => DaemonLauncher.StartDaemon(parseResult.GetValue(pipeOption)!));

var rootCommand = new RootCommand("Roslyn MCP without Visual Studio") { pipeOption, mcpCommand, daemonCommand, startDaemonCommand };

return await rootCommand.Parse(args).InvokeAsync();
