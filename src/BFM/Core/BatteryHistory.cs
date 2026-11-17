using System.Text.Json;

namespace Bfm.Core;

/// <summary>
/// Per-device battery readings, stored only when the level or charging state changes, kept for 14 days.
/// Drives the history graph and the time-left estimate.
/// </summary>
internal sealed class BatteryHistory
{
    public readonly record struct Sample(long T, byte L, bool C)
    {
        public DateTime Time => DateTimeOffset.FromUnixTimeSeconds(T).LocalDateTime;
    }

    static readonly TimeSpan Keep = TimeSpan.FromDays(14);
    const int MaxPerDevice = 3000;

    static string FilePath => Path.Combine(Settings.Folder, "history.json");

    readonly Dictionary<string, List<Sample>> _data;
    readonly object _gate = new();
    bool _dirty;

    BatteryHistory(Dictionary<string, List<Sample>> data) => _data = data;

    public static BatteryHistory Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return new BatteryHistory(JsonSerializer.Deserialize<Dictionary<string, List<Sample>>>(File.ReadAllText(FilePath)) ?? new());
        }
        catch (Exception ex)
        {
            Log.Write("history load failed: " + ex.Message);
        }
        return new BatteryHistory(new());
    }

    public void Record(string id, int level, bool charging, DateTime when)
    {
        lock (_gate)
        {
            if (!_data.TryGetValue(id, out var list)) _data[id] = list = new();
            if (list.Count > 0 && list[^1].L == level && list[^1].C == charging) return;
            list.Add(new Sample(new DateTimeOffset(when).ToUnixTimeSeconds(), (byte)Math.Clamp(level, 0, 100), charging));
            long cutoff = DateTimeOffset.Now.Add(-Keep).ToUnixTimeSeconds();
            int old = list.FindIndex(s => s.T >= cutoff);
            if (old > 0) list.RemoveRange(0, old);
            if (list.Count > MaxPerDevice) list.RemoveRange(0, list.Count - MaxPerDevice);
            _dirty = true;
        }
    }

    public void SaveIfDirty()
    {
        lock (_gate)
        {
            if (!_dirty) return;
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_data));
                File.Move(tmp, FilePath, overwrite: true);
                _dirty = false;
            }
            catch (Exception ex)
            {
                Log.Write("history save failed: " + ex.Message);
            }
        }
    }

    public IReadOnlyList<Sample> Get(string id, TimeSpan span)
    {
        lock (_gate)
        {
            if (!_data.TryGetValue(id, out var list)) return Array.Empty<Sample>();
            long cutoff = DateTimeOffset.Now.Add(-span).ToUnixTimeSeconds();
            // Keep the reading just before the window so the graph starts at the right level.
            int first = list.FindIndex(s => s.T >= cutoff);
            if (first < 0) return list.Count > 0 ? new[] { list[^1] } : Array.Empty<Sample>();
            return list.GetRange(Math.Max(0, first - 1), list.Count - Math.Max(0, first - 1));
        }
    }

    public void Forget(string id)
    {
        lock (_gate) _dirty |= _data.Remove(id);
    }

    /// <summary>Typical discharge rate across earlier charges versus the current one, in percent per hour.</summary>
    public readonly record struct DrainInfo(double Typical, double? Current, long CurrentStart)
    {
        public const double FasterRatio = 1.5;
        public bool Faster => Current is double c && c >= Typical * FasterRatio;
    }

    /// <summary>A gap this long between readings means the device was off or out of range, not draining.</summary>
    static readonly TimeSpan MaxGap = TimeSpan.FromHours(12);

    /// <summary>
    /// Splits the history into discharge runs (on battery, level only going down, no long gaps) and compares the
    /// latest run with the ones before it. Null until there are at least three earlier runs covering six hours.
    /// The current rate is only given while the device is on battery with two hours and five percent to go on.
    /// </summary>
    public DrainInfo? Drain(string id)
    {
        var runs = new List<(long Start, long End, int Drop)>();
        bool onBattery;
        lock (_gate)
        {
            if (!_data.TryGetValue(id, out var list) || list.Count < 2) return null;
            onBattery = !list[^1].C;
            int start = 0;
            for (int i = 1; i <= list.Count; i++)
            {
                bool split = i == list.Count || list[i].C || list[i - 1].C || list[i].L > list[i - 1].L + 1
                             || list[i].T - list[i - 1].T > MaxGap.TotalSeconds;
                if (!split) continue;
                if (!list[start].C && i - 1 > start)
                    runs.Add((list[start].T, list[i - 1].T, list[start].L - list[i - 1].L));
                start = i;
            }
            // A run still in progress ends at the latest reading; anything after that is not part of it.
            if (runs.Count > 0 && (!onBattery || runs[^1].End != list[^1].T)) onBattery = false;
        }

        static bool Usable((long Start, long End, int Drop) r, double hours, int drop) =>
            (r.End - r.Start) / 3600.0 >= hours && r.Drop >= drop;

        var current = onBattery && runs.Count > 0 ? runs[^1] : default;
        var earlier = (onBattery ? runs.Take(runs.Count - 1) : runs).Where(r => Usable(r, 1, 3)).ToList();
        double hoursTotal = earlier.Sum(r => (r.End - r.Start) / 3600.0);
        if (earlier.Count < 3 || hoursTotal < 6) return null;
        double typical = earlier.Sum(r => r.Drop) / hoursTotal;
        if (typical <= 0) return null;

        double? now = onBattery && Usable(current, 2, 5) ? current.Drop / ((current.End - current.Start) / 3600.0) : null;
        return new DrainInfo(typical, now, current.Start);
    }

    /// <summary>
    /// Estimates time left on battery (or to full while charging) from the current run of readings:
    /// a least-squares slope over the samples since the last charge started or stopped.
    /// Null until there is at least an hour and a few percent of movement to go on.
    /// </summary>
    public TimeSpan? Estimate(string id, int level, bool charging)
    {
        List<Sample> run;
        lock (_gate)
        {
            if (!_data.TryGetValue(id, out var list) || list.Count < 2) return null;
            int start = list.Count - 1;
            while (start > 0 && list[start - 1].C == charging
                   && (charging ? list[start - 1].L <= list[start].L + 1 : list[start - 1].L >= list[start].L - 1))
                start--;
            run = list.GetRange(start, list.Count - start);
        }
        if (run.Count < 2) return null;
        double spanH = (run[^1].T - run[0].T) / 3600.0;
        if (spanH < 1 || Math.Abs(run[^1].L - run[0].L) < 3) return null;

        double mx = run.Average(s => (s.T - run[0].T) / 3600.0), my = run.Average(s => (double)s.L);
        double num = 0, den = 0;
        foreach (var s in run)
        {
            double x = (s.T - run[0].T) / 3600.0 - mx;
            num += x * (s.L - my);
            den += x * x;
        }
        if (den <= 0) return null;
        double slope = num / den; // percent per hour
        double hours = charging ? (slope > 0.1 ? (100 - level) / slope : -1) : (slope < -0.05 ? level / -slope : -1);
        if (hours < 0 || hours > 24 * 60) return null;
        return TimeSpan.FromHours(hours);
    }
}
