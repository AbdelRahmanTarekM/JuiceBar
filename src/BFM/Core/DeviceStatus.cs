namespace Bfm.Core;

public enum DeviceKind { Unknown, Headset, Earbuds, Keyboard, Mouse, Controller, Phone }

public enum Transport { Bluetooth, BluetoothLE, Dongle, Usb }

public sealed class DeviceStatus
{
    public required string Id { get; init; }
    public required string Name { get; set; }
    public DeviceKind Kind { get; set; }
    public Transport Transport { get; set; }
    public int? Battery { get; set; }
    public bool? Charging { get; set; }
    public bool Connected { get; set; }
    public DateTime? Updated { get; set; }
    public string? BluetoothAddress { get; set; }

    /// <summary>Extra reading shown in the tooltip and flyout, e.g. "L 80% · R 70% · Case 40%".</summary>
    public string? Detail { get; set; }

    /// <summary>Reading comes from a reverse-engineered vendor protocol not yet confirmed on this hardware.</summary>
    public bool Experimental { get; set; }

    public DeviceStatus Clone() => (DeviceStatus)MemberwiseClone();

    public string TransportLabel => Transport switch
    {
        Transport.Bluetooth => "Bluetooth",
        Transport.BluetoothLE => "Bluetooth LE",
        Transport.Usb => "USB cable",
        _ => "USB dongle",
    };
}
