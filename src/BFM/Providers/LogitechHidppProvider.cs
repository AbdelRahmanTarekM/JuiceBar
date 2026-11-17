using Bfm.Core;
using Bfm.Interop;

namespace Bfm.Providers;

/// <summary>
/// Logitech Unifying / Bolt / Lightspeed receivers via HID++ 2.0 (as used by Solaar and Options+).
/// Only read-only functions are called.
/// </summary>
internal sealed class LogitechHidppProvider : IBatteryProvider, IEventSource
{
    readonly Dictionary<string, HidEventWatcher> _watchers = new();

    /// <summary>Receivers send a short 0x41 report when a paired device connects or drops its link
    /// (when notifications are enabled, which Logi Options+ does).</summary>
    public void EnsureWatching(Action onEvent)
    {
        lock (_watchers)
        {
            foreach (var ifc in Hid.Enumerate(VendorLogitech).Where(i => ReceiverPids.Contains(i.Pid) && i.UsagePage == 0xFF00 && i.Usage == 0x01))
            {
                if (_watchers.TryGetValue(ifc.Path, out var w) && w.IsAlive) continue;
                w?.Dispose();
                var started = HidEventWatcher.Start(ifc, r => r.Length > 3 && r[0] == ShortReport && r[2] == 0x41, onEvent);
                if (started is null) _watchers.Remove(ifc.Path);
                else _watchers[ifc.Path] = started;
            }
        }
    }

    const ushort VendorLogitech = 0x046D;
    static readonly HashSet<ushort> ReceiverPids = new()
    {
        0xC52B, 0xC532, 0xC534, 0xC539, 0xC53A, 0xC53D, 0xC53F, 0xC541, 0xC545, 0xC547, 0xC548, 0xC54D,
    };

    const byte ShortReport = 0x10, LongReport = 0x11;
    const byte SoftwareId = 0x0B;

    const ushort FeatureDeviceInfo = 0x0003, FeatureName = 0x0005, FeatureUnifiedBattery = 0x1004, FeatureBatteryStatus = 0x1000, FeatureBatteryVoltage = 0x1001;

    readonly Dictionary<string, Dictionary<ushort, byte>> _featureCache = new();
    /// <summary>
    /// Keyed by receiver slot. <c>Id</c> comes from the device's unit ID, so one keyboard paired to the same
    /// receiver on two Easy-Switch channels (two slots) is still one device.
    /// </summary>
    readonly Dictionary<string, (string Name, DeviceKind Kind, string Id)> _identityCache = new();

    public string Name => "Logitech receiver";
    public bool IsDongle => true;

    public IReadOnlyList<DeviceStatus> Poll()
    {
        var list = new List<DeviceStatus>();
        var interfaces = Hid.Enumerate(VendorLogitech).Where(i => ReceiverPids.Contains(i.Pid)).ToList();
        foreach (var group in interfaces.GroupBy(i => (i.Pid, i.Interface)))
        {
            var shortIfc = group.FirstOrDefault(i => i.UsagePage == 0xFF00 && i.Usage == 0x01);
            var longIfc = group.FirstOrDefault(i => i.UsagePage == 0xFF00 && i.Usage == 0x02);
            if (shortIfc is null || longIfc is null) continue;

            using var shortCh = HidChannel.Open(shortIfc);
            using var longCh = HidChannel.Open(longIfc);
            if (shortCh is null || longCh is null)
            {
                Log.Debug($"receiver {shortIfc.Pid:x4}: open failed");
                continue;
            }
            var bus = new HidppBus(shortCh, longCh);
            if (Log.Verbose)
            {
                // HID++ 1.0 register 0x02 on the receiver: number of devices currently connected.
                var reg = bus.ReadRegister(0xFF, 0x02);
                Log.Debug($"receiver {shortIfc.Pid:x4}: connection register {(reg is null ? "no answer" : Convert.ToHexString(reg))}");
            }

            for (byte index = 1; index <= 6; index++)
            {
                var key = $"logi:{shortIfc.Pid:x4}:{index}";
                var status = ReadDevice(bus, key, index);
                if (status is not null) list.Add(status);
            }
        }
        return list.GroupBy(d => d.Id).Select(g => g.OrderByDescending(d => d.Connected).First()).ToList();
    }

