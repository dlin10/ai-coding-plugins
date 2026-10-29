using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace RoslynMcpExtension.Cli.Tests;

[CollectionDefinition(Name)]
public sealed class IntegrationCollection : ICollectionFixture<FixtureSolutions>
{
	public const string Name = "Integration";
}

/// <summary>
/// Small solutions on disk, restored once before any test loads them: the daemon does not restore in this slice.
/// They live under the temporary folder, away from this repository's Directory.*.props.
/// </summary>
public sealed class FixtureSolutions : IDisposable
{
	/// <summary>SymbolIdTests' Lib, unchanged, so that the line numbers its tests expect hold here too.</summary>
	private const string Api = """
		namespace Ns
		{
		    public static class Api
		    {
		        public static int Get(int x) => x;
		        public static int Get(string x) => 0;
		        public static int Multi()
		        {
		            return 1;
		        }
		    }
		    public class Widget { public Widget(int size) { } }
		}
		""";

	private const string Shapes = "namespace Ns { public interface IShape { } public sealed class Square : IShape { } }";

	/// <summary>SymbolIdTests' App, unchanged.</summary>
	private const string Use = """
		namespace Ns
		{
		    class Use
		    {
		        int U() => Api.Get(1);
		        int V() => Api.Get("a");
		        System.Func<int> W() => () => Api.Get(2);
		        int L() { var local = 1; return local; }
		        Widget N() => new Widget(3);
		    }
		}
		""";

	/// <summary>DiagnosticsServiceTests' Other: broken on its own.</summary>
	private const string Broken = "class Broken { int X => Missing; }";

	private readonly string _root = Path.Combine(Path.GetTempPath(), "roslyn-mcp-cli-tests", Guid.NewGuid().ToString("N"));

	public FixtureSolutions()
	{
		Solution = Path.Combine(_root, "three", "Three.slnx");
		ApiFile = Path.Combine(_root, "three", "Lib", "Api.cs");
		ShapesFile = Path.Combine(_root, "three", "Lib", "Shapes.cs");
		UseFile = Path.Combine(_root, "three", "App", "Use.cs");
		BrokenFile = Path.Combine(_root, "three", "Other", "Broken.cs");
		Write(Solution, """
			<Solution>
			  <Project Path="Lib/Lib.csproj" />
			  <Project Path="App/App.csproj" />
			  <Project Path="Other/Other.csproj" />
			</Solution>
			""");
		Write(Path.Combine(_root, "three", "Lib", "Lib.csproj"), Project("net10.0"));
		Write(Path.Combine(_root, "three", "App", "App.csproj"), Project("net10.0", """<ProjectReference Include="..\Lib\Lib.csproj" />"""));
		Write(Path.Combine(_root, "three", "Other", "Other.csproj"), Project("net10.0"));
		Write(ApiFile, Api);
		Write(ShapesFile, Shapes);
		Write(UseFile, Use);
		Write(BrokenFile, Broken);
		Restore(Solution);

		OlderSdk = OlderInstalledSdk();
		PinnedSolution = Path.Combine(_root, "pinned", "Pinned.slnx");
		PinnedFile = Path.Combine(_root, "pinned", "Pinned", "Pinned.cs");
		Write(Path.Combine(_root, "pinned", "global.json"), $$"""{ "sdk": { "version": "{{OlderSdk}}", "rollForward": "disable" } }""");
		Write(PinnedSolution, """<Solution><Project Path="Pinned/Pinned.csproj" /></Solution>""");
		// The SDK that evaluated the project names itself in a preprocessor symbol every result's compilation reports.
		Write(Path.Combine(_root, "pinned", "Pinned", "Pinned.csproj"),
		      Project($"net{new Version(OlderSdk).Major}.0",
		              properties: "<DefineConstants>$(DefineConstants);SDK_$(NETCoreSdkVersion.Replace('.', '_'))</DefineConstants>"));
		Write(PinnedFile, "namespace Pinned { public static class Answer { public static int Value => 42; } }");
		Restore(PinnedSolution);
	}

	public string Solution { get; }
	public string ApiFile { get; }
	public string ShapesFile { get; }
	public string UseFile { get; }
	public string BrokenFile { get; }

	/// <summary>An installed SDK of a lower major version than the newest one, which the pinned solution's global.json names.</summary>
	public string OlderSdk { get; }
	public string PinnedSolution { get; }
	public string PinnedFile { get; }

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
	}

	private static string Project(string targetFramework, string? item = null, string? properties = null) => $"""
		<Project Sdk="Microsoft.NET.Sdk">
		  <PropertyGroup>
		    <TargetFramework>{targetFramework}</TargetFramework>
		    {properties}
		  </PropertyGroup>
		  <ItemGroup>{item}</ItemGroup>
		</Project>
		""";

	private static void Write(string path, string content)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content);
	}

	/// <summary>Restores from the solution's folder, so that a global.json there picks the SDK.</summary>
	/// <param name="solution">The solution to restore.</param>
	private static void Restore(string solution)
	{
		// Without these two switches the restore's MSBuild node can stall for minutes after the restore is done.
		var output = Run(Path.GetDirectoryName(solution)!, "restore", solution, "--disable-build-servers", "-nodeReuse:false", "-p:NuGetAudit=false");
		if (output.ExitCode != 0)
			throw new InvalidOperationException($"dotnet restore {solution} failed:\n{output.Text}");
	}

	private static string OlderInstalledSdk()
	{
		var sdks = Regex.Matches(Run(Path.GetTempPath(), "--list-sdks").Text, @"^(\d+)\.(\d+)\.(\d+)\s", RegexOptions.Multiline)
		                .Select(match => new Version(match.Value.Trim()))
		                .OrderByDescending(version => version)
		                .ToList();
		return sdks.FirstOrDefault(version => version.Major < sdks[0].Major)?.ToString()
		       ?? throw new InvalidOperationException("The global.json test needs an installed .NET SDK of a lower major version than the newest one.");
	}

	private static (int ExitCode, string Text) Run(string workingDirectory, params string[] arguments)
	{
		var start = new ProcessStartInfo("dotnet")
		{
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};
		foreach (var argument in arguments) start.ArgumentList.Add(argument);
		// dotnet test runs this process under its own MSBuild and leaves that SDK's paths in the environment, where
		// they would override the SDK a global.json picks.
		foreach (var name in start.Environment.Keys.Where(name => name.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToList())
			start.Environment.Remove(name);
		using var process = Process.Start(start)!;
		var stderr = process.StandardError.ReadToEndAsync();
		var stdout = process.StandardOutput.ReadToEnd();
		process.WaitForExit();
		return (process.ExitCode, stdout + stderr.Result);
	}
}
