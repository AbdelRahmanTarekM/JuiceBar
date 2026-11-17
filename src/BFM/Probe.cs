using System.Diagnostics;
using System.Text;
using Bfm.Interop;

namespace Bfm;

/// <summary><c>BFM.exe --probe</c>: runs every provider once and writes a diagnostic report.</summary>
internal static class Probe
{
    public static int Run()
    {
        Kernel32.AttachConsole(-1);
        Core.Log.Verbose = true;
        var sb = new StringBuilder();
        sb.AppendLine($"BFM probe {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

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

        sb.AppendLine("== Vendor HID collections on other dongles");
        foreach (var vid in new ushort[] { 0x3554, 0x2B89 })
        {
            foreach (var i in Hid.Enumerate(vid).Where(i => i.UsagePage >= 0xFF00))
            {
                sb.AppendLine($"  {i.Vid:x4}:{i.Pid:x4} mi={i.Interface} page={i.UsagePage:x4}/{i.Usage:x2} in={i.InputLength} out={i.OutputLength} feat={i.FeatureLength} " +
                              $"ids in=[{string.Join(",", Hid.ReportIds(i.Path, 0))}] out=[{string.Join(",", Hid.ReportIds(i.Path, 1))}] feat=[{string.Join(",", Hid.ReportIds(i.Path, 2))}] " +
                              $"product=\"{Hid.GetProductString(i.Path)}\"");
            }
        }

        sb.AppendLine("== Audio endpoints (active)");
        foreach (var e in CoreAudio.List(render: true).Concat(CoreAudio.List(render: false)))
            sb.AppendLine($"  {(e.IsRender ? "out" : "in ")} {e.Name}");

        var text = sb.ToString();
        Console.Write(text);
        var file = Path.Combine(Path.GetTempPath(), "bfm-probe.txt");
        File.WriteAllText(file, text);
        Console.WriteLine("written to " + file);
        return 0;
    }
}
