using Bfm.Core;
using Bfm.Interop;

namespace Bfm.Providers;

/// <summary>
/// Sony DualSense / DualSense Edge over Bluetooth or USB. The battery is in the status byte of the
/// controller's own input report (layout from the Linux hid-playstation driver): low nibble 0-10 is the
/// level in tens, high nibble is 0 discharging, 1 charging, 2 full.
/// </summary>
internal sealed class DualSenseProvider : IBatteryProvider
{
    const ushort Sony = 0x054C;
    static readonly ushort[] ProductIds = { 0x0CE6, 0x0DF2 };

    const byte UsbReport = 0x01, BluetoothFullReport = 0x31, CalibrationFeature = 0x05;
    const int UsbStatusOffset = 53, BluetoothStatusOffset = 54;

    public string Name => "DualSense";
    public bool IsDongle => false;

    public IReadOnlyList<DeviceStatus> Poll()
    {
        var list = new List<DeviceStatus>();
        foreach (var pid in ProductIds)
        {
            foreach (var ifc in Hid.Enumerate(Sony, pid, includeBluetooth: true).Where(i => i.UsagePage == 0x01 && i.Usage == 0x05))
            {
                var status = ReadStatus(ifc);
                if (status is not byte s)
                {
                    Log.Debug($"dualsense {(ifc.IsBluetooth ? "bt" : "usb")}: no status report");
                    continue;
                }

                int level = s & 0x0F, state = s >> 4;
                var addr = ifc.IsBluetooth ? Hid.GetBluetoothAddress(ifc.Path) : null;
                list.Add(new DeviceStatus
                {
                    // Over Bluetooth, reuse the paired device's id so this merges with the Bluetooth entry.
                    Id = addr is null ? "dualsense:usb" : "bt:" + addr,
                    Name = pid == 0x0DF2 ? "DualSense Edge" : "DualSense Wireless Controller",
                    Kind = DeviceKind.Controller,
                    Transport = addr is null ? Transport.Usb : Transport.Bluetooth,
                    Battery = state == 2 ? 100 : Math.Min(level * 10 + 5, 100),
                    Charging = state == 1,
                    Connected = true,
                    BluetoothAddress = addr,
                });
            }
        }
        return list;
    }

    static byte? ReadStatus(HidInterface ifc)
    {
        using var ch = HidChannel.Open(ifc);
        if (ch is null) return null;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            byte? found = null;
            bool simpleMode = false;
            ch.ReadUntil(r =>
            {
                if (!ifc.IsBluetooth && r[0] == UsbReport && r.Length > UsbStatusOffset)
                {
                    found = r[UsbStatusOffset];
                    return true;
                }
                if (ifc.IsBluetooth && r[0] == BluetoothFullReport && r.Length > BluetoothStatusOffset)
                {
                    found = r[BluetoothStatusOffset];
                    return true;
                }
                if (ifc.IsBluetooth && r[0] == UsbReport)
                {
                    simpleMode = true;
                    return true;
                }
                return false;
            }, 500);

            if (found is not null) return found;
            if (!simpleMode || ifc.FeatureLength <= 0) return null;

            // Over Bluetooth the controller starts in a reduced report mode; reading the calibration
            // feature report switches it to full reports (what the Linux driver and Steam do).
            var feature = new byte[ifc.FeatureLength];
            feature[0] = CalibrationFeature;
            ch.GetFeature(feature);
        }
        return null;
    }
}
