using Bfm.Core;

namespace Bfm.Providers;

internal interface IBatteryProvider
{
    string Name { get; }

    /// <summary>True when polling talks to hardware over USB HID (polled less often).</summary>
    bool IsDongle { get; }

    /// <summary>Returns the current state of every device this provider can see. Called off the UI thread.</summary>
    IReadOnlyList<DeviceStatus> Poll();
}
