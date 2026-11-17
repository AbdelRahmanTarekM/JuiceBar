using Bfm.Core;
using Bfm.Interop;

namespace Bfm.Providers;

/// <summary>
/// UGREEN 2.4 GHz receivers. The receiver has no battery or link-state channel (UGREEN's own software doesn't
/// talk to it either), so this only reports *that* the mouse is in use on the receiver, judged from recent
/// pointer input coming through it. When the same mouse is paired over Bluetooth it reports under that
/// device's id, so both links are one chip and the Bluetooth battery reading carries over.
/// </summary>
internal sealed class UgreenReceiverProvider : IBatteryProvider
{
    public static readonly (ushort Vid, ushort Pid)[] Receivers = { (0x2B89, 0x0043) };

    /// <summary>How long after the last movement through the receiver the mouse still counts as connected.</summary>
    static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(5);

    static long _lastActivityTicks;

    /// <summary>Finds the Bluetooth pairing of the same mouse (id and name); set by the app.</summary>
    public static Func<(string Id, string Name)?>? FindPartner { get; set; }

    public static bool IsReceiverPath(string path) =>
        Receivers.Any(r => path.Contains($"VID_{r.Vid:X4}&PID_{r.Pid:X4}", StringComparison.OrdinalIgnoreCase));

    /// <summary>Records pointer input from the receiver; returns true when the mouse just became active.</summary>
    public static bool NoteActivity()
    {
        long now = DateTime.UtcNow.Ticks;
        long before = Interlocked.Exchange(ref _lastActivityTicks, now);
        return now - before > ActiveWindow.Ticks;
    }

    public string Name => "UGREEN receiver";
    public bool IsDongle => false;

    public IReadOnlyList<DeviceStatus> Poll()
    {
        if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastActivityTicks) > ActiveWindow.Ticks) return Array.Empty<DeviceStatus>();
        if (!Receivers.Any(r => Hid.Enumerate(r.Vid, r.Pid).Count > 0)) return Array.Empty<DeviceStatus>();

        var partner = FindPartner?.Invoke();
        return new[]
        {
            new DeviceStatus
            {
                Id = partner?.Id ?? "ugreen:2b89:0043",
                Name = partner?.Name ?? "UGREEN Mouse",
                Kind = DeviceKind.Mouse,
                Transport = Transport.Dongle,
                Connected = true,
                BluetoothAddress = partner?.Id is string id && id.StartsWith("bt:", StringComparison.Ordinal) ? id[3..] : null,
            },
        };
    }
}
