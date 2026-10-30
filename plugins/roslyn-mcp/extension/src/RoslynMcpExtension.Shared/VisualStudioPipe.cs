using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RoslynMcpExtension.Shared;

/// <summary>
/// The pipe a Visual Studio serves the analysis contract of its open solution on, for the daemon to forward that
/// solution's calls to. The extension and the daemon both take its name from here; see docs/adr/0007.
/// </summary>
public static class VisualStudioPipe
{
	/// <summary>
	/// The pipe of one solution file under this plugin version: the same for the path in any letter case and with either
	/// slash. A daemon of another version finds no pipe under its own name and loads the solution itself.
	/// </summary>
	/// <param name="solutionPath">The path of the .sln or .slnx file.</param>
	public static string Name(string solutionPath)
	{
		// SHA-256 rather than string.GetHashCode, which .NET randomizes per process.
		var normalized = Path.GetFullPath(solutionPath).Replace('/', '\\').ToUpperInvariant();
		using var sha = SHA256.Create();
		var hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(normalized))).Replace("-", string.Empty);
		return $"roslyn-mcp-vs-{ExtensionVersion.Value}-{hash}";
	}
}
