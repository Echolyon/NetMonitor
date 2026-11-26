# NetMonitor

A lightweight, local-only **per-process network monitor for Windows**. It shows which application is talking to which server, how much bandwidth each process uses right now, and flags connections that look suspicious. Everything stays on your computer.

> The user interface is currently **Turkish only** (English UI is planned). Türkçe açıklama: [README.tr.md](README.tr.md)

<!-- Add your screenshots to docs/ and keep these lines -->
<!-- ![Connections](docs/connections.png) -->
<!-- ![History](docs/history.png) -->

## Features

- **Live connection table** for TCP and UDP over IPv4 and IPv6: process, PID, local/remote address, state
- **Per-process bandwidth** (download/upload speed and totals), measured with Event Tracing for Windows (ETW)
- **Country and host name** of remote addresses (offline GeoIP database + reverse DNS)
- **Risk hints** with a plain-language reason for every flag (see below)
- **Connection history** stored in a local SQLite database, searchable by period
- **Tray icon and notifications** when a *new* risky destination appears
- Right-click actions (copy IP, open file location), CSV export, protocol/risk filters

## Requirements

- Windows 10 / 11, 64-bit
- **Administrator rights** (needed to listen to the ETW network events; Windows will show a UAC prompt)
- Nothing else: the release build is self-contained, no .NET installation required

## Download and run

1. Download the latest zip from the [Releases](../../releases) page and extract it anywhere.
2. Run `NetMonitor.exe` and accept the UAC prompt.
3. *(Optional)* Add the GeoIP database to see countries, see below.

Windows SmartScreen may warn about an unrecognized app, because the executable is not code-signed. You can verify the download against the SHA-256 checksum published with each release, or build it yourself from source.

### Country lookup (optional)

1. Download **IP to Country Lite** in MMDB format from <https://db-ip.com/db/download/ip-to-country-lite>.
2. Extract the `.mmdb` file and put it into the `GeoIP` folder next to `NetMonitor.exe`.
3. Restart NetMonitor.

## How the risk hints work

Rules are simple, additive and explainable. The *Reason* column shows which ones fired.

| Rule | Points |
|---|---|
| A system process name (`svchost`, `lsass`, ...) runs from outside the Windows folder | +50 |
| Runs from Temp, Downloads, Users\Public or the Recycle Bin | +40 |
| No valid digital signature | +30 |
| Signature present but invalid (file modified) | +60 |
| Runs from AppData | +10 |
| Established connection to an external address on an uncommon port | +10 |

20+ points = *Orta* (medium, yellow), 50+ points = *Yüksek* (high, red).

**Limits, please read:** this is a hint, not a verdict. Many legitimate programs are unsigned or live in AppData, and malware can be signed with stolen certificates. NetMonitor is not an antivirus and does not block anything. Windows system files that are signed through catalogs are treated as normal when they live in the Windows folder.

## Privacy

- Connection history, settings and the error log are stored only on your machine under `%LocalAppData%\NetMonitor\`.
- No telemetry, no accounts, no network calls to any NetMonitor server.
- Country lookup is fully offline.
- Host names are resolved with **reverse DNS**, so the remote IP addresses are sent to *your configured DNS server*, like any other DNS lookup.
- You can delete the history at any time from the *Geçmiş* (History) tab, or choose how long it is kept in *Ayarlar* (Settings).

## Build from source

Requirements: Visual Studio 2022 (".NET desktop development" workload) or the .NET 8 SDK.

```powershell
dotnet build -c Release
```

To create the single-file release package:

```powershell
powershell -ExecutionPolicy Bypass -File .\publish.ps1
```

Tech: C# / .NET 8, WPF, IP Helper API, ETW (`Microsoft.Diagnostics.Tracing.TraceEvent`), SQLite (`Microsoft.Data.Sqlite`), `MaxMind.Db` for MMDB reading.

## Known limitations

- Windows only; interface is Turkish only for now
- Does not start automatically with Windows (the app needs elevation, which requires a scheduled task; planned)
- Shows and alerts, but cannot block connections
- Per-process byte counters come from ETW and may include a small amount of loopback traffic

## Third-party components and data

- [DB-IP IP to Country Lite](https://db-ip.com), licensed under CC BY 4.0 (**IP Geolocation by DB-IP**). The database is not bundled; you download it yourself.
- Microsoft.Diagnostics.Tracing.TraceEvent (MIT), Microsoft.Data.Sqlite (MIT), MaxMind.Db (Apache-2.0)

## License

[MIT](LICENSE.txt)
