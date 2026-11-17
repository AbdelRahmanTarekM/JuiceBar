using System.Text.RegularExpressions;
using Bfm.Core;
using Bfm.Interop;

namespace Bfm.Providers;

/// <summary>
/// Battery for connected AirPods / Beats when Windows itself shows none, from their BLE broadcasts.
/// Scans at most once every two minutes, for a few seconds, and only while such a device is connected.
/// </summary>
internal sealed partial class AirPodsProvider : IBatteryProvider
{
    static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);
    static readonly TimeSpan ScanTime = TimeSpan.FromSeconds(6);
    DateTime _lastScan = DateTime.MinValue;

    public string Name => "AirPods (BLE broadcast)";
    public bool IsDongle => false;

    // Apple service nodes: BTHENUM\{uuid}_VID&0001004C_PID&200E\...&001122334455_C00000000
    [GeneratedRegex(@"_VID&0001004C_PID&([0-9A-F]{4})\\.*&([0-9A-F]{12})_C[0-9A-F]{8}$", RegexOptions.IgnoreCase)]
    private static partial Regex AppleServiceRegex();

    public IReadOnlyList<DeviceStatus> Poll()
    {
        if (DateTime.UtcNow - _lastScan < Interval) return Array.Empty<DeviceStatus>();

        var paired = BluetoothApi.GetPairedDevices();
        var withBattery = SetupApi.GetNodes("BTHENUM", id => id.Contains(@"\DEV_", StringComparison.OrdinalIgnoreCase))
            .Where(n => n.Battery is not null)
            .Select(n => n.InstanceId.Split('\\')[1][4..].ToUpperInvariant())
            .ToHashSet();

        var targets = new Dictionary<ushort, ClassicDevice>();
        foreach (var node in SetupApi.GetNodes("BTHENUM", id => AppleServiceRegex().IsMatch(id)))
        {
            var m = AppleServiceRegex().Match(node.InstanceId);
            var model = Convert.ToUInt16(m.Groups[1].Value, 16);
            var addr = m.Groups[2].Value.ToUpperInvariant();
            if (paired.TryGetValue(addr, out var dev) && dev.Connected && ((dev.ClassOfDevice >> 8) & 0x1F) == 4
                && !withBattery.Contains(addr))
                targets[model] = dev;
        }
        if (targets.Count == 0) return Array.Empty<DeviceStatus>();

        _lastScan = DateTime.UtcNow;
        var readings = AppleProximityScanner.Scan(targets.Keys.ToHashSet(), ScanTime);
        Log.Debug($"airpods scan: {readings.Count}/{targets.Count} found");

        var list = new List<DeviceStatus>();
        foreach (var (model, r) in readings)
        {
            var dev = targets[model];
            var pods = new[] { r.Left, r.Right }.Where(p => p is not null).Select(p => p!.Value).ToList();
            var parts = new List<string>();
            if (r.Left is int l) parts.Add($"L {l}%{(r.LeftCharging ? " ⚡" : "")}");
            if (r.Right is int rt) parts.Add($"R {rt}%{(r.RightCharging ? " ⚡" : "")}");
            if (r.Case is int c) parts.Add($"Case {c}%{(r.CaseCharging ? " ⚡" : "")}");

            list.Add(new DeviceStatus
            {
                Id = "bt:" + dev.Address,
                Name = dev.Name,
                Kind = DeviceKind.Earbuds,
                Transport = Transport.Bluetooth,
                Battery = pods.Count > 0 ? pods.Min() : r.Case,
                Charging = pods.Count > 0 && (r.Left is null || r.LeftCharging) && (r.Right is null || r.RightCharging),
                Connected = true,
                BluetoothAddress = dev.Address,
                Detail = parts.Count > 0 ? string.Join(" · ", parts) : null,
                Experimental = true,
            });
        }
        return list;
    }
}
