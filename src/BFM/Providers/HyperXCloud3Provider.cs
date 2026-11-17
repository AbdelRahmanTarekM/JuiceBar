using Bfm.Core;
using Bfm.Interop;

namespace Bfm.Providers;

/// <summary>
/// HyperX Cloud III Wireless over its 2.4 GHz dongle. Protocol from LennardKittner/HyperHeadset:
/// vendor collection 0xFF13, 62-byte reports with report id 0x66.
/// </summary>
internal sealed class HyperXCloud3Provider : IBatteryProvider
{
    const ushort VendorHp = 0x03F0;
    static readonly ushort[] ProductIds = { 0x05B7, 0x0C9D };

    const byte ReportId = 0x66;
    const byte GetBattery = 0x89, BatteryEvent = 0x0D;
    const byte GetCharging = 0x8A, ChargingEvent = 0x0C;

    public string Name => "HyperX Cloud III Wireless";
    public bool IsDongle => true;

    public IReadOnlyList<DeviceStatus> Poll()
    {
        var list = new List<DeviceStatus>();
        foreach (var pid in ProductIds)
        {
            var ifc = Hid.Enumerate(VendorHp, pid)
                .FirstOrDefault(i => i.UsagePage == 0xFF13 && i.OutputLength >= 3 && i.InputLength >= 5);
            if (ifc is null) continue;

            var status = new DeviceStatus
            {
                Id = $"hyperx:{pid:x4}",
                Name = Hid.GetProductString(ifc.Path) ?? "HyperX Cloud III Wireless",
                Kind = DeviceKind.Headset,
                Transport = Transport.Dongle,
            };

            using (var ch = HidChannel.Open(ifc))
            {
                if (ch is not null)
                {
                    var battery = Query(ch, GetBattery, BatteryEvent);
                    if (battery is not null && (battery[2] != 0 || battery[3] != 0) && battery[4] <= 100)
                    {
                        status.Battery = battery[4];
                        status.Connected = true;
                        var charging = Query(ch, GetCharging, ChargingEvent);
                        if (charging is not null)
                            status.Charging = charging[2] == 1;
                    }
                }
            }
            list.Add(status);
        }
        return list;
    }

    static byte[]? Query(HidChannel ch, byte command, byte eventId)
    {
        if (!ch.Write(new byte[] { ReportId, command })) return null;
        return ch.ReadUntil(r => r.Length >= 5 && r[0] == ReportId && (r[1] == command || r[1] == eventId), 1000);
    }
}
