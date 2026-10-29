using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace RoslynMcpExtension.Cli.Tests;

/// <summary>
/// roslyn_load from a working directory and the switch to another solution's file, through the shim and a real daemon
/// loading the restored fixtures: Three.slnx and Pinned.slnx, each alone in its folder.
/// </summary>
/// <param name="fixtures">The restored solutions the tests load.</param>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class DiscoveryShimTests(FixtureSolutions fixtures) : IDisposable
{
	private readonly string _pipe = Daemons.NewPipeName();

	public void Dispose() => Daemons.Kill(_pipe);

	[Fact]
	public async Task ADirectoryFindsItsSolutionAndAFileOfAnotherSwitchesTheSession()
	{
		using var shim = ShimProcess.Start(_pipe);
		await shim.InitializeAsync();

		var load = await shim.CallToolAsync("roslyn_load", new { directory = Path.GetDirectoryName(fixtures.ApiFile) });
		var pinned = await shim.CallToolAsync("roslyn_get_document_symbols", new { filePath = fixtures.PinnedFile });
		var again = await shim.CallToolAsync("roslyn_validate_file", new { filePath = fixtures.PinnedFile });

		Assert.Equal("loaded", load["status"]!.GetValue<string>());
		Assert.Equal("upward", load["foundBy"]!.GetValue<string>());
		Assert.Equal(fixtures.Solution, load["solution"]!.GetValue<string>(), ignoreCase: true);
		Assert.True(pinned["requestSucceeded"]!.GetValue<bool>(), pinned.ToJsonString());
		Assert.Equal(fixtures.Solution, pinned["solutionSwitch"]!["from"]!.GetValue<string>(), ignoreCase: true);
		Assert.Equal(fixtures.PinnedSolution, pinned["solutionSwitch"]!["to"]!.GetValue<string>(), ignoreCase: true);
		Assert.True(again["success"]!.GetValue<bool>(), again.ToJsonString());
		Assert.Null(again["solutionSwitch"]);
	}

	[Fact]
	public async Task AnExplicitSolutionWinsOverTheDirectory()
	{
		using var shim = ShimProcess.Start(_pipe);
		await shim.InitializeAsync();

		var load = await shim.CallToolAsync("roslyn_load", new { solution = fixtures.PinnedSolution, directory = Path.GetDirectoryName(fixtures.Solution) });

		Assert.Equal("loaded", load["status"]!.GetValue<string>());
		Assert.Equal("explicit", load["foundBy"]!.GetValue<string>());
		Assert.Equal(fixtures.PinnedSolution, load["solution"]!.GetValue<string>(), ignoreCase: true);
	}
}
