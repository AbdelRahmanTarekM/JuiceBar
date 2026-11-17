# BFM — battery bar for the Windows taskbar

A small always-on-top strip over the empty left side of the Windows 11 taskbar that shows the battery
level of Bluetooth and USB-dongle peripherals, with quick controls on click.

## Run

```powershell
dotnet build src\BFM\BFM.csproj -c Release
src\BFM\bin\Release\net10.0-windows\BFM.exe
```

Requires the .NET 10 Desktop Runtime. `BFM.exe --probe` prints what every battery source sees
(also written to `%TEMP%\bfm-probe.txt`).

## Using it

- Hover a chip for details; click it for controls (default output, volume, mic mute, connect/disconnect
  for Bluetooth audio, hide).
- Drag the bar sideways to move it along the taskbar. Right-click for settings: which devices to show,
  disconnected devices, low-battery alert, dongle polling interval, start with Windows.
- Hides itself while a fullscreen app is in front.

Settings and the last-known battery levels live in `%APPDATA%\BFM\settings.json`; errors go to
`%APPDATA%\BFM\bfm.log`.

## Battery sources

| Source | Devices | How |
|---|---|---|
| Bluetooth | Any paired device that reports battery to Windows (BLE battery service, hands-free) | `DEVPKEY_Bluetooth_Battery` on the device node, polled every 60 s and on device changes |
| DualSense / DualSense Edge | Bluetooth or USB cable | Status byte of the controller's input report (hid-playstation layout) |
| AirPods / Beats | Bluetooth, when Windows shows no battery | Passive BLE scan for Apple proximity broadcasts, ≤6 s every 2 min, only while connected; experimental |
| HyperX Cloud III Wireless | 2.4 GHz dongle `03F0:05B7` / `0C9D` | Vendor HID report `66 89` (battery) / `66 8A` (charging), plus pushed events |
| Logitech receivers | Unifying / Bolt / Lightspeed | HID++ 2.0 features `0x1004`, `0x1000`, `0x1001`; read-only; re-read on connect notifications |
| Compx 2.4 GHz receivers | ATK / VXE / Pulsar style (`3554:*` …) | Report `08 04`, checksummed; experimental |

USB dongles are queried every 5 minutes by default (configurable), immediately when a device is
plugged in or removed, and within a couple of seconds when the HyperX headset or a Logitech device
reports a change by itself (an overlapped read stays pending, so listening costs no CPU).

## Footprint

Framework-dependent WinForms, no background threads between polls, no WebView. Measured idle: about
12 MB private memory and ~0.01 % CPU.
