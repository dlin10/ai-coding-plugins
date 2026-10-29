using System.Security.Principal;
using RoslynMcpExtension.Shared;

namespace RoslynMcpExtension.Cli;

/// <summary>The pipe a shim finds its daemon on.</summary>
internal static class DaemonPipe
{
	/// <summary>
	/// One daemon per plugin version and Windows user. The version keeps a shim on the daemon built with it while
	/// an older one idles out; the user's SID keeps two accounts on one machine from contending for one name.
	/// </summary>
	public static string DefaultName => $"roslyn-mcp-{ExtensionVersion.Value}-{WindowsIdentity.GetCurrent().User}";
}
