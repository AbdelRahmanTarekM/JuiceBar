using Bfm.Interop;

namespace Bfm.Core;

/// <summary>Connect / disconnect actions. Disconnecting never unpairs.</summary>
internal static class DeviceControl
{
    static bool IsClassic(DeviceStatus d) => d.Transport == Transport.Bluetooth && d.BluetoothAddress is not null;

    static bool IsAudio(DeviceStatus d) => d.Kind is DeviceKind.Headset or DeviceKind.Earbuds;

    public static bool CanDisconnect(DeviceStatus d) => IsClassic(d) && d.Connected;

    /// <summary>A Bluetooth controller powers itself off when its link drops, so call it what it does.</summary>
    public static string DisconnectLabel(DeviceStatus d) =>
        d.Kind == DeviceKind.Controller ? "Turn off controller" : "Disconnect (stays paired)";

    /// <summary>Only audio devices can be asked to connect from the PC; others connect from their own side.</summary>
    public static bool CanConnect(DeviceStatus d) =>
        IsClassic(d) && !d.Connected && IsAudio(d) && BluetoothAudioControl.HasAudioFilter(d.BluetoothAddress!);

    public static Task<bool> DisconnectAsync(DeviceStatus d) => Task.Run(() =>
    {
        var addr = d.BluetoothAddress!;
        // Audio devices first get the polite profile-level disconnect Settings uses; the link-level one covers the rest.
        if (IsAudio(d) && BluetoothAudioControl.HasAudioFilter(addr) && BluetoothAudioControl.Disconnect(addr))
            return true;
        return BluetoothApi.Disconnect(addr);
    });

    public static Task<bool> ConnectAsync(DeviceStatus d) => Task.Run(() => BluetoothAudioControl.Connect(d.BluetoothAddress!));
}
