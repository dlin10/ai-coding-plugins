using System.Globalization;
using System.IO;
using System.Security.Principal;
using CodexSessionPulse.Infrastructure.Settings;

namespace CodexSessionPulse.App.Ipc;

/// <summary>
/// The names that tie the hooks to the one overlay of a user (spec §4.4 item 2): the single-instance mutex
/// <c>Local\CodexSessionPulse.&lt;SID&gt;.&lt;major&gt;</c> and the pipe <c>CodexSessionPulse.&lt;SID&gt;.&lt;major&gt;</c>. The
/// protocol major version keeps two installed releases with incompatible messages off each other's channel.
/// </summary>
/// <param name="MutexName">The mutex the overlay holds while it runs.</param>
/// <param name="PipeName">The pipe the overlay listens on.</param>
internal sealed record PulseChannel(string MutexName, string PipeName)
{
    /// <summary>The protocol major version; bump it with any incompatible change to <see cref="HookMessage"/>.</summary>
    public const int ProtocolMajor = 1;

    /// <summary>
    /// Gets the channel of the current user. When <see cref="PulsePaths.HomeVariable"/> moves the root, a hash of the root
    /// is appended, so a test or development overlay never takes the user's real channel or mutex.
    /// </summary>
    /// <param name="paths">The overlay's files.</param>
    /// <returns>The channel.</returns>
    public static PulseChannel For(PulsePaths paths)
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        string name = string.Create(CultureInfo.InvariantCulture, $"CodexSessionPulse.{sid}.{ProtocolMajor}");
        string defaultRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex-session-pulse");

        if (!string.Equals(Path.TrimEndingDirectorySeparator(paths.Root), defaultRoot, StringComparison.OrdinalIgnoreCase))
        {
            name += "." + Fnv1a(paths.Root.ToUpperInvariant()).ToString("x16", CultureInfo.InvariantCulture);
        }

        return new PulseChannel(@"Local\" + name, name);
    }

    /// <summary>Checks whether an overlay holds the mutex, without creating it.</summary>
    /// <returns>Whether an overlay is running for this channel.</returns>
    public bool IsOverlayRunning()
    {
        try
        {
            if (!Mutex.TryOpenExisting(MutexName, out Mutex? mutex))
            {
                return false;
            }

            mutex.Dispose();

            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // It exists; only its security keeps this process from opening it.
            return true;
        }
    }

    /// <summary>A stable 64-bit FNV-1a hash; no cryptography assembly is loaded on the hook path for it.</summary>
    /// <param name="text">The text to hash.</param>
    /// <returns>The hash.</returns>
    private static ulong Fnv1a(string text)
    {
        ulong hash = 14695981039346656037;

        foreach (char c in text)
        {
            hash = (hash ^ c) * 1099511628211;
        }

        return hash;
    }
}
