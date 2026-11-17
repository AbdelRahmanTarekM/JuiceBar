using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace Bfm.Core;

/// <summary>
/// Per-user install, autostart and self-update for the single-file build. Nothing here needs admin:
/// the exe lives in %LOCALAPPDATA%\Programs\BFM and registers itself under HKCU only.
/// </summary>
internal static class Installer
{
    /// <summary>GitHub repository (owner/name) whose latest release is checked for updates.</summary>
    public const string Repo = "AhmedAshrafM/BFM";

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BFM";

    public static string InstallDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "BFM");
    public static string InstalledExe => Path.Combine(InstallDir, "BFM.exe");
    static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "BFM - Battery Bar.lnk");

    static string CurrentExe => Environment.ProcessPath ?? InstalledExe;

    /// <summary>True for the published single-file exe (assemblies have no location on disk), false for dev builds.</summary>
#pragma warning disable IL3000 // the empty Location is exactly what identifies the single-file build
    public static bool IsSingleFile => string.IsNullOrEmpty(typeof(Installer).Assembly.Location);
#pragma warning restore IL3000

    public static bool IsInstalled => string.Equals(Path.GetFullPath(CurrentExe), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    public static Version Version
    {
        get
        {
            var v = typeof(Installer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            int plus = v.IndexOf('+');
            return System.Version.TryParse(plus > 0 ? v[..plus] : v, out var parsed) ? parsed : new Version(0, 0, 0);
        }
    }

    // ---------- autostart ----------

    public static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("BFM") is string;
    }

    public static void SetStartup(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enable) key.SetValue("BFM", $"\"{CurrentExe}\"");
        else key.DeleteValue("BFM", false);
    }

    /// <summary>Keeps an enabled autostart entry pointing at the exe that is actually running.</summary>
    public static void RepairStartup()
    {
        if (IsStartupEnabled()) SetStartup(true);
    }

    // ---------- install / uninstall ----------

    /// <summary>
    /// Copies this exe into the install folder, adds the Start menu shortcut, autostart and an
    /// "Installed apps" entry, then launches the installed copy. Returns false if the user declined.
    /// </summary>
    public static bool OfferInstall()
    {
        bool upgrade = File.Exists(InstalledExe);
        var text = upgrade
            ? $"Update the installed BFM to version {Version.ToString(3)}?"
            : "Install BFM for your account?\n\n" +
              "• Copies BFM to your user folder (no admin needed)\n" +
              "• Adds it to the Start menu and starts it with Windows\n" +
              "• Remove it any time from Settings > Apps > Installed apps\n\n" +
              "Choose No to just run it once from here.";
        var answer = MessageBox.Show(text, "BFM", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.Cancel) return true;
        if (answer == DialogResult.No) return false;
        Install(quiet: false);
        return true;
    }

    /// <summary>Installs (or upgrades) for the current user. Quiet mode is what winget runs: no dialogs, no launch.</summary>
    public static int Install(bool quiet)
    {
        try
        {
            bool wasRunning = StopOtherInstances();
            Directory.CreateDirectory(InstallDir);
            if (!IsInstalled) File.Copy(CurrentExe, InstalledExe, overwrite: true);
            CreateShortcut(ShortcutPath, InstalledExe);
            using (var run = Registry.CurrentUser.CreateSubKey(RunKey))
                run.SetValue("BFM", $"\"{InstalledExe}\"");
            WriteUninstallEntry();
            if (!quiet || wasRunning) Process.Start(new ProcessStartInfo(InstalledExe) { UseShellExecute = true });
            return 0;
        }
        catch (Exception ex)
        {
            Log.Write("install failed: " + ex);
            if (!quiet) MessageBox.Show("Install failed: " + ex.Message, "BFM", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    static void WriteUninstallEntry()
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey);
        key.SetValue("DisplayName", "BFM - Battery Bar");
        key.SetValue("DisplayVersion", Version.ToString(3));
        key.SetValue("Publisher", "BFM");
        key.SetValue("DisplayIcon", InstalledExe);
        key.SetValue("InstallLocation", InstallDir);
        key.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        key.SetValue("QuietUninstallString", $"\"{InstalledExe}\" --uninstall --quiet");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        try { key.SetValue("EstimatedSize", (int)(new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord); }
        catch (IOException) { }
    }

    public static int Uninstall(bool quiet)
    {
        if (!quiet && MessageBox.Show("Remove BFM from this PC?", "BFM", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return 1;
        bool keepData = quiet || MessageBox.Show("Keep your BFM settings and battery history in case you reinstall?", "BFM",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

        StopOtherInstances();
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey, writable: true))
            run?.DeleteValue("BFM", false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        TryDelete(ShortcutPath);
        if (!keepData)
        {
            try { Directory.Delete(Settings.Folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        // A running exe can't delete itself; let a detached shell remove the folder once we've exited.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{InstallDir}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        });
        return 0;
    }

    static bool StopOtherInstances()
    {
        bool any = false;
        foreach (var p in Process.GetProcessesByName("BFM"))
        {
            using (p)
            {
                if (p.Id == Environment.ProcessId) continue;
                try
                {
                    any = true;
                    p.CloseMainWindow();
                    if (!p.WaitForExit(2000)) p.Kill();
                    p.WaitForExit(3000);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return any;
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // ---------- updates ----------

    public sealed record Release(Version Version, string Tag, string ExeUrl, string? ShaUrl, string Notes);

    public static void CleanupAfterUpdate() => TryDelete(InstalledExe + ".old");

    public static async Task<Release?> CheckAsync()
    {
        using var http = NewClient();
        using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest"));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!System.Version.TryParse(tag.TrimStart('v', 'V'), out var version) || version <= Version) return null;

        string? exe = null, sha = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();
            if (string.Equals(name, "BFM.exe", StringComparison.OrdinalIgnoreCase)) exe = url;
            else if (string.Equals(name, "BFM.exe.sha256", StringComparison.OrdinalIgnoreCase)) sha = url;
        }
        if (exe is null) return null;
        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        return new Release(version, tag, exe, sha, notes);
    }

    /// <summary>
    /// Downloads and verifies the release, then swaps it in: the running exe is renamed to .old (Windows
    /// allows renaming a running image) and the new one takes its place. The caller restarts.
    /// </summary>
    public static async Task<bool> ApplyAsync(Release release)
    {
        if (!IsInstalled) return false;
        var tmp = InstalledExe + ".new";
        using var http = NewClient();
        await using (var file = File.Create(tmp))
        await using (var stream = await http.GetStreamAsync(release.ExeUrl))
            await stream.CopyToAsync(file);

        if (release.ShaUrl is not null)
        {
            var expected = (await http.GetStringAsync(release.ShaUrl)).Trim().Split(' ', '\t')[0];
            string actual;
            await using (var file = File.OpenRead(tmp))
                actual = Convert.ToHexString(await SHA256.HashDataAsync(file));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                Log.Write($"update {release.Tag}: checksum mismatch");
                TryDelete(tmp);
                return false;
            }
        }

        TryDelete(InstalledExe + ".old");
        File.Move(InstalledExe, InstalledExe + ".old");
        try
        {
            File.Move(tmp, InstalledExe);
        }
        catch
        {
            File.Move(InstalledExe + ".old", InstalledExe);
            throw;
        }
        WriteUninstallEntryVersion(release.Version);
        return true;
    }

    static void WriteUninstallEntryVersion(Version v)
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey, writable: true);
        key?.SetValue("DisplayVersion", v.ToString(3));
    }

    public static void Restart(string extraArg)
    {
        Process.Start(new ProcessStartInfo(InstalledExe, extraArg) { UseShellExecute = false });
    }

    static HttpClient NewClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"BFM/{Version.ToString(3)}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    // ---------- Start menu shortcut (IShellLink, no scripting host) ----------

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int cch, IntPtr fd, uint flags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int cmd);
        void SetShowCmd(int cmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder path, int cch, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    static void CreateShortcut(string lnk, string target)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
            link.SetDescription("Battery levels of your wireless devices on the taskbar");
            link.SetIconLocation(target, 0);
            ((IPersistFile)link).Save(lnk, true);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }
}
