using Bfm.Core;
using Bfm.Providers;
using Bfm.UI;

namespace Bfm;

internal static class Program
{
    internal static IReadOnlyList<IBatteryProvider> CreateProviders() => new IBatteryProvider[]
    {
        new BluetoothProvider(),
        new HyperXCloud3Provider(),
        new LogitechHidppProvider(),
        new CompxReceiverProvider(),
    };

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--probe", StringComparer.OrdinalIgnoreCase))
            return Probe.Run();

        using var mutex = new Mutex(true, @"Local\BFM.BatteryBar", out bool firstInstance);
        if (!firstInstance) return 0;

        ApplicationConfiguration.Initialize();
        var settings = Settings.Load();
        var monitor = new DeviceMonitor(settings, CreateProviders());
        Application.Run(new BarForm(monitor, settings));
        settings.Save();
        return 0;
    }
}
