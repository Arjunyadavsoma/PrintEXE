# Third-party dependency inventory

**Rule (brief Part 14):** independent implementation preferred; any future code reuse requires a license review first. No source code from the four studied references was incorporated. This inventory lists every third-party component shipped or referenced, with license and scope. The QZ Tray JS API examples are Public Domain; we target the same *wire protocol shape* only where the production SaaS contract defines it.

## NuGet dependencies (production)

| Package | Version | License | Where used | Why |
|---|---|---|---|---|
| Microsoft.Data.Sqlite | 10.0.0 | MIT | Infrastructure | Queue DB access |
| SQLitePCLRaw.bundle_e_sqlcipher | 2.1.11 | Apache-2.0 (bundle; SQLCipher itself is BSD-style licensed) | Infrastructure | At-rest encryption of queue.db (fail-closed) |
| SQLitePCLRaw.lib.e_sqlite3 | 3.50.3 | Apache-2.0 | Infrastructure/App | Native SQLite |
| System.Security.Cryptography.ProtectedData | 10.0.2 | MIT | Infrastructure | DPAPI device-token + key storage (newly explicit — audit risk fix) |
| Microsoft.Extensions.* (Hosting, Http, Options, Configuration, Logging) | 10.0.x | MIT | all | DI/configuration/logging host |
| Serilog / Serilog.Sinks.File / Serilog.Sinks.EventLog / Serilog.Extensions.Hosting | 4.2.0 / 6.0.0 / 4.0.0 / 9.0.0 | Apache-2.0 | App, Worker, Infrastructure | Structured logging + redactor enricher |
| SocketIOClient | 3.1.2 | MIT | Infrastructure | Realtime job/command/revocation transport |
| Newtonsoft.Json | 13.0.4 | MIT | App, Worker | Socket.IO payload handling |
| PDFtoImage (+ SkiaSharp, bblanchon.PDFium.* natives) | 5.4.0 (+ 4.x/3.x natives) | MIT (PDFtoImage), MIT (SkiaSharp), Apache-2.0 (PDFium bindings) | Infrastructure | Managed PDF validation/page-count/preview + GDI raster fallback (**new this branch**) |
| CommunityToolkit.Mvvm | 8.4.0 | MIT | App | Source-generated MVVM |
| H.NotifyIcon.Wpf | 2.4.1 | MIT | App | Tray icon + menu |
| Microsoft.Extensions.Hosting.WindowsServices | 10.0.2 | MIT | Worker | Windows Service host capability |

## Build/test-only dependencies

| Package | License | Scope |
|---|---|---|
| Microsoft.NET.Test.Sdk, xunit, xunit.runner.visualstudio, FluentAssertions | MIT/Apache-2.0 | all test projects |
| Microsoft.AspNetCore.App (FrameworkReference) | MIT | Integration.Tests fake backend |
| WiX v5 (installer tooling) | MS-RL (tool only; output MSI is ours) | installer/ |
| .NET 10 SDK / Windows Desktop targeting packs | MIT | build |

## Third-party binaries distributed with the app

| Binary | License | Notes |
|---|---|---|
| SumatraPDF.exe (bundled, primary PDF renderer) | GPL-3.0 | **Invocation-only**: spawned as a separate process with command-line args — no SumatraPDF code is linked or embedded; GPL obligations therefore do not attach to PrintEXE (see INSTALLATION; keep the binary update process documented). If legal review ever prefers zero GPL artifacts, the raw-GDI PDFium path (Apache/MIT stack) is the fallback renderer. |
| .NET 10 Desktop Runtime (MSI bundle chain) | MIT | redistributed via bundle |

## Reference projects studied (nothing incorporated)

| Project | License | Relationship |
|---|---|---|
| QZ Tray (qzind/tray) | LGPL-2.1-only | Architectural techniques only; zero source copied (see ENGINEERING_DECISIONS D-01..D-15) |
| PaperCut Mobility Print | Proprietary | Documentation reference only |
| ZMA printer agent (Encryft/zma-printer) | MIT | Pattern reference only; no code copied |
| PrintNode | Proprietary | Documentation/behavior reference only |

## Maintenance rules

1. Every new dependency gets a row here with license + purpose + alternative.
2. Vulnerability pins (NU1903-grade) go into csproj comments, as the existing transitive-override block does.
3. AGPL/GPL libraries that would force source disclosure are rejected at review (GPL-3 invocation-only tooling is acceptable and flagged).
