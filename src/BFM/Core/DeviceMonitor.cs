using Bfm.Providers;

namespace Bfm.Core;

/// <summary>Polls the providers and keeps the merged, last-known state of every device.</summary>
internal sealed class DeviceMonitor
{
    readonly IReadOnlyList<IBatteryProvider> _providers;
    readonly Settings _settings;
    readonly Dictionary<string, DeviceStatus> _devices = new();
    readonly object _gate = new();
    DateTime _lastDonglePoll = DateTime.MinValue;
    int _busy;

    public event Action? Changed;
    public event Action<DeviceStatus, AlertKind>? Alert;

    public BatteryHistory History { get; } = BatteryHistory.Load();

    public DeviceMonitor(Settings settings, IReadOnlyList<IBatteryProvider> providers)
    {
        _settings = settings;
        _providers = providers;
        // Logitech devices used to be keyed by receiver slot ("logi:c548:2"); they are now keyed by unit ID.
        foreach (var legacy in settings.Cache.Keys.Where(k => System.Text.RegularExpressions.Regex.IsMatch(k, @"^logi:[0-9a-f]{4}:\d$")).ToList())
            settings.Cache.Remove(legacy);
        foreach (var (id, c) in settings.Cache)
        {
            _devices[id] = new DeviceStatus
            {
                Id = id,
                Name = c.Name,
                Kind = c.Kind,
                Transport = c.Transport,
                Battery = c.Battery,
                Updated = c.Updated,
                BluetoothAddress = c.BluetoothAddress,
                LastSeen = c.LastSeen,
            };
        }
    }

    /// <summary>
    /// Multi-link peripherals (e.g. Logitech Easy-Switch: Bluetooth on one channel, Bolt receiver on another)
    /// show up once per link. Entries with the same model name and kind but a different transport are one
    /// device: keep the live link, else the most recently read. Same-transport twins stay separate.
    /// </summary>
    static IEnumerable<DeviceStatus> CollapseMultiLink(IEnumerable<DeviceStatus> devices)
    {
        foreach (var group in devices.GroupBy(d => (Normalize(d.Name), d.Kind)))
        {
            var best = group.OrderByDescending(d => d.Connected).ThenByDescending(d => d.Updated ?? DateTime.MinValue).First();
            foreach (var d in group)
                if (d == best || d.Transport == best.Transport)
                    yield return d;
        }
    }

    static string Normalize(string name) =>
        new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public IReadOnlyList<DeviceStatus> Snapshot()
    {
        lock (_gate)
        {
            var order = _settings.Order;
            return CollapseMultiLink(_devices.Values)
                .Select(d =>
                {
                    var c = d.Clone();
                    c.CustomName = _settings.Prefs(d.Id).Name;
                    return c;
                })
                .OrderBy(d => order.IndexOf(d.Id) is var i && i >= 0 ? i : int.MaxValue)
                .ThenByDescending(d => d.Connected)
                .ThenBy(d => d.Kind)
                .ThenBy(d => d.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public void Forget(string id)
    {
        lock (_gate)
        {
            _devices.Remove(id);
            lock (_settings.SyncRoot) _settings.Cache.Remove(id);
            History.Forget(id);
        }
    }

    /// <summary>Refreshes on a worker thread. Dongles are only queried when their interval has elapsed or <paramref name="includeDongles"/> is set.</summary>
    public Task RefreshAsync(bool includeDongles) => Run(null, includeDongles);

    Task Run(IBatteryProvider? only, bool includeDongles)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            if (only is not null) ScheduleEvent(only);
            return Task.CompletedTask;
        }
        bool dongles = includeDongles || DateTime.UtcNow - _lastDonglePoll >= TimeSpan.FromMinutes(Math.Max(1, _settings.DonglePollMinutes));
        return Task.Run(() =>
        {
            try
            {
                foreach (var p in _providers)
                {
                    if (only is not null ? p != only : p.IsDongle && !dongles) continue;
                    try
                    {
                        Merge(p.Poll());
                        if (p is IEventSource source) source.EnsureWatching(() => ScheduleEvent(p));
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"{p.Name} poll failed: {ex.Message}");
                    }
                }
                if (only is null && dongles) _lastDonglePoll = DateTime.UtcNow;
            }
            finally
            {
                Interlocked.Exchange(ref _busy, 0);
            }
            Changed?.Invoke();
        });
    }

    readonly Dictionary<IBatteryProvider, System.Threading.Timer> _eventTimers = new();

