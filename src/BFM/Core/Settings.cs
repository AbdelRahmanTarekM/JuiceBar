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
}

public sealed class Settings
{
    public int OffsetX { get; set; }
    public bool ShowDisconnected { get; set; } = true;
    public int AlertThreshold { get; set; } = 15;
    public int DonglePollMinutes { get; set; } = 5;
    public HashSet<string> Hidden { get; set; } = new();
    public Dictionary<string, CachedDevice> Cache { get; set; } = new();

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
