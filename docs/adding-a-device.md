# Adding support for a device

This guide takes a device Juice Bar doesn't show a battery for, works out how the device reports its
battery, and adds a provider for it. It is written so that an AI coding agent can follow it step by step
(see also [AGENTS.md](../AGENTS.md)); every step says how to check it worked.

## 1. Collect a probe

With the device connected and awake:

```powershell
& "$env:LOCALAPPDATA\Programs\JuiceBar\JuiceBar.exe" --probe     # installed copy
# or, from a source checkout:
dotnet build src\BFM\BFM.csproj -c Release
& (Get-ChildItem src\BFM\bin\Release -Recurse -Filter JuiceBar.exe | Select -First 1).FullName --probe
```

The report is printed and saved to `%TEMP%\juicebar-probe.txt`. Sections:

| Section | Meaning |
|---|---|
| `== Bluetooth` | Every paired Bluetooth device and the battery Windows has for it (`battery=-` means none). |
| `== DualSense`, `== HyperX …`, `== Logitech receiver`, … | What each existing provider found. |
| `== Vendor-defined HID collections` | Every HID collection with a vendor usage page (`FF00`-`FFFF`), with report sizes and report ids. Proprietary battery protocols live here. |
| `== HID devices` | One line per HID device (USB or Bluetooth) with its VID:PID, product string and collections. |
| `== Bluetooth LE devices offering the Battery Service (0x180F)` | BLE devices that expose the standard battery service, by VID/PID. |

Bluetooth addresses and Logitech serials are shortened to their last 4 digits. Device names are whatever
Windows calls them, so remove personal names before sharing a probe publicly.

## 2. Identify the protocol

Find the device in the probe and match it to one of these cases.

### A. Bluetooth, battery shown in Windows but not in Juice Bar

`BluetoothProvider` reads the same value as Windows (`DEVPKEY_Bluetooth_Battery`). If the probe shows a
battery under `== Bluetooth`, the device works: check it isn't hidden (*Settings > Devices > Show on the bar*).

### B. Bluetooth LE with the Battery Service (0x180F) but `battery=-`

The device offers the standard BLE battery characteristic (0x2A19) but Windows hasn't stored a value, which
happens with some devices that only notify. Reconnect it first. If the value stays empty, write a provider
that reads 0x2A19 directly with `Windows.Devices.Bluetooth.GenericAttributeProfile` (the project already
references the Windows SDK projection; see `Interop/AppleProximityScanner.cs` for WinRT Bluetooth usage).
Read on `Poll`, don't keep a subscription open unless the device only notifies.

### C. Bluetooth without the Battery Service

Battery is in a vendor channel. Common ones:

- **Vendor HID over Bluetooth**: the device shows under `== HID devices` as `bt` with a vendor collection.
  Same approach as case D, but enumerate with `includeBluetooth: true` (see `DualSenseProvider`).
- **BLE advertisements**: some earbuds broadcast battery in manufacturer data (AirPods do; see `AirPodsProvider`).
- **Vendor GATT service**: a custom 128-bit service UUID. Needs the vendor protocol from an open-source project.

### D. USB 2.4 GHz dongle

Look up the dongle's VID:PID in `== HID devices`, then its rows under `== Vendor-defined HID collections`.
Note the usage page, input/output/feature lengths and report ids.

- **Logitech (`046d`)**: Unifying, Bolt and Lightspeed receivers speak **HID++** (report ids 0x10 short /
  0x11 long on page `FF00`). `LogitechHidppProvider` already handles HID++ 2.0 battery features
  (`0x1000`, `0x1001`, `0x1004`); if a Logitech device isn't shown, debug that provider rather than writing a new one.