    DeviceStatus? ReadDevice(HidppBus bus, string key, byte index)
    {
        var features = _featureCache.TryGetValue(key, out var f) ? f : null;

        byte? Feature(ushort id)
        {
            if (features is not null && features.TryGetValue(id, out var idx)) return idx == 0 ? null : idx;
            var r = bus.Request(index, 0x00, 0, (byte)(id >> 8), (byte)id);
            if (r is null) return null;
            features ??= new Dictionary<ushort, byte>();
            features[id] = r[0];
            return r[0] == 0 ? null : r[0];
        }

        // A device that is asleep or switched off answers with an error; treat it as disconnected.
        var ping = bus.Request(index, 0x00, 1, 0, 0, 0xAA);
        Log.Debug($"{key}: ping {(ping is null ? "no answer" : "ok, HID++ " + ping[0] + "." + ping[1])}");
        if (ping is null)
        {
            if (_identityCache.TryGetValue(key, out var known))
                return new DeviceStatus { Id = known.Id, Name = known.Name, Kind = known.Kind, Transport = Transport.Dongle };
            return null;
        }

        if (!_identityCache.TryGetValue(key, out var identity))
        {
            identity = ("Logitech device", DeviceKind.Unknown, key);
            if (Feature(FeatureDeviceInfo) is byte infoIdx && bus.Request(index, infoIdx, 0) is { } info
                && info.Skip(1).Take(4).Any(b => b != 0))
                identity.Id = "logi:" + Convert.ToHexString(info, 1, 4).ToLowerInvariant();
            if (Feature(FeatureName) is byte nameIdx)
            {
                var len = bus.Request(index, nameIdx, 0);
                if (len is not null)
                {
                    var chars = new List<byte>();
                    while (chars.Count < len[0])
                    {
                        var chunk = bus.Request(index, nameIdx, 1, (byte)chars.Count);
                        if (chunk is null) break;
                        chars.AddRange(chunk.Take(Math.Min(16, len[0] - chars.Count)));
                    }
                    if (chars.Count > 0) identity.Name = System.Text.Encoding.ASCII.GetString(chars.ToArray()).Trim('\0', ' ');
                }
                var type = bus.Request(index, nameIdx, 2);
                if (type is not null)
                {
                    identity.Kind = type[0] switch
                    {
                        0 or 2 => DeviceKind.Keyboard,
                        3 or 4 or 5 => DeviceKind.Mouse,
                        8 => DeviceKind.Headset,
                        _ => DeviceKind.Unknown,
                    };
                }
            }
            _identityCache[key] = identity;
        }

        var status = new DeviceStatus
        {
            Id = identity.Id,
            Name = identity.Name,
            Kind = identity.Kind,
            Transport = Transport.Dongle,
            Connected = true,
        };

        if (Feature(FeatureUnifiedBattery) is byte ub && bus.Request(index, ub, 1) is { } u)
        {
            status.Battery = u[0] <= 100 ? u[0] : null;
            status.Charging = u[2] is 1 or 2;
        }
        else if (Feature(FeatureBatteryStatus) is byte bs && bus.Request(index, bs, 0) is { } s)
        {
            status.Battery = s[0] is > 0 and <= 100 ? s[0] : null;
            status.Charging = s[2] is 1 or 2 or 4;
        }
        else if (Feature(FeatureBatteryVoltage) is byte bv && bus.Request(index, bv, 0) is { } v)
        {
            status.Battery = VoltageToPercent((v[0] << 8) | v[1]);
            status.Charging = (v[2] & 0x80) != 0;
        }

        if (features is not null) _featureCache[key] = features;
        return status;
    }

    static int VoltageToPercent(int mv)
    {
        ReadOnlySpan<(int Mv, int Pct)> curve = new[]
        {
            (4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50),
            (3778, 40), (3751, 30), (3717, 20), (3671, 10), (3646, 5), (3579, 2), (3500, 0),
        };
        if (mv >= curve[0].Mv) return 100;
        for (int i = 1; i < curve.Length; i++)
        {
            if (mv >= curve[i].Mv)
            {
                var (hiMv, hiPct) = curve[i - 1];
                var (loMv, loPct) = curve[i];
                return loPct + (mv - loMv) * (hiPct - loPct) / (hiMv - loMv);
            }
        }
        return 0;
    }

    /// <summary>Sends short HID++ requests and waits for the matching reply on either collection.</summary>
    sealed class HidppBus(HidChannel shortCh, HidChannel longCh)
    {
        public byte[]? Request(byte device, byte feature, byte function, params byte[] args) =>
            Raw(device, feature, (byte)((function << 4) | SoftwareId), args);

        /// <summary>HID++ 1.0 register read (sub-id 0x81) on the receiver or a device.</summary>
        public byte[]? ReadRegister(byte device, byte register) => Raw(device, 0x81, register, Array.Empty<byte>());

        byte[]? Raw(byte device, byte feature, byte function, byte[] args)
        {
            var report = new byte[7];
            report[0] = ShortReport;
            report[1] = device;
            report[2] = feature;
            report[3] = function;
            Array.Copy(args, 0, report, 4, Math.Min(3, args.Length));
            if (!shortCh.Write(report)) return null;

            using var cts = new CancellationTokenSource(400);
            var shortRead = shortCh.ReadAsync(cts.Token);
            var longRead = longCh.ReadAsync(cts.Token);
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var done = Task.WhenAny(shortRead, longRead).GetAwaiter().GetResult();
                    var r = done.GetAwaiter().GetResult();
                    if (r is null) return null;

                    if (r.Length >= 4 && (r[0] == ShortReport || r[0] == LongReport) && r[1] == device)
                    {
                        if (r[2] == feature && r[3] == report[3])
                            return r[4..];
                        bool error20 = r[2] == 0xFF && r[3] == feature && r.Length > 4 && r[4] == report[3];
                        bool error10 = r[2] == 0x8F && r[3] == feature;
                        if (error20 || error10)
                        {
                            Log.Debug($"hid++ dev {device} feat {feature:x2} fn {function}: error {(r.Length > 5 ? r[5] : 0):x2} ({(error10 ? "1.0" : "2.0")})");
                            return null;
                        }
                    }

                    if (done == shortRead) shortRead = shortCh.ReadAsync(cts.Token);
                    else longRead = longCh.ReadAsync(cts.Token);
                }
                return null;
            }
            finally
            {
                cts.Cancel();
                try { Task.WaitAll(new Task[] { shortRead, longRead }, 500); } catch { }
            }
        }
    }
}
