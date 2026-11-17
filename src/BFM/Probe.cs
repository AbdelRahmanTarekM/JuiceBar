using System.Diagnostics;
using System.Text;
using Bfm.Interop;

namespace Bfm;

/// <summary><c>JuiceBar.exe --probe</c>: runs every provider once and writes a diagnostic report.</summary>
internal static class Probe
{
    /// <summary><c>JuiceBar.exe --listen VID SECONDS</c>: passively prints every input report on that vendor's
    /// vendor-defined collections. Sends nothing to the device.</summary>
    public static int Listen(string vidHex, int seconds)
    {
        Kernel32.AttachConsole(-1);
        ushort vid = Convert.ToUInt16(vidHex, 16);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var tasks = Hid.Enumerate(vid).Where(i => i.UsagePage >= 0xFF00).Select(async ifc =>
        {
            using var ch = HidChannel.Open(ifc);
            if (ch is null) { Console.WriteLine($"open failed {ifc.Path}"); return; }
            Console.WriteLine($"listening {ifc.Vid:x4}:{ifc.Pid:x4} page={ifc.UsagePage:x4}/{ifc.Usage:x2}");
            while (!cts.IsCancellationRequested)
            {
                byte[]? r;
                try { r = await ch.ReadAsync(cts.Token); } catch (OperationCanceledException) { break; }
                if (r is null) break;
                Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {ifc.UsagePage:x4} {Convert.ToHexString(r)}");
            }
        }).ToArray();
        Task.WaitAll(tasks);
        return 0;
    }

    public static int Run()
    {
        Kernel32.AttachConsole(-1);
        Core.Log.Verbose = true;
        var sb = new StringBuilder();
        sb.AppendLine($"Juice Bar probe {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        foreach (var p in Program.CreateProviders())
        {
            sb.AppendLine($"== {p.Name}");
            var sw = Stopwatch.StartNew();
            try
            {
                foreach (var d in p.Poll())
                {
                    sb.AppendLine($"  {d.Name} [{d.Id}] kind={d.Kind} via={d.TransportLabel} connected={d.Connected} " +
                                  $"battery={(d.Battery?.ToString() ?? "-")} charging={(d.Charging?.ToString() ?? "-")}" +
                                  (d.Experimental ? " (experimental)" : ""));
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("  error: " + ex);
            }
            sb.AppendLine($"  ({sw.ElapsedMilliseconds} ms)");
        }

        // Vendor-defined collections (usage page FF00+) are where proprietary battery protocols live.
        sb.AppendLine("== Vendor-defined HID collections (all devices, USB and Bluetooth)");
        foreach (var i in Hid.Enumerate(vid: null, includeBluetooth: true).Where(i => i.UsagePage >= 0xFF00)
                     .OrderBy(i => i.Vid).ThenBy(i => i.Pid).ThenBy(i => i.Interface))
        {
            sb.AppendLine($"  {i.Vid:x4}:{i.Pid:x4} {(i.IsBluetooth ? "bt" : $"mi={i.Interface}")} page={i.UsagePage:x4}/{i.Usage:x2} in={i.InputLength} out={i.OutputLength} feat={i.FeatureLength} " +
                          $"ids in=[{string.Join(",", Hid.ReportIds(i.Path, 0))}] out=[{string.Join(",", Hid.ReportIds(i.Path, 1))}] feat=[{string.Join(",", Hid.ReportIds(i.Path, 2))}] " +
                          $"product=\"{Hid.GetProductString(i.Path)}\"");
        }

        sb.AppendLine("== HID devices (one line per device)");
        foreach (var g in Hid.Enumerate(vid: null, includeBluetooth: true).GroupBy(i => (i.Vid, i.Pid, i.IsBluetooth)).OrderBy(g => g.Key.Vid))
        {
            var pages = string.Join(",", g.Select(i => $"{i.UsagePage:x4}/{i.Usage:x2}").Distinct());
            sb.AppendLine($"  {g.Key.Vid:x4}:{g.Key.Pid:x4} {(g.Key.IsBluetooth ? "bt " : "usb")} \"{Hid.GetProductString(g.First().Path)}\" collections={pages}");
        }

        sb.AppendLine("== Bluetooth LE devices offering the Battery Service (0x180F)");
        foreach (var n in SetupApi.GetNodes("BTHLEDEVICE", id => id.Contains("{0000180F-", StringComparison.OrdinalIgnoreCase)))
            sb.AppendLine($"  {n.FriendlyName ?? "(no name)"} [{n.InstanceId.Split('\\')[1]}]");

        sb.AppendLine("== Audio endpoints (active)");
        foreach (var e in CoreAudio.List(render: true).Concat(CoreAudio.List(render: false)))
            sb.AppendLine($"  {(e.IsRender ? "out" : "in ")} {e.Name}");

        // Shorten Bluetooth addresses and Logitech unit ids (serial numbers) so the report can be shared publicly;
        // the last 4 digits still tell devices apart.
        var text = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"(?<![0-9A-Fa-f-])[0-9A-Fa-f]{8}([0-9A-Fa-f]{4})(?![0-9A-Fa-f])", "xxxxxxxx$1");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"logi:[0-9a-f]{4}([0-9a-f]{4})\b", "logi:xxxx$1");
        text += "Names shown above are the ones Windows uses; edit out anything personal before sharing.\n";
        Console.Write(text);
        var file = Path.Combine(Path.GetTempPath(), "juicebar-probe.txt");
        File.WriteAllText(file, text);
        Console.WriteLine("written to " + file);
        return 0;
    }
}