- **Other vendors**: search for an existing open-source implementation for that VID:PID:
  the Linux kernel (`drivers/hid/hid-*.c`), [HeadsetControl](https://github.com/Sapd/HeadsetControl),
  [Solaar](https://github.com/pwr-Solaar/Solaar), [OpenRGB](https://gitlab.com/CalcProgrammer1/OpenRGB),
  [libratbag](https://github.com/libratbag/libratbag), or device-specific projects. Note the exact request
  bytes and which response byte holds the level.
- **No known protocol**: listen passively and see whether the device pushes battery on its own:

  ```powershell
  & "$env:LOCALAPPDATA\Programs\JuiceBar\JuiceBar.exe" --listen 3554 120
  ```

  This prints every input report from that vendor's vendor-defined collections for 120 seconds and sends
  nothing. Turn the device off and on, plug in its charger, and watch for a byte that tracks the battery.
  **Do not** guess commands by sending arbitrary output or feature reports; some vendor commands change
  settings or enter firmware-update mode.

### E. No battery channel at all

Some receivers simply don't carry battery (UGREEN `2B89:0043` is one). If the device also pairs over
Bluetooth, report it under the Bluetooth id so the two links merge into one chip (see `UgreenReceiverProvider`).

## 3. Write the provider

Create `src/BFM/Providers/<Vendor><Model>Provider.cs`. The interface:

```csharp
internal interface IBatteryProvider
{
    string Name { get; }                 // shown in --probe
    bool IsDongle { get; }               // true: polled every DonglePollMinutes (default 5) instead of every 60 s
    IReadOnlyList<DeviceStatus> Poll();  // called off the UI thread; return every device you can see
}

internal interface IEventSource          // optional: the device pushes reports
{
    void EnsureWatching(Action onEvent); // start or restart listeners; call onEvent to trigger a re-poll
}
```

A minimal vendor-HID provider, modelled on `HyperXCloud3Provider.cs`:

```csharp
using Bfm.Core;
using Bfm.Interop;

namespace Bfm.Providers;

/// <summary>
/// Acme Wireless Headset over its 2.4 GHz dongle. Protocol from <link to the source you used>:
/// vendor collection 0xFF00, report 0x05; reply byte 3 is the level in percent.
/// </summary>
internal sealed class AcmeHeadsetProvider : IBatteryProvider
{
    const ushort Vid = 0x1234, Pid = 0x5678;

    public string Name => "Acme headset";
    public bool IsDongle => true;

    public IReadOnlyList<DeviceStatus> Poll()
    {
        var ifc = Hid.Enumerate(Vid, Pid).FirstOrDefault(i => i.UsagePage == 0xFF00 && i.OutputLength >= 2);
        if (ifc is null) return Array.Empty<DeviceStatus>();      // dongle not plugged in

        var status = new DeviceStatus
        {
            Id = $"acme:{Pid:x4}",                                // stable and unique; see below
            Name = Hid.GetProductString(ifc.Path) ?? "Acme Wireless Headset",
            Kind = DeviceKind.Headset,
            Transport = Transport.Dongle,
        };
        using var ch = HidChannel.Open(ifc);
        if (ch is not null && ch.Write(new byte[] { 0x05, 0x01 })
            && ch.ReadUntil(r => r.Length > 3 && r[0] == 0x05, 1000) is { } reply && reply[3] <= 100)
        {
            status.Battery = reply[3];
            status.Connected = true;                              // the headset answered, so it is on
        }
        return new[] { status };
    }
}
```

Then register it in `Program.CreateProviders()` in `src/BFM/Program.cs`.

Rules that keep the app correct and light:

- **Id**: stable across restarts and unique, prefixed by the provider (`acme:`). If the device can also be
  reached over Bluetooth, use `bt:<ADDRESS>` so both links merge into one chip.
- **Connected vs. battery**: return the dongle's device even when the headset is off (`Connected = false`,
  `Battery = null`); the app keeps the last known level and shows *last seen*.
- **Charging**: set `Charging` only if the protocol tells you; leave it `null` otherwise.
- **Unconfirmed protocol**: set `Experimental = true` until someone confirms it on real hardware.
- **Timeouts**: keep every read under about 1 s (`ReadUntil(..., 1000)`); `Poll` runs for every provider in turn.
- **Events**: if the device pushes battery reports, implement `IEventSource` with
  `HidEventWatcher.Start(ifc, isEvent, onEvent)` so the bar updates immediately; the poll still runs as a fallback.
- **No new timers or threads**, and dispose every `HidChannel`.
- **Credit the source** of the protocol in the class summary.

## 4. Verify

1. `dotnet build src\BFM\BFM.csproj -c Release`: 0 warnings, 0 errors.
2. Run the dev build's `--probe`. Your provider appears as `== <Name>` with the device, `connected=True`
   and a plausible `battery=` while the device is on, and `connected=False` after switching it off.
3. Compare the level with the vendor's own app or indicator, ideally at two different levels.
4. Exit the installed copy (right-click the bar > *Exit*), start the dev build, and check the chip on the
   bar: level, tooltip, and the popup. Leave it running for 10 minutes; `%APPDATA%\JuiceBar\juicebar.log`
   should have no new errors.
5. Check idle cost didn't change: in Task Manager the process stays around 15 MB private memory and near 0% CPU.
6. Publish and reinstall your build if you want to keep using it:
   `tools\publish.ps1`, then `dist\JuiceBar.exe --install`.

## 5. Contribute it back

Open a pull request with the provider, a probe excerpt for the device (personal names removed), what you
compared the readings against, and the protocol source. Add a row to the *Supported devices* table in the
README. See [CONTRIBUTING.md](../CONTRIBUTING.md).
