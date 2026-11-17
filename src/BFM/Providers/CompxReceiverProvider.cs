using Bfm.Core;
using Bfm.Interop;
using Microsoft.Win32;

namespace Bfm.Providers;

/// <summary>
/// Generic 2.4 GHz receivers built on the Compx chipset (vendor 0x3554 and others), on the 0xFF02 collection.
/// Two firmware families:
///  - mice (ATK / VXE / Pulsar): 17-byte report 0x08, command 0x04; level byte 6, cable flag byte 7.
///  - keyboards (Redragon, AULA, RK, Womier): 20-byte report 0x13, command 0x4A; level byte 5,
///    state byte 6 (0x01 on battery, 0x10 cable in, when the level is meaningless).
/// Only devices that answer with a valid checksum are shown.
/// </summary>
internal sealed class CompxReceiverProvider : IBatteryProvider
{
    static readonly ushort[] Vendors = { 0x3554, 0x373B, 0x3710 };

    public string Name => "2.4 GHz receiver";
    public bool IsDongle => true;

    readonly Dictionary<string, string> _names = new();
    readonly HashSet<string> _unsupported = new();

    public IReadOnlyList<DeviceStatus> Poll()
    {
        var list = new List<DeviceStatus>();
        foreach (var vid in Vendors)
        {
            foreach (var ifc in Hid.Enumerate(vid).Where(i => i.UsagePage == 0xFF02 && i.OutputLength >= 17 && i.InputLength >= 17))
            {
                bool keyboard = ifc.OutputLength >= 20;
                var id = $"compx:{ifc.Vid:x4}{ifc.Pid:x4}";
                if (list.Any(d => d.Id == id) || _unsupported.Contains(id)) continue;

                using var ch = HidChannel.Open(ifc);
                if (ch is null)
                {
                    Log.Debug($"{id}: open failed");
                    continue;
                }

                if (!_names.TryGetValue(id, out var name))
                    _names[id] = name = FriendlyName(ifc, keyboard);
                var status = keyboard ? QueryKeyboard(ch, id, name) : QueryMouse(ch, id, name);
                if (status is not null) list.Add(status);
            }
        }
        return list;
    }

    DeviceStatus? QueryMouse(HidChannel ch, string id, string name)
    {
        var frame = new byte[17];
        frame[0] = 0x08;
        frame[1] = 0x04;
        frame[16] = (byte)(0x55 - Sum(frame, 16));
        if (!ch.Write(frame))
        {
            // Windows refuses report ids the collection doesn't declare: a different protocol.
            _unsupported.Add(id);
            Log.Debug($"{id}: write refused");
            return null;
        }

        var r = ch.ReadUntil(x => x.Length >= 17 && x[0] == 0x08 && x[1] == 0x04, 600);
        Log.Debug($"{id}: reply {(r is null ? "none" : Convert.ToHexString(r))}");
        if (r is null || (byte)(0x55 - Sum(r, 16)) != r[16] || r[6] > 100) return null;
        return Make(id, name, DeviceKind.Mouse, r[6], r[7] == 1);
    }

    DeviceStatus? QueryKeyboard(HidChannel ch, string id, string name)
    {
        var frame = new byte[20];
        frame[0] = 0x13;
        frame[1] = 0x4A;
        frame[19] = Sum(frame, 19);

        // A sleeping keyboard can miss the first request while its radio link wakes up.
        for (int attempt = 0; attempt < 2; attempt++)
        {
            if (!ch.Write(frame))
            {
                _unsupported.Add(id);
                Log.Debug($"{id}: write refused");
                return null;
            }
            // The receiver also pushes unrelated 0x13 frames, so match on the command and checksum too.
            var r = ch.ReadUntil(x =>
            {
                if (Log.Verbose) Log.Debug($"{id}: frame {Convert.ToHexString(x)}");
                return x.Length >= 20 && x[0] == 0x13 && (x[1] & 0x7F) == 0x4A && Sum(x, 19) == x[19];
            }, 800);
            Log.Debug($"{id}: reply {(r is null ? "none" : Convert.ToHexString(r))}");
            if (r is null) continue;

            if (r[6] == 0x10) return Make(id, name, DeviceKind.Keyboard, null, true);
            if (r[5] is >= 1 and <= 100) return Make(id, name, DeviceKind.Keyboard, r[5], false);
            return null;
        }
        return null;
    }

    static DeviceStatus Make(string id, string name, DeviceKind kind, int? battery, bool charging) => new()
    {
        Id = id,
        Name = name,
        Kind = kind,
        Transport = Transport.Dongle,
        Battery = battery,
        Charging = charging,
        Connected = true,
        Experimental = true,
        Detail = battery is null && charging ? "Cable connected — level is reported when unplugged" : null,
    };

    static byte Sum(byte[] f, int count)
    {
        int sum = 0;
        for (int i = 0; i < count; i++) sum += f[i];
        return (byte)sum;
    }

    static string FriendlyName(HidInterface ifc, bool keyboard)
    {
        var product = Hid.GetProductString(ifc.Path)?.Trim();
        bool generic = string.IsNullOrEmpty(product) || product.Contains("Receiver", StringComparison.OrdinalIgnoreCase);
        if (!generic) return product!;
        if (keyboard && InstalledKeyboardApp() is { } app) return app;
        return keyboard ? "Wireless keyboard" : "Wireless mouse";
    }

    /// <summary>Vendor configurators register as e.g. "Redragon K688-RGB-PRO", the best model name available.</summary>
    static string? InstalledKeyboardApp()
    {
        string[] roots =
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        };
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        foreach (var root in roots)
        {
            using var key = hive.OpenSubKey(root);
            if (key is null) continue;
            foreach (var sub in key.GetSubKeyNames())
            {
                using var app = key.OpenSubKey(sub);
                if (app?.GetValue("DisplayName") is not string name) continue;
                if (System.Text.RegularExpressions.Regex.IsMatch(name, @"^(Redragon|AULA|Royal Kludge|RK) K?\d", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return name.Replace('-', ' ');
            }
        }
        return null;
    }
}
