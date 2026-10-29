using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace RoslynMcpExtension.Cli.Tests;

/// <summary>
/// What roslyn_load reports of the restore the daemon runs before it loads, through the shim and a real daemon. Every
/// test has a pipe of its own and kills the daemon on it afterwards.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RestoreLoadTests : IDisposable
{
	private readonly string _pipe = Daemons.NewPipeName();
	private readonly UnrestoredSolutions _solutions = new();

	public void Dispose()
	{
		Daemons.Kill(_pipe);
		_solutions.Dispose();
	}

	[Fact]
	public async Task AnUnrestoredSolutionIsRestoredBeforeItLoadsAndCompiles()
	{
		var app = _solutions.Write(@"App\App.csproj", UnrestoredSolutions.SdkProject("""<PackageReference Include="Newtonsoft.Json" Version="13.0.3" />"""));
		var use = _solutions.Write(@"App\Use.cs", """
			namespace App
			{
			    public static class Use
			    {
			        public static Newtonsoft.Json.Formatting Format() => Newtonsoft.Json.Formatting.Indented;
			    }
			}
			""");
		var solution = _solutions.Solution("App.slnx", @"App\App.csproj");
		using var shim = ShimProcess.Start(_pipe);
		await shim.InitializeAsync();

		var load = await LoadAsync(shim, solution);
		var validation = await shim.CallToolAsync("roslyn_validate_file", new { filePath = use });

		Assert.True(validation["success"]!.GetValue<bool>(), validation.ToJsonString());
		Assert.Equal(app, Assert.Single(Projects(load["restore"]!["restored"]!)), ignoreCase: true);
		Assert.Empty(load["restore"]!["failed"]!.AsArray());
		Assert.False(load["partial"]!.GetValue<bool>());
	}

	[Fact]
	public async Task AFailedRestoreIsReportedAndTheLoadGoesOnWithItsAnswersFlaggedPartial()
	{
		// The one feed does not exist, and no package folder holds a package of this name.
		_solutions.Write("nuget.config", $"""
			<configuration>
			  <packageSources>
			    <clear />
			    <add key="unreachable" value="{_solutions.PathOf("no-such-feed")}" />
			  </packageSources>
			</configuration>
			""");
		var broken = _solutions.Write(@"Broken\Broken.csproj", UnrestoredSolutions.SdkProject("""<PackageReference Include="RoslynMcp.Tests.NoSuchPackage" Version="1.0.0" />"""));
		var fine = _solutions.Write(@"Fine\Fine.csproj", UnrestoredSolutions.SdkProject());
		var fineFile = _solutions.Write(@"Fine\Fine.cs", "namespace Fine { public static class Answer { public static int Value => 42; } }");
		var solution = _solutions.Solution("Feed.slnx", @"Broken\Broken.csproj", @"Fine\Fine.csproj");
		using var shim = ShimProcess.Start(_pipe);
		await shim.InitializeAsync();

		var load = await LoadAsync(shim, solution);
		var validation = await shim.CallToolAsync("roslyn_validate_file", new { filePath = fineFile });

		Assert.Equal("loaded", load["status"]!.GetValue<string>());
		Assert.True(load["partial"]!.GetValue<bool>());
		var failed = Assert.Single(load["restore"]!["failed"]!.AsArray())!;
		Assert.Equal(broken, failed["project"]!.GetValue<string>(), ignoreCase: true);
		Assert.Contains("RoslynMcp.Tests.NoSuchPackage", failed["reason"]!.GetValue<string>());
		Assert.Contains("NU1301", load["restore"]!["errors"]!.GetValue<string>());
		Assert.Equal(fine, Assert.Single(Projects(load["restore"]!["restored"]!)), ignoreCase: true);
		Assert.True(validation["success"]!.GetValue<bool>(), validation.ToJsonString());
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

	private static string[] Projects(JsonNode list) => [.. list.AsArray().Select(project => project!.GetValue<string>())];
}
