# BFM — battery bar for the Windows taskbar

A small always-on-top strip over the empty left side of the Windows 11 taskbar that shows the battery
level of Bluetooth and USB-dongle peripherals, with quick controls on click.

## Install

Download `BFM.exe` from the [latest release](https://github.com/AhmedAshrafM/BFM/releases/latest) and run it.
It offers to install itself for your account (no admin): it copies itself to
`%LOCALAPPDATA%\Programs\BFM`, adds a Start menu entry, starts with Windows and appears under
Settings > Apps > Installed apps for removal. Choose **No** to just run it once.

Requires the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) (Windows asks
to download it on first run if it's missing).

Once installed, BFM checks GitHub for a new release once a day, verifies its SHA-256 and swaps itself
in place (Settings > General > *Install updates automatically*).

Command line: `BFM.exe --install [--quiet]`, `--uninstall [--quiet]`, `--portable` (run without the
install prompt), `--probe` (prints what every battery source sees, also to `%TEMP%\bfm-probe.txt`).
Launching BFM while it is already running opens its settings.

## Using it

- Hover a chip for details; click it for controls: default output, volume, mic mute, connect /
  disconnect (stays paired), DualSense lightbar colour and *Turn off controller*, a 7-day battery
  graph and an estimate of time left.
- Right-click a chip to rename it, move it left or right, set its low-battery alert or *Notify when
  fully charged*, or hide it.
- The audio chip switches the default output between your headset and speakers in one click
  (right-click to pick any output; choose the pair in Settings > Audio).
- Compact mode shows only icon and number, and can hide devices above a level.
- Drag the bar sideways to move it along the taskbar. It follows an auto-hiding taskbar and hides
  while a fullscreen app is in front.

Data lives in `%APPDATA%\BFM`: `settings.json` (settings, per-device preferences, last-known levels),
`history.json` (14 days of level changes), `bfm.log` (errors).

## Battery sources

| Source | Devices | How |
|---|---|---|
| Bluetooth | Any paired device that reports battery to Windows (BLE battery service, hands-free) | `DEVPKEY_Bluetooth_Battery` on the device node, polled every 60 s and on device changes |
| DualSense / DualSense Edge | Bluetooth or USB cable | Status byte of the controller's input report (hid-playstation layout); lightbar via output report 0x02 / 0x31 |
| AirPods / Beats | Bluetooth, when Windows shows no battery | Passive BLE scan for Apple proximity broadcasts, ≤6 s every 2 min, only while connected; experimental |
| HyperX Cloud III Wireless | 2.4 GHz dongle `03F0:05B7` / `0C9D` | Vendor HID report `66 89` (battery) / `66 8A` (charging), plus pushed events |
| Logitech receivers | Unifying / Bolt / Lightspeed | HID++ 2.0 features `0x1004`, `0x1000`, `0x1001`, plus battery events the device pushes; link state from receiver notifications |
| Compx 2.4 GHz receivers | Redragon / AULA / ATK / VXE style (`3554:*`) | Keyboard report `13 4A`, mouse report `08 04`, checksummed |

| UGREEN receivers | 2.4 GHz `2B89:0043` (e.g. M571P) | No battery channel exists on the receiver; shows "connected" while the mouse moves through it (raw input, sampled every 30 s) under the Bluetooth pairing's chip, carrying the last Bluetooth reading |

Devices reachable over two links (Bluetooth and a receiver) show as one chip.

## Build and release

```powershell
dotnet build src\BFM\BFM.csproj -c Release      # dev build, runs without the install prompt
tools\publish.ps1                                # dist\BFM.exe + BFM.exe.sha256
```

Pushing a tag `vX.Y.Z` runs `.github/workflows/release.yml`, which publishes the exe and checksum to a
GitHub release (bump `<Version>` in `BFM.csproj` first). `packaging/winget` has the manifest templates
for submitting to winget-pkgs.

## Footprint

Framework-dependent WinForms, no background threads between polls, no WebView. Measured idle: about
15 MB working set and ~0.01 % CPU. The settings window and flyouts are built only while open.
The exe is ~25 MB on disk, almost all of it the Windows SDK projection used by the Bluetooth LE scanner;
it is loaded on demand and doesn't add to memory use.
