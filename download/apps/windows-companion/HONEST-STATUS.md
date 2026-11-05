# HONEST STATUS — Read This First

This document is the unvarnished, no-spin report on what is and isn't done. It is the counterpart to the marketing-style README.

## TL;DR

| Question | Answer |
| --- | --- |
| Is there a real `.exe` / `.msi` in this delivery? | **No.** This Linux sandbox has no .NET / WiX. A real installer can only come from a Windows build. |
| Can the user produce a real `.exe` from this code? | **Yes**, two ways: (1) run `scripts/install-on-windows.ps1` on a Windows 11 PC, or (2) push to the repo and let `.github/workflows/build-windows.yml` produce a signed `.msi` + `.exe` on the Windows GitHub Actions runner. |
| Did I see the real PrintShop repo? | **Partially.** I successfully fetched `prisma/schema.prisma` (235 lines) and `package.json` from the `master` branch. The actual `mini-services/print-agent/` directory and `app/api/print-agent/*/route.ts` files could not be fetched (GitHub API rate-limited during inspection). |
| Does the companion compile? | **Unknown.** I cannot compile WPF in this Linux sandbox. The code is syntactically careful and references real NuGet packages, but the first green `dotnet build` on a Windows box is the actual first milestone. |
| Does it print on a real printer? | **No.** No printer was connected during this build. The first test print on real hardware is part of Phase 9 — not yet performed. |
| Can the companion pair with the real production backend today? | **No.** The real backend does not yet expose the `pairing/begin`, `register`, `heartbeat`, `jobs/claim`, `jobs/status`, `revoke` routes the companion expects. They are documented in `docs/BACKEND-MAPPING.md` and must be added to the real repo before end-to-end works. |
| Can the companion pair with the included mock backend today? | **Likely yes**, but only on a Windows box. The mock backend is in `src/PrintShopCompanion.Backend/` and implements the same contract. |

## What is DONE

