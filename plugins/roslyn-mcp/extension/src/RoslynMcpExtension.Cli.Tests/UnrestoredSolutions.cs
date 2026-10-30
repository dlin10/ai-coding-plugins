using System;
using System.IO;
using System.Linq;

namespace RoslynMcpExtension.Cli.Tests;

/// <summary>
/// Solutions written for one test under the temporary folder, away from this repository's Directory.*.props, and never
/// restored by the test itself: restoring them is what the tests are about.
/// </summary>
internal sealed class UnrestoredSolutions : IDisposable
{
	private readonly string _root = Path.Combine(Path.GetTempPath(), "roslyn-mcp-restore-tests", Guid.NewGuid().ToString("N"));

	/// <summary>The full path of a file under this test's folder.</summary>
	/// <param name="relativePath">The path relative to the test's folder.</param>
	public string PathOf(string relativePath) => Path.Combine(_root, relativePath);

	/// <summary>Writes a file under this test's folder and returns its full path.</summary>
	/// <param name="relativePath">The path relative to the test's folder.</param>
	/// <param name="content">The file's text.</param>
	public string Write(string relativePath, string content)
	{
		var path = PathOf(relativePath);
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		File.WriteAllText(path, content);
		return path;
	}

	/// <summary>Writes a .slnx solution listing projects and returns its full path.</summary>
	/// <param name="relativePath">The solution's path relative to the test's folder.</param>
	/// <param name="projects">The project files, relative to the test's folder, which is where the solution must be.</param>
	public string Solution(string relativePath, params string[] projects)
		=> Write(relativePath, $"<Solution>{string.Concat(projects.Select(project => $"<Project Path=\"{project}\" />"))}</Solution>");

	/// <summary>An SDK project targeting net10.0.</summary>
	/// <param name="items">The content of its one item group.</param>
	public static string SdkProject(string? items = null) => $"""
		<Project Sdk="Microsoft.NET.Sdk">
		  <PropertyGroup>
		    <TargetFramework>net10.0</TargetFramework>
		  </PropertyGroup>
		  <ItemGroup>{items}</ItemGroup>
		</Project>
		""";

	public void Dispose()
	{
		try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
	}
}
