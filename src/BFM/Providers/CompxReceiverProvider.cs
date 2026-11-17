using Bfm.Core;
using Bfm.Interop;

namespace Bfm.Providers;

/// <summary>
/// Generic 2.4 GHz receivers built on the Compx chipset (ATK / VXE / Pulsar style, vendor 0x3554 and others).
/// Command 0x04 on the 0xFF02 collection returns level (byte 6) and cable flag (byte 7) in a
/// checksummed frame. Only devices that answer with a valid checksum are shown.
/// </summary>
internal sealed class CompxReceiverProvider : IBatteryProvider
{
    static readonly ushort[] Vendors = { 0x3554, 0x373B, 0x3710 };

    public string Name => "2.4 GHz receiver";
    public bool IsDongle => true;

    readonly Dictionary<string, string> _names = new();
    readonly HashSet<string> _unsupported = new();

    public IReadOnlyList<DeviceStatus> Poll()
    {
        var list = new List<DeviceStatus>();
        foreach (var vid in Vendors)
        {
            foreach (var ifc in Hid.Enumerate(vid).Where(i => i.UsagePage == 0xFF02 && i.OutputLength >= 17 && i.InputLength >= 17))
            {
                var id = $"compx:{ifc.Vid:x4}{ifc.Pid:x4}";
                if (list.Any(d => d.Id == id) || _unsupported.Contains(id)) continue;
                if (!_names.TryGetValue(id, out var name))
                    _names[id] = name = Hid.GetProductString(ifc.Path) ?? "2.4G Wireless";

                using var ch = HidChannel.Open(ifc);
                if (ch is null)
                {
                    Log.Debug($"{id}: open failed");
                    continue;
                }

                var frame = new byte[17];
                frame[0] = 0x08;
                frame[1] = 0x04;
                frame[16] = Checksum(frame);
                if (!ch.Write(frame))
                {
                    // Windows refuses report ids the collection doesn't declare: a different protocol.
                    _unsupported.Add(id);
                    Log.Debug($"{id}: write refused (out={ifc.OutputLength})");
                    continue;
                }

                var r = ch.ReadUntil(x => x.Length >= 17 && x[0] == 0x08 && x[1] == 0x04, 600);
                Log.Debug($"{id}: reply {(r is null ? "none" : Convert.ToHexString(r))}");
                if (r is null || Checksum(r) != r[16] || r[6] > 100) continue;

                list.Add(new DeviceStatus
                {
                    Id = id,
                    Name = name,
                    Kind = DeviceKind.Mouse,
                    Transport = Transport.Dongle,
                    Battery = r[6],
                    Charging = r[7] == 1,
                    Connected = true,
                    Experimental = true,
                });
            }
        }
        return list;
    }

    static byte Checksum(byte[] f)
    {
        int sum = 0;
        for (int i = 0; i < 16; i++) sum += f[i];
        return (byte)(0x55 - sum);
    }
}
