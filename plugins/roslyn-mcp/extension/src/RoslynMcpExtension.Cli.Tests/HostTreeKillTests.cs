using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace RoslynMcpExtension.Cli.Tests;

/// <summary>
/// A host that ends its MCP server by killing the server's process tree, as Claude Code does with
/// <c>taskkill /PID &lt;server&gt; /T /F</c>, must leave the daemon and the solutions it holds to the next session.
/// </summary>
/// <param name="fixtures">The restored solution the tests load.</param>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class HostTreeKillTests(FixtureSolutions fixtures) : IDisposable
{
	private readonly string _pipe = Daemons.NewPipeName();

	public void Dispose() => Daemons.Kill(_pipe);

	[Fact]
	public async Task KillingTheShimsProcessTreeLeavesTheDaemonAndItsSolution()
	{
		int daemonId;
		string loadedAt;
		using (var shim = ShimProcess.Start(_pipe))
		{
			await shim.InitializeAsync();
			var load = await shim.CallToolAsync("roslyn_load", new { solution = fixtures.Solution });
			Assert.Equal("loaded", load["status"]!.GetValue<string>());
			daemonId = load["daemon"]!["processId"]!.GetValue<int>();
			loadedAt = load["loadedAt"]!.GetValue<string>();

			KillTree(shim.Process.Id);
			Assert.True(shim.Process.WaitForExit(10_000), "taskkill left the shim running.");
		}

		Assert.Equal(daemonId, Daemons.ServerProcessId(_pipe));
		using var next = ShimProcess.Start(_pipe);
		await next.InitializeAsync();
		var reload = await next.CallToolAsync("roslyn_load", new { solution = fixtures.Solution });
		Assert.Equal(loadedAt, reload["loadedAt"]!.GetValue<string>());
		Assert.Equal(daemonId, reload["daemon"]!["processId"]!.GetValue<int>());
	}

	/// <summary>Kills a process and its descendants the way Claude Code ends an MCP server it started.</summary>
	/// <param name="processId">The process at the root of the tree.</param>
	private static void KillTree(int processId)
	{
		var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "taskkill.exe"))
		{
			UseShellExecute = false,
			CreateNoWindow = true
		};
		foreach (var argument in new[] { "/PID", processId.ToString(), "/T", "/F" }) start.ArgumentList.Add(argument);
		using var taskkill = Process.Start(start)!;
		taskkill.WaitForExit();
		Assert.Equal(0, taskkill.ExitCode);
	}
}
