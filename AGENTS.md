# Guide for AI coding agents

This file tells an AI agent (Cursor, Claude Code, Codex, Copilot and similar) how to install Juice Bar for a
user, and how to add support for a device it doesn't recognise. Humans can follow it too.

Juice Bar is a .NET 10 WinForms app for Windows 10/11 that shows peripheral battery levels on the taskbar.
Code lives in `src/BFM` (the folder keeps the app's original name; the exe is `JuiceBar.exe`).

## Ground rules

- **Ask before changing the user's system** beyond installing Juice Bar itself (for example installing the
  .NET SDK, vendor software or drivers).
- **Never send unknown output or feature reports to a device.** Vendor HID commands can change device
  settings, and some can put a device into firmware-update mode. Read passively first (`--listen`), and only
  send commands that are documented by the vendor or by an established open-source project for that exact
  device family. Cite the source in the provider's summary comment.
- **Keep idle cost flat.** No new timers, threads or polling loops that run all the time. Use the existing
  poll (`IBatteryProvider.Poll`, every 60 s, or every `DonglePollMinutes` when `IsDongle` is true) and the
  event hook (`IEventSource`) instead.
- **Don't commit personal data.** Probe output, logs and `%APPDATA%\JuiceBar\*.json` contain device names and
  addresses. Use placeholders such as `001122334455` in comments and tests.

## 1. Install Juice Bar end to end

### From a release (normal users)

1. Check the .NET 10 Desktop Runtime: `dotnet --list-runtimes` should list `Microsoft.WindowsDesktop.App 10.*`.
   If it doesn't, ask the user, then install it, for example `winget install Microsoft.DotNet.DesktopRuntime.10`.
2. Download the latest release asset and check its hash:

   ```powershell
   $dir = "$env:TEMP\juicebar"; New-Item -ItemType Directory -Force $dir | Out-Null
   $rel = Invoke-RestMethod https://api.github.com/repos/AhmedAshrafM/JuiceBar/releases/latest
   foreach ($a in $rel.assets) { Invoke-WebRequest $a.browser_download_url -OutFile (Join-Path $dir $a.name) }
   $expected = (Get-Content "$dir\JuiceBar.exe.sha256").Split(' ')[0]
   if ((Get-FileHash "$dir\JuiceBar.exe" -Algorithm SHA256).Hash -ne $expected) { throw "hash mismatch" }
   ```

3. Install quietly. Don't wait on the process: the installer starts the installed copy and returns.

   ```powershell
   Start-Process "$env:TEMP\juicebar\JuiceBar.exe" -ArgumentList "--install","--quiet"
   ```

### From source (developers)

Needs the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`, ask first).

```powershell
dotnet build src\BFM\BFM.csproj -c Release     # quick check that it compiles
powershell -ExecutionPolicy Bypass -File tools\publish.ps1   # dist\JuiceBar.exe + .sha256
Start-Process dist\JuiceBar.exe -ArgumentList "--install","--quiet"
```

### Verify the install

All of these should hold a few seconds after installing:

```powershell
Get-Process JuiceBar | Select-Object Path                                   # running from %LOCALAPPDATA%\Programs\JuiceBar
(Get-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Run).JuiceBar  # starts with Windows
Get-ItemProperty HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\JuiceBar | Select DisplayName, DisplayVersion
Test-Path "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\Juice Bar.lnk"
Test-Path "$env:APPDATA\JuiceBar\settings.json"                             # written after the first poll
```

Then ask the user to look at the left side of the taskbar. Uninstall with `JuiceBar.exe --uninstall --quiet`
(from the install folder); it keeps `%APPDATA%\JuiceBar`.

## 2. A device isn't recognised

Follow [docs/adding-a-device.md](docs/adding-a-device.md). In short:

1. Run `& "$env:LOCALAPPDATA\Programs\JuiceBar\JuiceBar.exe" --probe` (or the dev build) and read
   `%TEMP%\juicebar-probe.txt`. Bluetooth addresses and serials are shortened in the output.
2. Classify the device from the probe:
   - listed under `== Bluetooth` **with** a battery: it already works; check it isn't hidden in Settings.
   - listed under `== Bluetooth` **without** a battery, and present under `Battery Service (0x180F)`: Windows
     should fill the level; reconnect the device, then consider reading the GATT characteristic directly.
   - listed under `== Bluetooth` without a battery and no 0x180F: the device uses a vendor channel
     (vendor HID over Bluetooth, BLE advertisements like AirPods, or a vendor GATT service).
   - only under `== HID devices` as a USB dongle: look for its vendor-defined collections (usage page
     `FF00`-`FFFF`) under `== Vendor-defined HID collections`; that's where battery protocols live.
     Logitech receivers (`046d`) use HID++ and are already handled by `LogitechHidppProvider`.
3. Find the protocol: search for an open-source driver for that VID:PID (Linux kernel `hid-*` drivers,
   Solaar, HeadsetControl, OpenRGB, HyperHeadset, libratbag). Then confirm passively with
   `JuiceBar.exe --listen <VID> 60` while the battery changes or the device sleeps and wakes.
4. Write a provider in `src/BFM/Providers` implementing `IBatteryProvider` (and `IEventSource` if the device
   pushes reports), register it in `Program.CreateProviders()`, then verify with `--probe` and on the bar.

## Repository map

| Path | What's there |
|---|---|
| `src/BFM/Program.cs` | Entry point, command-line switches, provider list (`CreateProviders`). |
| `src/BFM/Providers/` | One class per battery source. Start with `HyperXCloud3Provider.cs` (simplest vendor HID example). |
| `src/BFM/Interop/Hid.cs` | HID enumeration (`Hid.Enumerate`), `HidChannel` (write, read with timeout), product strings. |
| `src/BFM/Interop/HidEventWatcher.cs` | Background read loop for devices that push reports. |
| `src/BFM/Core/DeviceMonitor.cs` | Polling schedule, merging results from all providers, alerts. |
| `src/BFM/Core/DeviceStatus.cs` | What a provider returns per device. |
| `src/BFM/Probe.cs` | `--probe` and `--listen` diagnostics. |
| `src/BFM/UI/` | Taskbar bar, device popup, settings window. |
| `tools/publish.ps1` | Builds the single-file exe and checksum. |
| `.github/workflows/release.yml` | Builds and publishes a release when a `v*` tag is pushed. |

Build check before any commit: `dotnet build src\BFM\BFM.csproj -c Release` must report 0 warnings and 0 errors.