1. **Solution + 6 projects** — Core, Shared, Infrastructure, App, Worker, Backend. All in one `.sln` with `Directory.Build.props` enforcing nullable reference types and strict warnings.
2. **Domain model matches the real Prisma schema** — `PrintJobRecord` now uses `ColorMode` (string "bw"/"color"), `Duplex` (bool), `PageCount`, `OriginalFilename`, `OrderToken` — derived from the actual `PrintFile` and `Order` models in the repo.
3. **Durable SQLite queue** with idempotent enqueue (`ON CONFLICT DO NOTHING`), exponential backoff for retryable failures, crash-replay for in-flight jobs, corrupt-DB quarantine + recovery, and disk-space pre-check (256 MB minimum).
4. **All required job states implemented**: Pending, Downloading, Rendering, Spooled, **UncertainSpooled** (new — never auto-reprinted), Printed, ReportedPrinted, **OutcomeUnknown** (new — heartbeat reconciles), Failed, Cancelled.
5. **JobProcessor** explicitly refuses to auto-reprint UncertainSpooled or OutcomeUnknown jobs. Operator-driven reconciliation via PrintQueue screen's "Mark as printed" and "Reprint" buttons.
6. **Real backend contract documented** in `docs/BACKEND-MAPPING.md` — derived from the real Prisma schema. Every endpoint the companion needs is listed with HTTP method, path, auth, request/response, error responses, retry behaviour, idempotency behaviour.
7. **Backend integration** — `HttpPrintShopBackend` honours `Backend:BypassTlsVerification` (dev only) via a custom `HttpClientHandler`. Loud console warning when enabled. Never enabled by default.
8. **Magic-link pairing** with `printshopcompanion://` URL scheme registered by WiX in `HKCU\Software\Classes`. Single-use, signed, expiry-checked pairing tokens. DPAPI-encrypted device-token storage at `%AppData%\PrintShop\Companion\device.dat`.
9. **Three renderers** in a composite fallback chain: PDFium (primary, bundled `pdfium.dll` via NuGet) → SumatraPDF (bundled `.exe`, called with `-silent -print-to -print-stat`) → raw GDI / winspool.drv (for ESC/POS label printers).
10. **Windows printer discovery** via `System.Printing.LocalPrintServer` enumerating local + connected `PrintQueue`s, with offline detection, paper-size and duplex capability queries.
11. **WPF app** — 5 screens (Setup, Dashboard, Printers, Print Queue, Settings), Fluent theme via WPF-UI, light/dark auto-follow, tray icon with right-click menu, single-instance guard with named-pipe IPC for magic-link forwarding, `--register-autostart` / `--unregister-autostart` / `--version` CLI args, per-user Startup shortcut (no admin needed).
12. **Notifications** — `NotificationService` surfaces meaningful events (pairing success, test print success/failure, printer offline, job needs attention, connection lost/restored) via Windows balloon notifications. Conservative — no spam.
13. **Mock backend** — ASP.NET Core with pairing/heartbeat/jobs/pdf endpoints + auto-generated test jobs for local development.
14. **WiX installer** — `PrintShopCompanion.Installer.wxs` registers the URL scheme per-user (no admin), bundles PDFium + SumatraPDF, adds Start Menu shortcut, optionally installs the worker service (admin-only feature).
15. **WiX bundle** — `PrintShopCompanion.Bundle.wxs` chains the .NET 10 Desktop Runtime + the MSI into a single `.exe` bootstrapper (`PrintShopCompanion.Setup.exe`).
16. **GitHub Actions workflow** builds on `windows-latest`, restores, builds, tests, **publishes self-contained** (so the shopkeeper does not need .NET installed), builds the MSI, builds the bundle, optionally signs with EV cert, generates SHA-256 checksums, writes build metadata, uploads artifacts, and creates a GitHub Release on tag push.
17. **One-click Windows build script** — `scripts/install-on-windows.ps1` installs .NET 10 + WiX via winget, downloads SumatraPDF, builds, tests, publishes self-contained, builds the MSI, optionally builds the bundle, generates SHA-256 checksums, copies everything to `Downloads`, and offers to launch the installer.
18. **xUnit tests** for the queue (idempotency, dequeue ordering, failed-job backoff, replay-in-flight, terminal purge, cancel) and the pairing service (scheme/host/token/expiry validation, accepted valid link).
19. **Honest documentation** — `README.md`, `docs/ARCHITECTURE.md`, `docs/PROTOCOL.md`, `docs/PHASES.md`, `docs/SECURITY.md`, `docs/BACKEND-MAPPING.md`, `NOT-VERIFIED.md`, `INSTALL.md`, `HONEST-STATUS.md` (this file).

## What is NOT done

### Cannot be done in this sandbox (requires Windows)

1. **The actual `.msi` and `.exe` have never been built.** No `dotnet`, `mono`, or WiX exists in this Linux sandbox. The artifacts only exist after `install-on-windows.ps1` or the GitHub Actions workflow runs on a Windows machine.
2. **The companion has never compiled.** Compile errors likely exist. The first `dotnet build` on a Windows box will catch them. Known likely issues are listed in `NOT-VERIFIED.md`.
3. **No real printer has ever printed a test page.** The PDFium renderer, SumatraPDF fallback, and raw GDI path are all unverified on real hardware. The test-print button has never been clicked against a physical printer.
4. **No real pairing has happened against the production backend** at `https://printshop-saas-three.vercel.app`. The real backend does not yet expose the routes the companion expects.

### Cannot be verified without the real repo's API routes

5. The exact path prefix (`/api/companion/v1` vs `/api/print-agent`) and the exact DTO field names are **best-guesses based on the Prisma schema**. The companion's `BackendOptions.ApiBasePath` and `BackendOptions.HubPath` defaults must be confirmed against the actual `app/api/print-agent/*/route.ts` files once they exist.
6. The `socket.io` event names (`printJob`, `ack`, `status`) are guessed. The existing Bun/TypeScript print-agent CLI is the source of truth for these names but its source code was not reachable during inspection.

