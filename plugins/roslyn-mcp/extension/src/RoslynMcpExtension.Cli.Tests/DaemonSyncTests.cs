using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace RoslynMcpExtension.Cli.Tests;

/// <summary>
/// How a real daemon follows the disk and ends, through the shim: one project, App, whose Use.cs calls Api.Get in Api.cs.
/// Every test has pipes of its own and kills the daemons on them, and the ones it started, by their process IDs.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DaemonSyncTests : IDisposable
{
	private readonly string _pipe = Daemons.NewPipeName();
	private readonly UnrestoredSolutions _solutions = new();
	private readonly List<Process> _daemons = [];
	private readonly List<string> _pipes = [];
	private readonly string _solution;
	private readonly string _api;
	private readonly string _use;

	public DaemonSyncTests()
	{
		_pipes.Add(_pipe);
		_solutions.Write(@"App\App.csproj", UnrestoredSolutions.SdkProject());
		_api = _solutions.Write(@"App\Api.cs", "namespace App { public static class Api { public static int Get() => 1; } }");
		_use = _solutions.Write(@"App\Use.cs", "namespace App { static class Use { static int U() => Api.Get(); } }");
		_solution = _solutions.Solution("App.slnx", @"App\App.csproj");
	}

	public void Dispose()
	{
		foreach (var pipe in _pipes)
			Daemons.Kill(pipe);
		foreach (var daemon in _daemons)
		{
			if (!daemon.HasExited)
				daemon.Kill(entireProcessTree: true);
			daemon.Dispose();
		}

		_solutions.Dispose();
	}

	[Fact]
	public async Task AnEditIsSeenByTheVeryNextCall()
	{
		using var shim = ShimProcess.Start(_pipe);
		await shim.InitializeAsync();
		await LoadAsync(shim, _solution);
		var clean = await shim.CallToolAsync("roslyn_validate_file", new { filePath = _api });

		File.WriteAllText(_api, "namespace App { public static class Api { public static int Get() => Missing; } }");
		var broken = await shim.CallToolAsync("roslyn_validate_file", new { filePath = _api });

		Assert.True(clean["success"]!.GetValue<bool>(), clean.ToJsonString());
		Assert.Contains(broken["errors"]!.AsArray(), error => error!["id"]!.GetValue<string>() == "CS0103");
	}

	[Fact]
	public async Task AnEditToAnotherFileReachesTheNextCallsThroughTheWatcher()
	{
		using var shim = ShimProcess.Start(_pipe);
		await shim.InitializeAsync();
		await LoadAsync(shim, _solution);

		File.WriteAllText(_api, "namespace App { public static class Api { public static int Renamed() => 1; } }");
		// The watcher reports the edit within milliseconds, but not before the call returns: wait for it a while.
		JsonNode validation;
		var deadline = DateTime.UtcNow.AddSeconds(15);
		do
		{
			validation = await shim.CallToolAsync("roslyn_validate_file", new { filePath = _use });
		} while (validation["success"]!.GetValue<bool>() && DateTime.UtcNow < deadline && await Delay());

		Assert.Contains(validation["errors"]!.AsArray(), error => error!["id"]!.GetValue<string>() == "CS0117");
	}

	[Fact]
	public async Task DeletingTheSolutionFileUnloadsTheSolution()
	{
		using var shim = ShimProcess.Start(_pipe);
		await shim.InitializeAsync();
		var first = await LoadAsync(shim, _solution);
		var content = File.ReadAllText(_solution);

		File.Delete(_solution);
		var gone = await shim.CallToolAsync("roslyn_validate_file", new { filePath = _use });
		File.WriteAllText(_solution, content);
		var again = await LoadAsync(shim, _solution);

		Assert.Equal("load_failed", gone["errorCode"]!.GetValue<string>());
		Assert.Contains(_solution, gone["errorMessage"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
		// A new load: the daemon no longer held the first one when the solution file came back.
		Assert.NotEqual(first["loadedAt"]!.GetValue<string>(), again["loadedAt"]!.GetValue<string>());
	}

	[Fact]
	public async Task DaemonsOfTwoVersionsCoexistAndTheOlderExitsByItsTimers()
	{
		var olderPipe = Daemons.NewPipeName();
		_pipes.Add(olderPipe);
		// Seconds instead of hours, but long enough for the load to start before an empty daemon idles out.
		var older = StartDaemon(olderPipe, "--unload-after", "00:00:03", "--exit-after", "00:00:05");
		var newer = StartDaemon(_pipe);
		using var olderShim = ShimProcess.Start(olderPipe);
		using var newerShim = ShimProcess.Start(_pipe);
		await Task.WhenAll(olderShim.InitializeAsync(), newerShim.InitializeAsync());

		var loads = await Task.WhenAll(LoadAsync(olderShim, _solution), LoadAsync(newerShim, _solution));
		// The older daemon gets no further calls; the newer one keeps serving.
		var answers = new List<JsonNode>();
		var deadline = DateTime.UtcNow.AddSeconds(30);
		while (!older.HasExited && DateTime.UtcNow < deadline && await Delay())
			answers.Add(await newerShim.CallToolAsync("roslyn_search_symbols", new { query = "Api" }));

		Assert.Equal([older.Id, newer.Id], loads.Select(load => load["daemon"]!["processId"]!.GetValue<int>()));
		Assert.True(older.HasExited, "The older daemon outlived its timers.");
		Assert.Equal(0, older.ExitCode);
		Assert.NotEmpty(answers);
		Assert.All(answers, answer => Assert.True(answer["requestSucceeded"]!.GetValue<bool>(), answer.ToJsonString()));
		Assert.False(newer.HasExited);
		Assert.Equal(newer.Id, Daemons.ServerProcessId(_pipe));
	}

	/// <summary>Starts a daemon on a pipe and waits until it serves it, so that no shim starts one of its own there.</summary>
	/// <param name="pipe">The pipe.</param>
	/// <param name="options">Options after the pipe.</param>
	private Process StartDaemon(string pipe, params string[] options)
	{
		var start = new ProcessStartInfo(Cli.ExecutablePath) { UseShellExecute = false, CreateNoWindow = true };
		foreach (var argument in new[] { "daemon", "--pipe", pipe }.Concat(options)) start.ArgumentList.Add(argument);
		var daemon = Process.Start(start)!;
		_daemons.Add(daemon);
		var deadline = DateTime.UtcNow.AddSeconds(30);
		while (Daemons.ServerProcessId(pipe) != daemon.Id)
		{
			Assert.True(DateTime.UtcNow < deadline, $"The daemon on {pipe} did not answer.");
			Assert.False(daemon.HasExited, $"The daemon on {pipe} exited with {(daemon.HasExited ? daemon.ExitCode : 0)}.");
		}

		return daemon;
	}

	/// <summary>Loads a solution, calling again while the daemon answers that it is still loading.</summary>
	/// <param name="shim">The shim to call.</param>
	/// <param name="solution">The solution's full path.</param>
	private static async Task<JsonNode> LoadAsync(ShimProcess shim, string solution)
	{
		while (true)
		{
			var load = await shim.CallToolAsync("roslyn_load", new { solution });
			Assert.True(load["requestSucceeded"]!.GetValue<bool>(), load.ToJsonString());
			if (load["status"]!.GetValue<string>() != "loading")
				return load;
		}
	}

	private static async Task<bool> Delay()
	{
		await Task.Delay(TimeSpan.FromMilliseconds(500));
		return true;
	}
}
