# Contributing to Juice Bar

Thanks for helping. The most valuable contributions are **support for more devices** and **bug reports
with a probe attached**.

## Reporting a device that doesn't work

[Open a device support request](https://github.com/AhmedAshrafM/JuiceBar/issues/new?template=device-support.yml).
It asks for the output of `JuiceBar.exe --probe`, which tells us how the device connects and which
channels it exposes. Addresses and serials are shortened automatically; remove personal device names
(like "Jane's iPhone") before posting.

## Reporting a bug

[Open a bug report](https://github.com/AhmedAshrafM/JuiceBar/issues/new?template=bug-report.yml) with your
Juice Bar version (*Settings > General*), Windows version, what you expected and what happened. Recent lines
from `%APPDATA%\JuiceBar\juicebar.log` help a lot.

## Pull requests

1. Fork, create a branch, and keep each pull request to one change.
2. Build with `dotnet build src\BFM\BFM.csproj -c Release`; it must report 0 warnings and 0 errors.
3. Test on real hardware where it matters and say what you tested in the description.
4. For a new device, follow [docs/adding-a-device.md](docs/adding-a-device.md) and add a row to the README's
   *Supported devices* table.

### Principles

- **Idle cost stays flat.** Juice Bar runs all day. No always-on timers, threads or polling loops; use the
  existing poll and the `IEventSource` hook. Check Task Manager before and after your change.
- **Never send undocumented commands to devices.** Only use requests documented by the vendor or an
  established open-source project, and credit the source in the provider's summary comment. Listen passively
  (`--listen`) to discover anything else.
- **Match the surrounding code.** Same naming, small classes, comments only for what the code can't say.
- **No personal data in the repo.** No real Bluetooth addresses, serials, usernames, logs or settings files;
  use placeholders like `001122334455`.

AI coding agents are welcome; point them at [AGENTS.md](AGENTS.md).

## License

By contributing you agree that your contributions are licensed under the [MIT License](LICENSE).
