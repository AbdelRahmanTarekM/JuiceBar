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

    /// <summary>Looks up the user's lightbar colour (RRGGBB) for a device id; set by the app.</summary>
    public static Func<string, string?>? LightbarFor { get; set; }

    /// <summary>Colour last sent per device; cleared when the controller goes away so it is re-sent on reconnect.</summary>
    static readonly Dictionary<string, string> Applied = new();
    static byte _btSequence;

    public IReadOnlyList<DeviceStatus> Poll()
    {
        var list = Read();
        lock (Applied)
        {
            foreach (var gone in Applied.Keys.Where(id => list.All(d => d.Id != id)).ToList())
                Applied.Remove(gone);
        }
        foreach (var d in list)
        {
            if (LightbarFor?.Invoke(d.Id) is not string hex) continue;
            lock (Applied)
                if (Applied.TryGetValue(d.Id, out var sent) && sent == hex) continue;
            SetLightbar(d.Id, hex);
        }
        return list;
    }

    /// <summary>Sets the lightbar of the controller with this device id. Games that drive the lightbar override it while running.</summary>
    public static bool SetLightbar(string id, string hex)
    {
        int rgb = Convert.ToInt32(hex, 16);
        foreach (var pid in ProductIds)
        foreach (var ifc in Hid.Enumerate(Sony, pid, includeBluetooth: true).Where(i => i.UsagePage == 0x01 && i.Usage == 0x05))
        {
            var addr = ifc.IsBluetooth ? Hid.GetBluetoothAddress(ifc.Path) : null;
            if ((addr is null ? "dualsense:usb" : "bt:" + addr) != id) continue;
            using var ch = HidChannel.Open(ifc);
            Log.Debug($"dualsense {id}: out={ifc.OutputLength} bt={ifc.IsBluetooth} open={ch is not null}");
            if (ch is null) return false;
            // First release the firmware's own lightbar animation, then set the colour (as hid-playstation does).
            bool ok = ch.Write(OutputReport(ifc.IsBluetooth, c =>
            {
                c[38] = 0x02; // valid_flag2: lightbar setup control
                c[41] = 0x02; // lightbar_setup: light out
            }));
            ok &= ch.Write(OutputReport(ifc.IsBluetooth, c =>
            {
                c[1] = 0x04;  // valid_flag1: lightbar colour
                c[44] = (byte)(rgb >> 16);
                c[45] = (byte)(rgb >> 8);
                c[46] = (byte)rgb;
            }));
            Log.Debug($"dualsense {id}: lightbar {hex} {(ok ? "sent" : "failed")}");
            if (ok) lock (Applied) Applied[id] = hex;
            return ok;
        }
        return false;
    }

    /// <summary>
    /// Builds an output report around the 47-byte "common" block. USB: report 0x02 with the block at byte 1.
    /// Bluetooth: report 0x31, sequence tag, tag 0x10, the block at byte 3, CRC-32 (seeded with 0xA2) in the last 4 bytes.
    /// </summary>
    static byte[] OutputReport(bool bluetooth, Action<Span<byte>> fill)
    {
        if (!bluetooth)
        {
            var usb = new byte[48];
            usb[0] = 0x02;
            fill(usb.AsSpan(1, 47));
            return usb;
        }
        var bt = new byte[78];
        bt[0] = 0x31;
        bt[1] = (byte)((_btSequence++ & 0x0F) << 4);
        bt[2] = 0x10;
        fill(bt.AsSpan(3, 47));
        uint crc = Crc32(0xA2, bt.AsSpan(0, 74));
        BitConverter.TryWriteBytes(bt.AsSpan(74), crc);
        return bt;
    }

    static uint Crc32(byte seed, ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        void Feed(byte b)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        Feed(seed);
        foreach (var b in data) Feed(b);
        return ~crc;
    }

    static List<DeviceStatus> Read()
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
