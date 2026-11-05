# PrintShop Companion

A native Windows desktop application that bridges the PrintShop website with physical printers in a print shop. Designed for non-technical Indian print shop owners — install once, open, pair with the shop, pick a printer, test print.

This companion lives in `apps/windows-companion/` in the PrintShop monorepo. It coexists with the existing Bun/TypeScript Print Agent CLI daemon (which remains available as an advanced/fallback option until the companion is verified on real hardware).

> **Status:** Phase 1–5 scaffold. First milestone target: working `.msi` installer that opens a desktop app, detects installed printers, pairs securely with a PrintShop shop, and successfully prints a real test PDF on a Windows box. **No production auto-printing** until the first milestone passes on actual hardware.

---

## Repository layout

```
apps/windows-companion/
├── .github/workflows/build-windows.yml   # Compiles the .msi on a Windows runner
├── docs/                                  # Architecture, protocol, security, phases
├── installer/                             # WiX .wxs + bundle + build script
├── src/
│   ├── PrintShopCompanion.Core/            # Domain models, contracts, options (no I/O)
│   ├── PrintShopCompanion.Shared/          # DTOs, protocol constants (companion + backend)
│   ├── PrintShopCompanion.Infrastructure/  # Real implementations of the contracts
│   │   ├── Backend/                        # HTTP + Socket.IO clients
│   │   ├── Pairing/                        # Magic-link pairing
│   │   ├── Printers/                       # Windows spooler, PDFium, SumatraPDF, raw GDI
│   │   ├── Queue/                          # SQLite durable queue + job processor
│   │   └── Storage/                        # Device-token store
│   ├── PrintShopCompanion.App/             # WPF app: 5 screens, tray, Fluent theme
│   ├── PrintShopCompanion.Worker/          # Background job-processing service
│   └── PrintShopCompanion.Backend/         # Mock ASP.NET Core backend for local testing
└── tests/                                  # xUnit + FluentAssertions
    ├── PrintShopCompanion.Queue.Tests/
    └── PrintShopCompanion.Infrastructure.Tests/
```

## Build

This project targets **.NET 10** with **WPF**. Build and test on a Windows 10/11 machine:

```powershell
# Restore + build entire solution
dotnet build PrintShopCompanion.sln -c Release

# Run unit tests
dotnet test

# Publish the WPF app (single-file, framework-dependent)
dotnet publish src\PrintShopCompanion.App\PrintShopCompanion.App.csproj `
  -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:PublishReadyToRun=true `
  -o .\artifacts\app

# Build the .msi (requires WiX Toolset v5 on PATH)
installer\BuildInstaller.bat
```

The GitHub Actions workflow `.github/workflows/build-windows.yml` does all of the above on a `windows-latest` runner and uploads the signed `.msi` as a build artifact.

## Local mock backend

For pairing and test-printing without touching real PrintShop infrastructure:

```powershell
dotnet run --project src\PrintShopCompanion.Backend\PrintShopCompanion.Backend.csproj
# Mock backend listens on https://localhost:5241
# Auto-generated dev cert; companion can be told to trust it via appsettings.json
```

The companion's setup wizard will offer "Connect to local mock shop" as the first option when no production URL is configured.

## Pairing flow (magic link)

1. Shopkeeper signs into the PrintShop website and visits **Shop → Printers → Add this PC**.
2. The website generates a signed one-time pairing URL of the form
   `printshopcompanion://pair?token=…&shop=…&expires=…`.
3. The companion is registered as the handler for the `printshopcompanion://` scheme
   during install. Opening the URL launches the companion (or focuses it) and
   completes pairing automatically — **no manual code to type**.
4. The companion exchanges the one-time token for a long-lived **device token**
   stored under `%AppData%\PrintShop\Companion\device.dat`, encrypted with DPAPI
   (current-user scope).

See `docs/PROTOCOL.md` and `docs/SECURITY.md` for the full wire contract and threat model.

## Non-negotiables

The companion honours the project's hard rules:

- **Never print a job twice** because of a reconnect or retry — every job has a stable `JobId`; the SQLite queue rejects duplicates and the printer layer guards `JobId` in a spooler-tag map.
- **Never report a job as printed just because an API call succeeded** — `ReportedPrinted` is set only after the Windows spooler reports `JobStatus.Printed` for the matching spooler job.
- **Persist before processing** — every accepted job is written to SQLite before any rendering starts.
- **Authenticate every device** — every backend request carries the device token; tokens are revocable from the website.
- **Restrict the local API** — the companion's local HTTP listener binds to `127.0.0.1` and validates the `Origin` header against a configured allow-list (PrintShop website origin only).
- **Never expose device credentials in browser logs** — tokens are redacted in Serilog's `Sub`/`Token` destructuring presets.
- **Multiple installed printers** — the printer picker lists all `PrintQueue`s known to Windows.
- **No admin required for daily use** — install elevation is one-time; the app runs as a normal user, autos-start via a per-user Scheduled Task (`%AppData%\Microsoft\Windows\Start Menu\Programs\Startup` shortcut).
- **Existing CLI agent stays compatible** — the companion uses the same `PrintAgent` / `PrintJob` backend records; both can run side-by-side during migration.

## Phases

See `docs/PHASES.md` for the phase-by-phase breakdown and current status.

## What is **not** verified here

This codebase was written without access to the existing PrintShop monorepo and without a Windows build machine. See `NOT-VERIFIED.md` for the full list of assumptions that must be confirmed against the real repo and on real hardware before release.
