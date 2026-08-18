using System.Threading.Channels;
using PlanForge.Diagnostics;

namespace PlanForge.Vendors;

/// <summary>
/// The one way a session raises a <see cref="VendorEvent"/>: into the channel for whichever host
/// eventually reads progress, and into the run's log at the same moment.
/// </summary>
/// <remarks>
/// The channel alone was never a record. It is unbounded and, in production, unread — see
/// <see cref="IVendorSession.Events"/> — so every lifecycle event a failing run produced was
/// collected by the garbage collector along with the session. Routing both from one call is what
/// keeps the log honest as vendors gain events.
/// </remarks>
internal static class VendorEvents
{
    public static ValueTask EmitAsync(this ChannelWriter<VendorEvent> writer,
                                      string vendor,
                                      VendorEvent raised,
                                      CancellationToken ct)
    {
        Record(vendor, raised);
        return writer.WriteAsync(raised, ct);
    }

    public static void Emit(this ChannelWriter<VendorEvent> writer, string vendor, VendorEvent raised)
    {
        Record(vendor, raised);
        writer.TryWrite(raised);
    }

    private static void Record(string vendor, VendorEvent raised) =>
        RunLog.Current?.Write(raised.Kind is VendorEventKind.Failed ? "error" : "info",
            vendor, $"vendor.{raised.Kind.ToString().ToLowerInvariant()}", ("text", raised.Text));
}
