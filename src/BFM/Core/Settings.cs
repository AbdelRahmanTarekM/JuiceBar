using System.Text.Json;

namespace Bfm.Core;

public sealed class CachedDevice
{
    public string Name { get; set; } = "";
    public DeviceKind Kind { get; set; }
    public Transport Transport { get; set; }
    public int? Battery { get; set; }
    public DateTime? Updated { get; set; }
    public string? BluetoothAddress { get; set; }
    public DateTime? LastSeen { get; set; }
}

public sealed class DevicePrefs
{
    public string? Name { get; set; }
    /// <summary>Low-battery alert level for this device; null uses the global default, 0 turns it off.</summary>
    public int? LowAlert { get; set; }
    public bool FullAlert { get; set; }
    /// <summary>DualSense lightbar colour as RRGGBB; null leaves the controller's own colour.</summary>
    public string? Lightbar { get; set; }

    public bool IsEmpty => Name is null && LowAlert is null && !FullAlert && Lightbar is null;
}

public sealed class Settings
{
    public int OffsetX { get; set; }
    public bool ShowDisconnected { get; set; } = true;
    public int AlertThreshold { get; set; } = 15;
    public int DonglePollMinutes { get; set; } = 5;
    public HashSet<string> Hidden { get; set; } = new();
    public Dictionary<string, CachedDevice> Cache { get; set; } = new();

    public Dictionary<string, DevicePrefs> Devices { get; set; } = new();
    /// <summary>Chip order chosen by the user; devices not listed follow in the default order.</summary>
    public List<string> Order { get; set; } = new();

    public bool Compact { get; set; }
    /// <summary>In compact mode, hide chips above this level (0 = show all). The lowest device always stays.</summary>
    public int CompactHideAbove { get; set; }

    public bool AudioChip { get; set; } = true;
    /// <summary>The two outputs the audio chip toggles between (endpoint ids); null picks automatically.</summary>
    public string? AudioA { get; set; }
    public string? AudioB { get; set; }

    public bool AutoUpdate { get; set; } = true;
    public DateTime? LastUpdateCheck { get; set; }

    public DevicePrefs Prefs(string id) => Devices.TryGetValue(id, out var p) ? p : new DevicePrefs();

    public DevicePrefs EditPrefs(string id)
    {
        if (!Devices.TryGetValue(id, out var p)) Devices[id] = p = new DevicePrefs();
        return p;
    }

    public void TidyPrefs()
    {
        foreach (var id in Devices.Where(kv => kv.Value.IsEmpty).Select(kv => kv.Key).ToList())
            Devices.Remove(id);
    }

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BFM");

    static string FilePath => Path.Combine(Folder, "settings.json");

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Options) ?? new Settings();
        }
        catch (Exception ex)
        {
            Log.Write("settings load failed: " + ex.Message);
        }
        return new Settings();
    }

    /// <summary>Guards <see cref="Cache"/>, which the monitor updates off the UI thread while saves serialize it.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public object SyncRoot { get; } = new();

    public void Save()
    {
        lock (SyncRoot)
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Write("settings save failed: " + ex.Message);
        }
    }
}
