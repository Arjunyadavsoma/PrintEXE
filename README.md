# PrintShop Companion

A native Windows desktop application that bridges the PrintShop website with physical printers in a print shop. Designed for non-technical Indian print shop owners — install once, open, pair with the shop, pick a printer, test print.

This companion lives in `apps/windows-companion/` in the PrintShop monorepo. It coexists with the existing Bun/TypeScript Print Agent CLI daemon (which remains available as an advanced/fallback option until the companion is verified on real hardware).

> **Read [`HONEST-STATUS.md`](HONEST-STATUS.md) first.** It is the no-spin report on what is and isn't done. The README is the marketing view; HONEST-STATUS is the engineering view.

## Quick start (non-technical shopkeeper)

See [`INSTALL.md`](INSTALL.md) for step-by-step instructions in plain language. The short version:

1. Download `PrintShopCompanion.Setup.exe` from the PrintShop GitHub Releases page.
2. Double-click it. Click **Install**.
3. Find "PrintShop Companion" in your Start menu. Open it.
4. Sign in to your PrintShop website, go to **Shop → Printers → Add this PC**, click **Connect Computer**.
5. Back in the companion, click **Printers**, pick your printer, click **Test print**.

That's it. The companion now runs in your system tray and prints customer orders automatically.

## Build it yourself (developer)

Two paths to a real `.msi` and `.exe`:

### A. One-click Windows script

```powershell
# On a Windows 11 PC:
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\install-on-windows.ps1 -BuildBundle
```

This installs .NET 10 + WiX via winget, downloads SumatraPDF, builds, tests, publishes self-contained, builds the MSI, builds the Setup.exe bootstrapper, generates SHA-256 checksums, and copies everything to your Downloads folder.

### B. GitHub Actions Windows runner

Push to the repo and `.github/workflows/build-windows.yml` does all of the above on a `windows-latest` runner. The signed MSI + Setup.exe are uploaded as build artifacts and (on tag push) attached to a GitHub Release.

## Repository layout

```
apps/windows-companion/
├── .github/workflows/build-windows.yml   # Builds .msi + .exe on Windows runner
├── docs/                                  # ARCHITECTURE, PROTOCOL, PHASES, SECURITY, BACKEND-MAPPING
├── installer/                             # WiX .wxs (MSI) + .wxs (Bundle .exe) + redist/
├── scripts/install-on-windows.ps1         # One-click Windows build script
├── src/
│   ├── PrintShopCompanion.Core/            # Domain models matching real Prisma schema, contracts, options
│   ├── PrintShopCompanion.Shared/          # DTOs, protocol constants (companion + backend)
│   ├── PrintShopCompanion.Infrastructure/  # Real implementations: SQLite queue, Windows printers, SumatraPDF + raw GDI, Socket.IO + HTTP, magic-link pairing
│   ├── PrintShopCompanion.App/             # WPF app: 5 screens, tray, Fluent theme, autostart
│   ├── PrintShopCompanion.Worker/          # Headless Windows Service that runs the same job processor
│   └── PrintShopCompanion.Backend/         # Mock ASP.NET Core backend for local testing
└── tests/                                  # xUnit + FluentAssertions
```

## Key documents

| Document | What it covers |
| --- | --- |
| [`HONEST-STATUS.md`](HONEST-STATUS.md) | What is and isn't done. Read this first. |
| [`INSTALL.md`](INSTALL.md) | Non-technical shopkeeper install guide. |
| [`NOT-VERIFIED.md`](NOT-VERIFIED.md) | Every assumption that must be verified before release. |
| [`docs/BACKEND-MAPPING.md`](docs/BACKEND-MAPPING.md) | The real-schema-derived contract between companion and backend. |
| [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) | Process model, IPC, logging. |
| [`docs/PROTOCOL.md`](docs/PROTOCOL.md) | Wire protocol (HTTP REST + Socket.IO). |
| [`docs/PHASES.md`](docs/PHASES.md) | Phase-by-phase status. |
| [`docs/SECURITY.md`](docs/SECURITY.md) | Threat model + mitigations. |

## Non-negotiables (project hard rules)

The companion honours:

- **Never print a job twice** because of a reconnect or retry — SQLite queue is idempotent on `JobId`; UncertainSpooled state never auto-reprints.
- **Never report a job as printed just because an API call succeeded** — `ReportedPrinted` requires the Windows spooler to confirm `PrintJobStatus.Printed` first.
- **Persist before processing** — every accepted job is written to SQLite before any rendering starts.
- **Authenticate every device** — every backend request carries the device token; tokens are revocable from the website.
- **Restrict local API** — local HTTP listener (if enabled) binds to `127.0.0.1` only and validates the `Origin` header.
- **Never expose device credentials in logs** — `DeviceTokenRedactor` enricher strips `*Token`/`*Secret`/`*Password` from log events.
- **Multiple installed printers** — the printer picker lists all `PrintQueue`s known to Windows.
- **No admin required for daily use** — install elevation is one-time (and only for the optional worker service); the app runs as a normal user, auto-starts via a per-user Startup shortcut.
- **Existing CLI agent stays compatible** — the companion uses the same `PrintAgent` / `PrintJob` backend models; both can run side-by-side during migration.

## What this is NOT

- Not a fake `EXE`. No binary installer exists in this delivery because this Linux sandbox cannot compile WPF. See `HONEST-STATUS.md` for the actual path to a real `.exe`.
- Not tested on real printer hardware. The Phase 9 hardware tests have not been performed.
- Not paired against the production backend. The real backend at `https://printshop-saas-three.vercel.app` does not yet expose the routes the companion needs; see `docs/BACKEND-MAPPING.md` §4 for the list of routes that must be added.

The first milestone is: install the MSI on a Windows 11 PC, pair with the mock backend, pick a real printer, and produce a real test print. **That has not been done yet.**

## Local mock backend (for development)

For pairing and test-printing without touching real PrintShop infrastructure:

```powershell
dotnet run --project src\PrintShopCompanion.Backend\PrintShopCompanion.Backend.csproj
# Mock backend listens on https://localhost:5241
```

Then set `Backend:BaseUrl` to `https://localhost:5241` and `Backend:BypassTlsVerification` to `true` in `src\PrintShopCompanion.App\appsettings.Local.json` (dev only — never in production).

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

See `docs/PROTOCOL.md`, `docs/BACKEND-MAPPING.md`, and `docs/SECURITY.md` for the full wire contract and threat model.

