using Bfm.Core;
using Bfm.Providers;
using Bfm.UI;

namespace Bfm;

internal static class Program
{
    internal static IReadOnlyList<IBatteryProvider> CreateProviders() => new IBatteryProvider[]
    {
        new BluetoothProvider(),
        new DualSenseProvider(),
        new AirPodsProvider(),
        new HyperXCloud3Provider(),
        new LogitechHidppProvider(),
        new CompxReceiverProvider(),
    };

    [STAThread]
    static int Main(string[] args)
    {
        bool Has(string flag) => args.Contains(flag, StringComparer.OrdinalIgnoreCase);

        if (Has("--probe"))
            return Probe.Run();
        if (args.Length >= 3 && args[0].Equals("--listen", StringComparison.OrdinalIgnoreCase))
            return Probe.Listen(args[1], int.Parse(args[2]));

        ApplicationConfiguration.Initialize();
        if (Has("--uninstall"))
            return Installer.Uninstall(Has("--quiet"));
        if (Has("--install"))
            return Installer.Install(Has("--quiet"));
        if (Installer.IsSingleFile && !Installer.IsInstalled && !Has("--portable") && Installer.OfferInstall())
            return 0;

        using var mutex = new Mutex(true, @"Local\BFM.BatteryBar", out bool firstInstance);
        // After a self-update the old process may still be closing; wait for it rather than exiting.
        using var showSettings = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\BFM.ShowSettings");
        if (!firstInstance && !(Has("--updated") && WaitForMutex(mutex)))
        {
            AllowSetForegroundWindow(-1);
            showSettings.Set();
            return 0;
        }

        if (Installer.IsInstalled)
        {
            Installer.CleanupAfterUpdate();
            Installer.RepairStartup();
        }

        var settings = Settings.Load();
        DualSenseProvider.LightbarFor = id => settings.Prefs(id).Lightbar;
        var monitor = new DeviceMonitor(settings, CreateProviders());
        var bar = new BarForm(monitor, settings) { JustUpdated = Has("--updated") };
        var wait = ThreadPool.RegisterWaitForSingleObject(showSettings,
            (_, _) => bar.BeginInvokeSafe(() => bar.OpenSettings()), null, Timeout.Infinite, executeOnlyOnce: false);
        Application.Run(bar);
        wait.Unregister(null);
        settings.Save();
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(int processId);

    static bool WaitForMutex(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(TimeSpan.FromSeconds(15));
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }
}
