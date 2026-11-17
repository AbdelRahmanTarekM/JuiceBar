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

    /// <summary>Our own link query makes the receiver emit 0x41 reports; the watcher ignores them until this tick.</summary>
    long _ownQueryUntil;

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
                var started = HidEventWatcher.Start(ifc,
                    r => r.Length > 3 && r[0] == ShortReport && r[2] == 0x41 && Environment.TickCount64 > Interlocked.Read(ref _ownQueryUntil),
                    onEvent);
                if (started is null) _watchers.Remove(ifc.Path);
                else _watchers[ifc.Path] = started;
            }
            foreach (var ifc in Hid.Enumerate(VendorLogitech).Where(i => ReceiverPids.Contains(i.Pid) && i.UsagePage == 0xFF00 && i.Usage == 0x02))
            {
                if (_watchers.TryGetValue(ifc.Path, out var w) && w.IsAlive) continue;
                w?.Dispose();
                ushort pid = ifc.Pid;
                var started = HidEventWatcher.Start(ifc, r => OnLongReport(pid, r), onEvent);
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
    readonly Dictionary<string, SlotIdentity> _identityCache = LoadIdentities();
    readonly HashSet<string> _verified = new();

    public LogitechHidppProvider()
    {
        foreach (var (slot, id) in _identityCache)
            if (id.BatteryFeature != 0) _batteryFeatures[slot] = (id.BatteryFeature, id.BatteryIndex);
    }

    internal sealed class SlotIdentity
    {
        public string Name { get; set; } = "";
        public DeviceKind Kind { get; set; }
        public string Id { get; set; } = "";
        /// <summary>Battery feature id (0x1004/0x1000/0x1001) and its index on this device, for decoding pushed events.</summary>
        public ushort BatteryFeature { get; set; }
        public byte BatteryIndex { get; set; }
    }

    /// <summary>Battery readings a device pushed by itself (HID++ event, software id 0), keyed by slot.</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int? Battery, bool? Charging)> _pushed = new();

    /// <summary>Slot → battery feature, readable from the watcher thread.</summary>
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, (ushort Feature, byte Index)> _batteryFeatures = new();

    bool OnLongReport(ushort pid, byte[] r)
    {
        // Notifications carry software id 0 in the low nibble and event 0 in the high nibble.
        if (r.Length < 8 || r[0] != LongReport || r[1] is < 1 or > 6 || r[3] != 0) return false;
        var slotKey = $"logi:{pid:x4}:{r[1]}";
        if (!_batteryFeatures.TryGetValue(slotKey, out var bf) || bf.Index != r[2]) return false;
        var p = r.AsSpan(4);
        (int? level, bool? charging) reading = bf.Feature switch
        {
            FeatureUnifiedBattery => (p[0] <= 100 ? p[0] : null, p[2] is 1 or 2),
            FeatureBatteryStatus => (p[0] is > 0 and <= 100 ? p[0] : null, p[2] is 1 or 2 or 4),
            _ => (VoltageToPercent((p[0] << 8) | p[1]), (p[2] & 0x80) != 0),
        };
        _pushed[slotKey] = reading;
        Log.Debug($"{slotKey}: pushed battery {reading.level} charging={reading.charging}");
        return true;
    }

    static string IdentityFile => Path.Combine(Settings.Folder, "logitech-slots.json");

    static Dictionary<string, SlotIdentity> LoadIdentities()
    {
        try
        {
            if (File.Exists(IdentityFile))
                return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, SlotIdentity>>(File.ReadAllText(IdentityFile)) ?? new();
        }
        catch (Exception ex)
        {
            Log.Write("logitech slots load failed: " + ex.Message);
        }
        return new();
    }

    void SaveIdentities()
    {
        try
        {
            Directory.CreateDirectory(Settings.Folder);
            File.WriteAllText(IdentityFile, System.Text.Json.JsonSerializer.Serialize(_identityCache));
        }
        catch (Exception ex)
        {
            Log.Write("logitech slots save failed: " + ex.Message);
        }
    }

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
            Interlocked.Exchange(ref _ownQueryUntil, Environment.TickCount64 + 5000);
            var links = bus.QueryLinks();
            Interlocked.Exchange(ref _ownQueryUntil, Environment.TickCount64 + 1000);
            Log.Debug($"receiver {shortIfc.Pid:x4}: links {string.Join(" ", links.Select(l => $"{l.Key}={(l.Value ? "up" : "down")}"))}");
            foreach (var slot in links.Keys)
            {
                var slotKey = $"logi:{shortIfc.Pid:x4}:{slot}";
                if (!_identityCache.ContainsKey(slotKey) && ReceiverIdentity(bus, slot, slotKey) is { } fromReceiver)
                {
                    _identityCache[slotKey] = fromReceiver;
                    SaveIdentities();
                }
            }

            for (byte index = 1; index <= 6; index++)
            {
                if (links.Count > 0 && !links.ContainsKey(index)) continue;
                var key = $"logi:{shortIfc.Pid:x4}:{index}";
                var status = ReadDevice(bus, key, index, links.TryGetValue(index, out var up) ? up : null);
                if (status is not null) list.Add(status);
            }
        }
        return list.GroupBy(d => d.Id).Select(g => g.OrderByDescending(d => d.Connected).First()).ToList();
    }

    /// <summary>
    /// Bolt receivers keep each slot's pairing record (register 0xB5): 0x50+n = kind, wireless PID, unit ID;
    /// 0x60+n page 1 = name. Read from the receiver itself, so it works while the device sleeps.
    /// </summary>
    static SlotIdentity? ReceiverIdentity(HidppBus bus, byte slot, string slotKey)
    {
        var pair = bus.ReadLongRegister(0xFF, 0xB5, (byte)(0x50 + slot));
        if (pair is null || pair.Length < 8 || pair[0] != 0x50 + slot) return null;
        var identity = new SlotIdentity
        {
            Id = pair.AsSpan(4, 4).ContainsAnyExcept((byte)0) ? "logi:" + Convert.ToHexString(pair, 4, 4).ToLowerInvariant() : slotKey,
            Kind = (pair[1] & 0x0F) switch
            {
                1 or 3 => DeviceKind.Keyboard,
                2 or 8 or 9 => DeviceKind.Mouse,
                _ => DeviceKind.Unknown,
            },
            Name = "Logitech device",
        };
        var name = bus.ReadLongRegister(0xFF, 0xB5, (byte)(0x60 + slot), 1);
        if (name is not null && name.Length > 3 && name[2] > 0)
            identity.Name = System.Text.Encoding.ASCII.GetString(name, 3, Math.Min(name[2], name.Length - 3)).Trim('\0', ' ');
        Log.Debug($"{slotKey}: receiver record {identity.Name} {identity.Kind} {identity.Id}");
        return identity;
    }

    /// <param name="linked">The receiver's view of the slot. Devices nap between keystrokes and then don't answer,
    /// so "linked" decides connected; a failed ping only means "asleep".</param>
    DeviceStatus? ReadDevice(HidppBus bus, string key, byte index, bool? linked)
    {
        if (linked == false)
        {
            return _identityCache.TryGetValue(key, out var gone)
                ? new DeviceStatus { Id = gone.Id, Name = gone.Name, Kind = gone.Kind, Transport = Transport.Dongle }
                : null;
        }

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
            {
                var napping = new DeviceStatus
                {
                    Id = known.Id, Name = known.Name, Kind = known.Kind, Transport = Transport.Dongle,
                    Connected = linked == true,
                };
                if (_pushed.TryRemove(key, out var pushed))
                {
                    napping.Battery = pushed.Battery;
                    napping.Charging = pushed.Charging;
                }
                return napping;
            }
            return null;
        }

        // Identities saved by an earlier run are trusted while the device naps, and re-read once it answers
        // (the slot may have been re-paired with another device since).
        if (!_verified.Contains(key) || !_identityCache.TryGetValue(key, out var identity))
        {
            identity = new SlotIdentity { Name = "Logitech device", Kind = DeviceKind.Unknown, Id = key };
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
            _verified.Add(key);
            bool changed = !_identityCache.TryGetValue(key, out var old)
                           || old.Id != identity.Id || old.Name != identity.Name || old.Kind != identity.Kind;
            _identityCache[key] = identity;
            if (changed) SaveIdentities();
        }

        var status = new DeviceStatus
        {
            Id = identity.Id,
            Name = identity.Name,
            Kind = identity.Kind,
            Transport = Transport.Dongle,
            Connected = true,
        };

        _pushed.TryRemove(key, out _);
        if (Feature(FeatureUnifiedBattery) is byte ub && bus.Request(index, ub, 1) is { } u)
        {
            status.Battery = u[0] <= 100 ? u[0] : null;
            status.Charging = u[2] is 1 or 2;
            RememberBatteryFeature(key, identity, FeatureUnifiedBattery, ub);
        }
        else if (Feature(FeatureBatteryStatus) is byte bs && bus.Request(index, bs, 0) is { } s)
        {
            status.Battery = s[0] is > 0 and <= 100 ? s[0] : null;
            status.Charging = s[2] is 1 or 2 or 4;
            RememberBatteryFeature(key, identity, FeatureBatteryStatus, bs);
        }
        else if (Feature(FeatureBatteryVoltage) is byte bv && bus.Request(index, bv, 0) is { } v)
        {
            status.Battery = VoltageToPercent((v[0] << 8) | v[1]);
            status.Charging = (v[2] & 0x80) != 0;
            RememberBatteryFeature(key, identity, FeatureBatteryVoltage, bv);
        }

        if (features is not null) _featureCache[key] = features;
        return status;
    }

    void RememberBatteryFeature(string key, SlotIdentity identity, ushort feature, byte index)
    {
        bool changed = false;
        // The same device paired in several slots of this receiver reports through whichever one is linked.
        foreach (var (slot, other) in _identityCache)
        {
            if (slot != key && other.Id != identity.Id) continue;
            _batteryFeatures[slot] = (feature, index);
            if (other.BatteryFeature == feature && other.BatteryIndex == index) continue;
            other.BatteryFeature = feature;
            other.BatteryIndex = index;
            changed = true;
        }
        if (changed) SaveIdentities();
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

        /// <summary>HID++ 1.0 long register read (sub-id 0x83); the reply comes back as a long report.</summary>
        public byte[]? ReadLongRegister(byte device, byte register, params byte[] args) => Raw(device, 0x83, register, args);

        /// <summary>
        /// Asks the receiver to re-announce every paired device (register 0x02 = 0x02, what Solaar does on start).
        /// It changes no setting; it only emits one 0x41 report per slot. Returns slot → link established.
        /// </summary>
        public Dictionary<byte, bool> QueryLinks()
        {
            var links = new Dictionary<byte, bool>();
            // Written directly rather than through Raw: the 0x41 reports can arrive before the write's ack.
            if (!shortCh.Write(new byte[] { ShortReport, 0xFF, 0x80, 0x02, 0x02, 0, 0 })) return links;
            var until = Environment.TickCount64 + 500;
            while (Environment.TickCount64 < until)
            {
                using var cts = new CancellationTokenSource((int)Math.Max(1, until - Environment.TickCount64));
                byte[]? r;
                try { r = shortCh.ReadAsync(cts.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { break; }
                if (r is null) break;
                if (r.Length > 4 && r[0] == ShortReport && r[2] == 0x41 && r[1] is >= 1 and <= 6)
                    links[r[1]] = (r[4] & 0x40) == 0;
            }
            return links;
        }

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
