using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CodexSessionPulse.Core;

namespace CodexSessionPulse.Infrastructure.Desktop;

/// <summary>
/// Reads the one line of Codex Desktop's log the overlay needs: the route sync its main process writes on every page
/// switch, passive ones included,
/// <c>&lt;UTC time&gt; info [electron-message-handler] IAB_LIFECYCLE received browser sidebar owner sync … ownerRoutePath=&lt;route&gt; windowId=&lt;n&gt;</c>.
/// Only <c>ownerRoutePath</c> and the time are taken; the line's other fields (its <c>conversationId</c> is not the
/// thread id) are not. The format is Codex's own and undocumented: measured on Desktop 26.1027 to 26.1103
/// (docs/compatibility.md §6.3).
/// </summary>
public static partial class DesktopLogLine
{
    /// <summary>Gets the marker as UTF-8: a line without it is never decoded, so nothing else of the log becomes text.</summary>
    public static ReadOnlySpan<byte> Marker => "received browser sidebar owner sync"u8;

    /// <summary>Gets whether a line is the route sync.</summary>
    /// <param name="line">One line of the log as UTF-8, without its line break.</param>
    /// <returns>Whether it carries the marker.</returns>
    public static bool IsMarker(ReadOnlySpan<byte> line) => line.IndexOf(Marker) >= 0;

    /// <summary>
    /// Parses a marker line: <c>/</c> is New chat, <c>/local/&lt;thread id&gt;</c> with nothing after the id a local
    /// thread, and any other route (<c>?hostId=…</c> of a remote host, <c>/g/…</c> project chats, <c>/remote/…</c>
    /// cloud tasks) a page without a local rollout.
    /// </summary>
    /// <param name="line">One marker line as UTF-8, without its line break.</param>
    /// <param name="route">The route when the line parses.</param>
    /// <returns>Whether the line has a UTC time first and an <c>ownerRoutePath</c> field.</returns>
    public static bool TryParse(ReadOnlySpan<byte> line, out CodexRoute route)
    {
        route = null!;
        Match match = LinePattern().Match(Encoding.UTF8.GetString(line));

        if (!match.Success
            || !DateTimeOffset.TryParse(match.Groups["at"].Value, CultureInfo.InvariantCulture,
                                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset atUtc))
        {
            return false;
        }

        string path = match.Groups["route"].Value;
        Match local = LocalThreadPattern().Match(path);

        route = path == "/" ? new CodexRoute(CodexRouteKind.NewChat, null, atUtc)
              : local.Success ? new CodexRoute(CodexRouteKind.LocalThread, local.Groups["thread"].Value.ToLowerInvariant(), atUtc)
              : new CodexRoute(CodexRouteKind.Other, null, atUtc);

        return true;
    }

    [GeneratedRegex(@"^(?<at>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?Z)\s.*\sownerRoutePath=(?<route>\S+)", RegexOptions.CultureInvariant)]
    private static partial Regex LinePattern();

    [GeneratedRegex(@"^/local/(?<thread>[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalThreadPattern();
}
