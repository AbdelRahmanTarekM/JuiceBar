using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace Bfm.Interop;

internal sealed record PodsReading(
    ushort Model, int? Left, int? Right, int? Case,
    bool LeftCharging, bool RightCharging, bool CaseCharging, short Rssi);

/// <summary>
/// Short passive BLE scans for Apple "proximity pairing" broadcasts (manufacturer 0x004C, type 0x07),
/// which AirPods and Beats send while in use. Kept in its own class so the WinRT projection is only
/// loaded when a scan actually runs.
/// </summary>
internal static class AppleProximityScanner
{
    const ushort Apple = 0x004C;

    public static Dictionary<ushort, PodsReading> Scan(IReadOnlySet<ushort> models, TimeSpan timeout)
    {
        var found = new Dictionary<ushort, PodsReading>();
        using var done = new ManualResetEventSlim();
        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
        watcher.AdvertisementFilter.Advertisement.ManufacturerData.Add(new BluetoothLEManufacturerData { CompanyId = Apple });
        watcher.Received += (_, e) =>
        {
            foreach (var md in e.Advertisement.GetManufacturerDataByCompanyId(Apple))
            {
                var bytes = new byte[md.Data.Length];
                using (var reader = DataReader.FromBuffer(md.Data))
                    reader.ReadBytes(bytes);
                if (Parse(bytes, e.RawSignalStrengthInDBm) is not { } r || !models.Contains(r.Model)) continue;
                lock (found)
                {
                    // Nearby devices of the same model (someone else's AirPods) are weaker; keep the closest.
                    if (!found.TryGetValue(r.Model, out var old) || r.Rssi > old.Rssi) found[r.Model] = r;
                    if (found.Count >= models.Count) done.Set();
                }
            }
        };

        try
        {
            watcher.Start();
            done.Wait(timeout);
        }
        catch (Exception ex)
        {
            Core.Log.Write("BLE scan failed: " + ex.Message);
        }
        finally
        {
            try { watcher.Stop(); } catch { }
        }
        lock (found) return new Dictionary<ushort, PodsReading>(found);
    }

    /// <summary>Layout as used by OpenPods / AirPodsDesktop.</summary>
    internal static PodsReading? Parse(byte[] d, short rssi)
    {
        if (d.Length < 8 || d[0] != 0x07) return null;
        var model = (ushort)(d[3] | (d[4] << 8));
        bool flipped = (d[5] & 0x20) == 0;
        int hi = d[6] >> 4, lo = d[6] & 0x0F;
        int charge = d[7] >> 4;

        static int? Pct(int n) => n == 15 ? null : Math.Min(n * 10, 100);

        return new PodsReading(
            model,
            Pct(flipped ? lo : hi),
            Pct(flipped ? hi : lo),
            Pct(d[7] & 0x0F),
            flipped ? (charge & 2) != 0 : (charge & 1) != 0,
            flipped ? (charge & 1) != 0 : (charge & 2) != 0,
            (charge & 4) != 0,
            rssi);
    }
}
