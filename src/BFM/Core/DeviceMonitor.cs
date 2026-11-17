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
    public event Action<DeviceStatus>? LowBattery;

    public DeviceMonitor(Settings settings, IReadOnlyList<IBatteryProvider> providers)
    {
        _settings = settings;
        _providers = providers;
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
            return CollapseMultiLink(_devices.Values)
                .Select(d => d.Clone())
                .OrderByDescending(d => d.Connected)
                .ThenBy(d => d.Kind)
                .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public void Forget(string id)
    {
        lock (_gate)
        {
            _devices.Remove(id);
            lock (_settings.SyncRoot) _settings.Cache.Remove(id);
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

    void Merge(IReadOnlyList<DeviceStatus> results)
    {
        var alerts = new List<DeviceStatus>();
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

                int threshold = _settings.AlertThreshold;
                if (threshold > 0 && r.Connected && r.Charging != true && r.Battery < threshold
                    && (prev?.Battery is null || prev.Battery >= threshold || !prev.Connected))
                    alerts.Add(r.Clone());

                _devices[r.Id] = r;
                if (prev is null || prev.Battery != r.Battery || prev.Name != r.Name)
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
                    };
            }
            // Persist on change so a killed process or a shutdown doesn't forget devices that are now asleep.
            if (dirty) _settings.Save();
        }
        foreach (var a in alerts) LowBattery?.Invoke(a);
    }
}
