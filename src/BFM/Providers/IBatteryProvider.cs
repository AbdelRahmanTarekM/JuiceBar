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

/// <summary>A provider that can be told about changes by the device itself instead of waiting for the next poll.</summary>
internal interface IEventSource
{
    /// <summary>Starts (or restarts, if the device was re-plugged) listening; <paramref name="onEvent"/> may run on any thread.</summary>
    void EnsureWatching(Action onEvent);
}