    /// <summary>A device pushed an event: re-read just that provider shortly, coalescing bursts.</summary>
    void ScheduleEvent(IBatteryProvider provider)
    {
        lock (_eventTimers)
        {
            if (_eventTimers.TryGetValue(provider, out var t))
            {
                t.Change(1500, Timeout.Infinite);
                return;
            }
            _eventTimers[provider] = new System.Threading.Timer(_ =>
            {
                lock (_eventTimers)
                {
                    _eventTimers.Remove(provider, out var self);
                    self?.Dispose();
                }
                _ = Run(provider, true);
            }, null, 1500, Timeout.Infinite);
        }
    }

    static readonly TimeSpan OtherLinkGrace = TimeSpan.FromMinutes(5);

    void Merge(IReadOnlyList<DeviceStatus> results)
    {
        var alerts = new List<(DeviceStatus, AlertKind)>();
        bool dirty = false;
        lock (_gate)
        {
            var now = DateTime.Now;
            foreach (var r in results)
            {
                _devices.TryGetValue(r.Id, out var prev);
                if (r.Battery is null && prev?.Battery is int keep)
                {
                    r.Battery = keep;
                    r.Updated = prev.Updated;
                    r.Detail ??= prev.Detail;
                    r.Charging ??= r.Connected ? prev.Charging : null;
                    r.Experimental |= prev.Experimental;
                }
                else if (r.Battery is not null)
                {
                    r.Updated = r.Battery == prev?.Battery && prev?.Updated is not null && !r.Connected ? prev.Updated : now;
                }

                // One device, two links (e.g. Bluetooth and a 2.4 GHz receiver reporting under the same id):
                // an idle link saying "not connected" doesn't override the other link that was live recently.
                bool otherLinkLive = !r.Connected && prev is { Connected: true } && prev.Transport != r.Transport
                    && prev.LastSeen is DateTime seen && now - seen < OtherLinkGrace;
                if (otherLinkLive)
                {
                    r.Connected = true;
                    r.Transport = prev!.Transport;
                    r.Charging = prev.Charging;
                }
                r.LastSeen = otherLinkLive ? prev!.LastSeen : r.Connected ? now : prev?.LastSeen;
                var prefs = _settings.Prefs(r.Id);
                if (r.Battery is int fresh && r.Updated == now)
                {
                    History.Record(r.Id, fresh, r.Charging == true, now);
                    if (_settings.HealthAlerts && fresh != prev?.Battery && History.Drain(r.Id) is { Faster: true } drain
                        && _settings.HealthNotified.GetValueOrDefault(r.Id) != drain.CurrentStart)
                    {
                        lock (_settings.SyncRoot) _settings.HealthNotified[r.Id] = drain.CurrentStart;
                        dirty = true;
                        alerts.Add((Labelled(r, prefs), AlertKind.Health));
                    }
                }

                int threshold = prefs.LowAlert ?? _settings.AlertThreshold;
                if (threshold > 0 && r.Connected && r.Charging != true && r.Battery < threshold
                    && (prev?.Battery is null || prev.Battery >= threshold || !prev.Connected))
                    alerts.Add((Labelled(r, prefs), AlertKind.Low));
                if (prefs.FullAlert && r.Connected && r.Battery == 100 && prev?.Battery is < 100)
                    alerts.Add((Labelled(r, prefs), AlertKind.Full));

                _devices[r.Id] = r;
                if (prev is null || prev.Battery != r.Battery || prev.Name != r.Name || prev.Connected != r.Connected || prev.Charging != r.Charging)
                    dirty = true;
                lock (_settings.SyncRoot)
                    _settings.Cache[r.Id] = new CachedDevice
                    {
                        Name = r.Name,
                        Kind = r.Kind,
                        Transport = r.Transport,
                        Battery = r.Battery,
                        Updated = r.Updated,
                        BluetoothAddress = r.BluetoothAddress,
                        LastSeen = r.LastSeen,
                    };
            }
            // Persist on change so a killed process or a shutdown doesn't forget devices that are now asleep.
            if (dirty) _settings.Save();
            History.SaveIfDirty();
        }
        foreach (var (d, kind) in alerts) Alert?.Invoke(d, kind);
    }

    static DeviceStatus Labelled(DeviceStatus d, DevicePrefs prefs)
    {
        var c = d.Clone();
        c.CustomName = prefs.Name;
        return c;
    }
}

internal enum AlertKind { Low, Full, Health }

internal static class DeviceMonitorOrder
{
    /// <summary>Moves a device one place left or right in the user's chip order, materialising the current order first.</summary>
    public static void Move(Settings settings, IReadOnlyList<DeviceStatus> visible, string id, int delta)
    {
        var order = visible.Select(d => d.Id).ToList();
        foreach (var known in settings.Order)
            if (!order.Contains(known)) order.Add(known);
        int i = order.IndexOf(id);
        int j = Math.Clamp(i + delta, 0, order.Count - 1);
        if (i < 0 || i == j) return;
        order.RemoveAt(i);
        order.Insert(j, id);
        settings.Order = order;
    }
}
