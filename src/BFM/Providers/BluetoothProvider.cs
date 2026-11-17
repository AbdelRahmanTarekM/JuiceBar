using System.Text.RegularExpressions;
using Bfm.Core;
using Bfm.Interop;

namespace Bfm.Providers;

/// <summary>
/// Paired Bluetooth devices, using the battery level Windows stores on each device node
/// (DEVPKEY_Bluetooth_Battery). Windows fills it from the BLE Battery Service and from
/// hands-free (HFP) battery reports.
/// </summary>
internal sealed partial class BluetoothProvider : IBatteryProvider
{
    public string Name => "Bluetooth";
    public bool IsDongle => false;

    [GeneratedRegex(@"^(BTHENUM|BTHLE)\\DEV_([0-9A-F]{12})\\", RegexOptions.IgnoreCase)]
    private static partial Regex DeviceIdRegex();

    public IReadOnlyList<DeviceStatus> Poll()
    {
        var classic = BluetoothApi.GetPairedDevices();
        var nodes = SetupApi.GetNodes("BTHENUM", id => DeviceIdRegex().IsMatch(id))
            .Concat(SetupApi.GetNodes("BTHLE", id => DeviceIdRegex().IsMatch(id)));

        var list = new List<DeviceStatus>();
        foreach (var node in nodes)
        {
            var m = DeviceIdRegex().Match(node.InstanceId);
            bool le = m.Groups[1].Value.Equals("BTHLE", StringComparison.OrdinalIgnoreCase);
            var addr = m.Groups[2].Value.ToUpperInvariant();
            classic.TryGetValue(addr, out var info);

            var name = node.FriendlyName ?? info?.Name ?? addr;
            var kind = info is not null ? KindFromClassOfDevice(info.ClassOfDevice, name) : KindFromName(name);
            if (kind == DeviceKind.Unknown) kind = KindFromName(name);

            list.Add(new DeviceStatus
            {
                Id = "bt:" + addr,
                Name = name,
                Kind = kind,
                Transport = le ? Transport.BluetoothLE : Transport.Bluetooth,
                Battery = node.Battery is byte b && b <= 100 ? b : null,
                Connected = info?.Connected ?? node.IsConnected ?? false,
                BluetoothAddress = addr,
            });
        }
        return list;
    }

    static DeviceKind KindFromClassOfDevice(uint cod, string name)
    {
        int major = (int)((cod >> 8) & 0x1F);
        int minor = (int)((cod >> 2) & 0x3F);
        switch (major)
        {
            case 2:
                return DeviceKind.Phone;
            case 4:
                return IsEarbudName(name) ? DeviceKind.Earbuds : DeviceKind.Headset;
            case 5:
                if ((minor & 0x0F) is 1 or 2) return DeviceKind.Controller;
                if ((minor & 0x20) != 0) return DeviceKind.Mouse;
                if ((minor & 0x10) != 0) return DeviceKind.Keyboard;
                break;
        }
        return DeviceKind.Unknown;
    }

    internal static DeviceKind KindFromName(string name)
    {
        var n = name.ToLowerInvariant();
        if (IsEarbudName(name)) return DeviceKind.Earbuds;
        if (n.Contains("mouse") || n.Contains("mx master") || n.Contains("mx anywhere") || n.Contains("trackball"))
            return DeviceKind.Mouse;
        if (n.Contains("key") || n.Contains("kb") || n.Contains("yiser")) return DeviceKind.Keyboard;
        if (n.Contains("headset") || n.Contains("headphone") || n.Contains("cloud")) return DeviceKind.Headset;
        if (n.Contains("controller") || n.Contains("dualsense") || n.Contains("gamepad") || n.Contains("xbox"))
            return DeviceKind.Controller;
        if (n.Contains("phone")) return DeviceKind.Phone;
        return DeviceKind.Unknown;
    }

    static bool IsEarbudName(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("airpods") || n.Contains("buds") || n.Contains("earbud");
    }
}