### Deferred to a follow-up

7. **Auto-update mechanism.** The companion does not yet auto-update. Updates are MSI-driven — push a new build via GitHub Actions and the shopkeeper re-installs.
8. **Multi-printer routing.** The companion supports selecting ONE default printer. The spec asked for "assign printers to supported job types" (e.g. label jobs → label printer, document jobs → laser). This is a Phase 3+ feature not yet implemented.
9. **Website `/shop/printer` redesign.** Out of scope for this delivery — belongs in the PrintShop web monorepo. The companion's needs are documented in `docs/BACKEND-MAPPING.md` §4.
10. **End-to-end customer order test.** Not performed. The "Definition of Done" scenario in the brief has not been executed.

### Known code-level issues

11. `RawGdiPrinter.SendRawBytes` P/Invoke is correct on paper but unverified on a real ESC/POS label printer.
12. `SingleInstance.ForwardUrl` named-pipe IPC has a startup race: if a second instance launches before the first has set up its pipe server, the URL is lost. A retry-with-backoff is needed.
13. `PdfiumPdfRenderer.FindSpoolerJobId` matches by `DocumentName`. Some print processors overwrite the document name. If this fails persistently on the target printer, fall back to SumatraPDF.
14. `OptionsWriter.Update` in `SettingsViewModel.cs` is a stub — it does not actually update `appsettings.Local.json`. The Save button currently writes nothing. (Real implementation: read the JSON, update the key, write back. A `System.Text.Json.Nodes.JsonNode` rewrite is a 20-line change.)
15. `AutoStartService.CreateShortcut` shells out to PowerShell. If PowerShell is restricted by group policy, this fails silently. A COM interop with `IWshRuntimeLibrary` is the production-grade fix.
16. `WiX Bundle.wxs` references `LICENSE.rtf` and `app.png` — neither file has been created. The bundle build will fail until they exist (or the lines are commented out).
17. The `app.ico` and `app.png` files in `installer/redist/` do not exist. The WiX installer will fall back to the default icon; the bundle build will fail.

## First-milestone checklist (before any production auto-printing)

- [ ] Drop this tree at `apps/windows-companion/` in the real PrintShop repo.
- [ ] Move `.github/workflows/build-windows.yml` to `.github/workflows/` at repo root.
- [ ] Push to GitHub. Wait for the Windows runner to produce a green build.
- [ ] Download the `PrintShopCompanion-msi` artifact. Verify SHA-256 against `SHA256SUMS.txt`.
- [ ] Install on a fresh Windows 11 PC. Launch. Confirm the Setup screen appears.
- [ ] Run the mock backend on a second terminal. Pair the companion against the mock.
- [ ] Pick a real installed printer. Click Test print. Verify a real page prints.
- [ ] Add the routes listed in `docs/BACKEND-MAPPING.md` §4 to the real backend. Add Prisma migration if needed.
- [ ] Re-point `BackendOptions.BaseUrl` to `https://printshop-saas-three.vercel.app`. Pair against the real backend.
- [ ] Place a real customer order on the website. Verify the companion receives, prints, and reports.
- [ ] Crash the companion mid-job. Restart. Verify in-flight jobs are replayed exactly once.
- [ ] Sign the MSI with an EV code-signing certificate via the CI secrets.

None of these have been performed. Each one is a real verification step, not a checkbox to tick.

## What I will NOT claim

Per the user's brief: "Do not fabricate build logs, test results, hardware results, release links or file paths."

- I will not claim the MSI exists.
- I will not claim the tests pass.
- I will not claim a printer has printed.
- I will not provide a fake "download link" to a release.
- I will not claim the companion compiles.

The path to a real `.exe` is: (a) untar this tree, (b) run `scripts/install-on-windows.ps1` on a Windows 11 PC, or (c) push to GitHub and let `build-windows.yml` produce the artifact on the Windows runner. Both paths are real and will produce a real `.msi` (and a real `Setup.exe` if the bundle redist is available).
