using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Xunit;

namespace RoslynMcpExtension.Cli.Tests;

/// <summary>
/// The shim and a real daemon, started from the built executable and loading real solutions with MSBuild. Every test
/// has a pipe of its own and kills the daemon on it afterwards.
/// </summary>
/// <param name="fixtures">The restored solutions the tests load.</param>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ShimDaemonTests(FixtureSolutions fixtures) : IDisposable
{
	private const string GetInt = "M:Ns.Api.Get(System.Int32)";

	private readonly string _pipe = Daemons.NewPipeName();

	public void Dispose() => Daemons.Kill(_pipe);

	[Fact]
	public async Task InitializeAloneStartsNoDaemonAndEveryToolIsReadOnly()
	{
		JsonArray tools;
		using (var shim = ShimProcess.Start(_pipe))
		{
			await shim.InitializeAsync();
			tools = (await shim.RequestAsync("tools/list", new { }))["tools"]!.AsArray();
			await Task.Delay(TimeSpan.FromSeconds(2));
			Assert.Null(Daemons.ServerProcessId(_pipe));
		}

		Assert.Equal(12, tools.Count);
		Assert.Contains(tools, tool => tool!["name"]!.GetValue<string>() == "roslyn_load");
		Assert.All(tools, tool =>
		{
			var annotations = tool!["annotations"]!;
			Assert.True(annotations["readOnlyHint"]!.GetValue<bool>(), tool["name"]!.GetValue<string>());
			Assert.False(annotations["destructiveHint"]!.GetValue<bool>(), tool["name"]!.GetValue<string>());
			Assert.False(annotations["openWorldHint"]!.GetValue<bool>(), tool["name"]!.GetValue<string>());
		});
		Assert.Null(Daemons.ServerProcessId(_pipe));
	}

	[Fact]
	public async Task ToolsThroughTheShimAnswerFromTheLoadedSolution()
	{
		using var shim = ShimProcess.Start(_pipe);
		await shim.InitializeAsync();

		var early = await shim.CallToolAsync("roslyn_get_document_symbols", new { filePath = fixtures.ApiFile });
		Assert.Equal("no_solution", early["errorCode"]!.GetValue<string>());

		var load = await shim.CallToolAsync("roslyn_load", new { solution = fixtures.Solution });
		Assert.True(load["requestSucceeded"]!.GetValue<bool>(), load.ToJsonString());
		Assert.Equal("loaded", load["status"]!.GetValue<string>());
		Assert.Equal("explicit", load["foundBy"]!.GetValue<string>());
		Assert.Equal(fixtures.Solution, load["solution"]!.GetValue<string>(), ignoreCase: true);
		Assert.Equal(3, load["projectCount"]!.GetValue<int>());
		Assert.False(load["daemon"]!["exitsWithJob"]!.GetValue<bool>());
		Assert.Equal(Daemons.ServerProcessId(_pipe), load["daemon"]!["processId"]!.GetValue<int>());

		// What SymbolIdTests expects of the same sources.
		var references = await shim.CallToolAsync("roslyn_find_references", new { symbolId = GetInt });
		Assert.Equal("Lib", references["compilation"]!["projectName"]!.GetValue<string>());
		Assert.Equal(["M:Ns.Use.U", "M:Ns.Use.W"],
		             Members(references).Where(m => m["memberType"]!.GetValue<string>() == "reference")
		                                .Select(m => m["containingSymbolId"]!.GetValue<string>())
		                                .OrderBy(id => id, StringComparer.Ordinal));

		var callers = Members(await shim.CallToolAsync("roslyn_find_callers", new { symbolId = GetInt }))
			.Select(m => m["containingSymbolId"]?.GetValue<string>()).ToList();
		Assert.Contains("M:Ns.Use.U", callers);
		Assert.Contains("M:Ns.Use.W", callers);
		Assert.DoesNotContain("M:Ns.Use.V", callers);

		var multi = Assert.Single(Members(await shim.CallToolAsync("roslyn_search_symbols", new { query = "Multi" })));
		Assert.Equal("M:Ns.Api.Multi", multi["symbolId"]!.GetValue<string>());
		Assert.Equal((7, 7, 10), (multi["startLine"]!.GetValue<int>(), multi["enclosingStartLine"]!.GetValue<int>(),
		                          multi["enclosingEndLine"]!.GetValue<int>()));

		var info = await shim.CallToolAsync("roslyn_get_symbol_info", new { symbolId = "M:Ns.Api.Multi" });
		Assert.Equal(multi["fullName"]!.GetValue<string>(), info["symbol"]!["fullName"]!.GetValue<string>());

		// Use.cs line 5 is "        int U() => Api.Get(1);", with Get at column 24.
		var definition = Assert.Single(Members(await shim.CallToolAsync("roslyn_go_to_definition", new { filePath = fixtures.UseFile, line = 5, column = 24 })));
		Assert.Equal(fixtures.ApiFile, definition["filePath"]!.GetValue<string>(), ignoreCase: true);
		Assert.Equal(5, definition["startLine"]!.GetValue<int>());

		var implementations = await shim.CallToolAsync("roslyn_find_implementations", new { symbolId = "T:Ns.IShape" });
		Assert.Contains(Members(implementations), m => m["symbolId"]?.GetValue<string>() == "T:Ns.Square");

		var widget = await shim.CallToolAsync("roslyn_describe_type", new { symbolId = "T:Ns.Widget" });
		Assert.Contains(widget["members"]!.AsArray(), m => m!["symbolId"]?.GetValue<string>() == "M:Ns.Widget.#ctor(System.Int32)");

		var symbols = await shim.CallToolAsync("roslyn_get_document_symbols", new { filePath = fixtures.ApiFile });
		Assert.Contains(Members(symbols), m => m["name"]!.GetValue<string>() == "Multi");

		var dead = await shim.CallToolAsync("roslyn_find_dead_code", new { });
		Assert.True(dead["requestSucceeded"]!.GetValue<bool>(), dead.ToJsonString());
		Assert.Contains(Members(dead), m => m["symbolId"]?.GetValue<string>() == "M:Ns.Use.L");

		// What DiagnosticsServiceTests expects of Other.
		var diagnostics = await shim.CallToolAsync("roslyn_get_diagnostics", new { projectName = "Other" });
		Assert.Equal(["Other"], diagnostics["checkedProjects"]!.AsArray().Select(p => p!.GetValue<string>()));
		Assert.Contains(diagnostics["diagnostics"]!.AsArray(), d => d!["id"]!.GetValue<string>() == "CS0103");

		var validation = await shim.CallToolAsync("roslyn_validate_file", new { filePath = fixtures.BrokenFile });
		Assert.False(validation["success"]!.GetValue<bool>());
		Assert.Contains(validation["errors"]!.AsArray(), e => e!["id"]!.GetValue<string>() == "CS0103");

		var outside = await shim.CallToolAsync("roslyn_validate_file", new { filePath = Path.Combine(Path.GetTempPath(), "Elsewhere", "Api.cs") });
		Assert.Equal("document_not_found", outside["errorCode"]!.GetValue<string>());
		Assert.Contains(fixtures.Solution, outside["errorMessage"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public async Task TwoShimsShareOneDaemonAndOneLoad()
	{
		using var first = ShimProcess.Start(_pipe);
		using var second = ShimProcess.Start(_pipe);
		await Task.WhenAll(first.InitializeAsync(), second.InitializeAsync());

		var loads = await Task.WhenAll(first.CallToolAsync("roslyn_load", new { solution = fixtures.Solution }),
		                               second.CallToolAsync("roslyn_load", new { solution = fixtures.Solution }));

		Assert.All(loads, load => Assert.Equal("loaded", load["status"]!.GetValue<string>()));
		Assert.Single(loads.Select(load => load["daemon"]!["processId"]!.GetValue<int>()).Distinct());
		Assert.Single(loads.Select(load => load["loadedAt"]!.GetValue<string>()).Distinct());
		Assert.Equal(loads[0]["daemon"]!["processId"]!.GetValue<int>(), Daemons.ServerProcessId(_pipe));
	}

	[Fact]
	public async Task UnderAJobThatRefusesBreakawayTheDaemonAnswersAndExitsWithTheJob()
	{
		using var job = new KillOnCloseJob();
		using var shim = ShimProcess.Start(_pipe);
		job.Assign(shim.Process);
		await shim.InitializeAsync();

		var load = await shim.CallToolAsync("roslyn_load", new { solution = fixtures.Solution });
		var symbols = await shim.CallToolAsync("roslyn_get_document_symbols", new { filePath = fixtures.ApiFile });

		Assert.Equal("loaded", load["status"]!.GetValue<string>());
		Assert.True(load["daemon"]!["exitsWithJob"]!.GetValue<bool>());
		Assert.True(symbols["requestSucceeded"]!.GetValue<bool>(), symbols.ToJsonString());
		using var daemon = Process.GetProcessById(load["daemon"]!["processId"]!.GetValue<int>());
		job.Dispose();
		Assert.True(daemon.WaitForExit(10_000), "The daemon outlived the job it was started in.");
	}

	[Fact]
	public async Task UnderAJobThatAllowsBreakawayTheDaemonOutlivesTheJob()
	{
		int processId;
		using (var job = new KillOnCloseJob(allowBreakaway: true))
		{
			using var shim = ShimProcess.Start(_pipe);
			job.Assign(shim.Process);
			await shim.InitializeAsync();

			var load = await shim.CallToolAsync("roslyn_load", new { solution = fixtures.Solution });

			Assert.Equal("loaded", load["status"]!.GetValue<string>());
			Assert.False(load["daemon"]!["exitsWithJob"]!.GetValue<bool>());
			processId = load["daemon"]!["processId"]!.GetValue<int>();
		}

		await Task.Delay(TimeSpan.FromSeconds(2));
		Assert.Equal(processId, Daemons.ServerProcessId(_pipe));
	}

	[Fact]
	public void ThePipeOpensToTheCurrentUserOnly()
	{
		var start = new ProcessStartInfo(Cli.ExecutablePath) { UseShellExecute = false, CreateNoWindow = true };
		foreach (var argument in new[] { "daemon", "--pipe", _pipe }) start.ArgumentList.Add(argument);
		using var daemon = Process.Start(start)!;
		try
		{
			using var client = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut);
			client.Connect(10_000);

			var rules = client.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();

			var rule = Assert.Single(rules);
			Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
			Assert.Equal(WindowsIdentity.GetCurrent().Owner, rule.IdentityReference);
		}
		finally
		{
			daemon.Kill(entireProcessTree: true);
			daemon.WaitForExit(10_000);
		}
	}

	[Fact]
	public async Task ASolutionPinningAnOlderSdkLoadsWithThatSdk()
	{
		using var shim = ShimProcess.Start(_pipe);
		await shim.InitializeAsync();

		var load = await shim.CallToolAsync("roslyn_load", new { solution = fixtures.PinnedSolution });
		var validation = await shim.CallToolAsync("roslyn_validate_file", new { filePath = fixtures.PinnedFile });
		var symbols = await shim.CallToolAsync("roslyn_get_document_symbols", new { filePath = fixtures.PinnedFile });

		Assert.Equal("loaded", load["status"]!.GetValue<string>());
		Assert.True(validation["success"]!.GetValue<bool>(), validation.ToJsonString());
		Assert.Contains("SDK_" + fixtures.OlderSdk.Replace('.', '_'),
		                symbols["compilation"]!["defines"]!.AsArray().Select(define => define!.GetValue<string>()));
	}

	private static JsonNode[] Members(JsonNode result) => [.. result["members"]!.AsArray().Select(member => member!)];
}
