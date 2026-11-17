using System.Runtime.InteropServices;

namespace Bfm.Interop;

/// <summary>
/// Connects / disconnects a paired Bluetooth audio device the same way the Settings app does:
/// the one-shot reconnect/disconnect property of the Bluetooth audio kernel-streaming filter.
/// </summary>
internal static class BluetoothAudioControl
{
    static readonly Guid KsCategoryAudio = new("6994AD04-93EF-11D0-A3CC-00A0C9223196");
    static readonly Guid KsPropSetBtAudio = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
    const uint OneShotReconnect = 0, OneShotDisconnect = 1;
    const uint KsPropertyTypeGet = 1;
    const uint IoctlKsProperty = 0x002F0003;

    [StructLayout(LayoutKind.Sequential)]
    struct KSPROPERTY { public Guid Set; public uint Id; public uint Flags; }

    public static bool HasAudioFilter(string address) => FilterPaths(address).Count > 0;

    public static bool Connect(string address) => Send(address, OneShotReconnect);

    public static bool Disconnect(string address) => Send(address, OneShotDisconnect);

    static List<string> FilterPaths(string address) =>
        SetupApi.GetInterfacePaths(KsCategoryAudio)
            .Where(p => p.Contains("bthenum", StringComparison.OrdinalIgnoreCase)
                        && p.Contains(address, StringComparison.OrdinalIgnoreCase))
            .ToList();

    static unsafe bool Send(string address, uint id)
    {
        foreach (var path in FilterPaths(address))
        {
            using var h = Kernel32.CreateFileW(path, 0xC0000000, 3, IntPtr.Zero, 3, 0x40000000, IntPtr.Zero);
            if (h.IsInvalid) continue;

            using var done = new ManualResetEvent(false);
            var prop = new KSPROPERTY { Set = KsPropSetBtAudio, Id = id, Flags = KsPropertyTypeGet };
            var ov = new NativeOverlapped { EventHandle = done.SafeWaitHandle.DangerousGetHandle() };
            bool ok = Kernel32.DeviceIoControl(h, IoctlKsProperty, &prop, sizeof(KSPROPERTY), null, 0, out _, &ov);
            if (!ok && Marshal.GetLastWin32Error() == 997)
                ok = Kernel32.GetOverlappedResult(h, &ov, out _, true);
            if (ok) return true;
            Core.Log.Write($"bt audio ioctl {id} failed on {path}: {Marshal.GetLastWin32Error()}");
        }
        return false;
    }
}
