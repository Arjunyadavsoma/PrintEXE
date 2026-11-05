# Build Phases

| Phase | Deliverable                                       | Status      | Notes |
| ----- | ------------------------------------------------- | ----------- | ----- |
| 1     | Windows app foundation, tray icon and installer   | Scaffold    | `PrintShopCompanion.App` + `TrayIconHost` + WiX `.wxs` |
| 2     | Automatic website pairing and device registration | Scaffold    | `MagicLinkPairingService` + `printshopcompanion://` scheme registration in WiX |
| 3     | Printer discovery and selection                    | Scaffold    | `WindowsPrinterService.DiscoverPrinters()` + Printers screen |
| 4     | PDF rendering and real test printing               | Scaffold    | `PdfiumPdfRenderer` primary + `SumatraPdfPrinter` fallback + `RawGdiPrinter` for label printers + `CompositePdfRenderer` chain |
| 5     | Background job processing and durable queue         | Scaffold    | `SqliteJobQueue` + `JobProcessor` + `SocketIoRealtimeClient` + `HttpFallbackPoller` + worker service |
| 6     | Job acknowledgements and error recovery            | Partial     | `MarkFailedAsync` + `MarkPrintedAsync` + `MarkReportedPrintedAsync` implemented; retry-from-ack not yet wired |
| 7     | Auto-start, reconnect and automatic updates        | Partial     | Auto-start via per-user Startup shortcut; reconnect is implicit in `SocketIOClient`; auto-update is stubbed |
| 8     | Website `/shop/printer` redesign                   | Out of scope| Belongs in the PrintShop web monorepo, not in `apps/windows-companion/` |
| 9     | Security, hardware testing and release              | Not started | Requires real printer hardware and the production backend |

## Per-phase status detail

### Phase 1 — Foundation
- `PrintShopCompanion.sln` with 6 projects.
- `PrintShopCompanion.App` (WPF, .NET 10, single-instance, tray icon, theme service).
- `PrintShopCompanion.Worker` (headless Windows Service host).
- `PrintShopCompanion.Backend` (mock for local testing).
- WiX v5 installer source + `BuildInstaller.bat`.
- GitHub Actions workflow that publishes the app and builds the .msi on a Windows runner.

### Phase 2 — Pairing
- `MagicLinkPairingService` validates scheme, host, presence of `token`, and URL-level `expires`.
- `DeviceTokenStore` persists the long-lived device token encrypted with DPAPI (current-user scope).
- WiX installer registers `HKCU\Software\Classes\printshopcompanion\shell\open\command`.

### Phase 3 — Printer discovery
- `WindowsPrinterService.DiscoverPrinters()` enumerates all local + connected `PrintQueue`s.
- `PrintersViewModel` exposes them to the UI; selection persists to `PrinterOptions.SelectedPrinter`.
- Test-print button generates a 1-page PDF and submits it through the configured renderer.

### Phase 4 — PDF + real printing
- `PdfiumPdfRenderer` uses bundled `pdfium.dll` to rasterise pages and submit via `PrintDocument`.
- `SumatraPdfPrinter` falls back to bundled `SumatraPDF.exe -silent -print-to -print-stat`.
- `RawGdiPrinter` covers raw ESC/POS / PCL printers via direct winspool.drv P/Invoke.
- `CompositePdfRenderer` walks the configured chain so a single failure doesn't break the print.
- **Open:** Real-hardware validation against at least one inkjet, one laser, and one dot-matrix printer.

### Phase 5 — Queue + worker
- `SqliteJobQueue` enforces idempotency on `JobId`, exponential backoff for retryable failures, and `ReplayInFlightAsync` on startup.
- `JobProcessor` is the single consumer; ensures persist-before-process, ack-on-receipt, spooler-confirm-before-report.
- `SocketIoRealtimeClient` is the primary transport (parity with existing Print Agent CLI).
- `HttpFallbackPoller` is the secondary transport (used when Socket.IO cannot connect, and as the sole transport against the mock backend).

### Phase 6 — Acks + error recovery (partial)
- Done: idempotent enqueue, ack, MarkPrinted, MarkReportedPrinted, MarkFailed, Cancel.
- Open: heartbeat-driven retry of `ReportJobStatus` for jobs in `Printed` state whose report call failed; UI affordance for operator-driven requeue.

### Phase 7 — Auto-start, reconnect, updates
- Done: per-user Startup shortcut via `AutoStartService`.
- Done: automatic reconnect via `SocketIOClient`'s built-in reconnection policy.
- Open: in-app auto-update mechanism (currently the MSI is the update path).

### Phase 8 — Website redesign
- Belongs in the web monorepo; companion deliverable is the local-API spec in `docs/PROTOCOL.md`.

### Phase 9 — Security, hardware, release
- See `docs/SECURITY.md` for the threat model and `NOT-VERIFIED.md` for the list of items that must pass on real hardware before release.

## First-milestone checklist (before any production auto-printing)

- [ ] `dotnet build PrintShopCompanion.sln` succeeds on a Windows 11 box with .NET 10 SDK installed.
- [ ] `wix build PrintShopCompanion.Installer.wxs` produces a valid `PrintShopCompanion.msi`.
- [ ] Installing the MSI does not prompt for elevation beyond the worker-service feature.
- [ ] Launching `PrintShopCompanion.exe` shows the Setup screen.
- [ ] Running the mock backend (`dotnet run --project src\PrintShopCompanion.Backend`) and clicking "Begin pairing" completes pairing via a magic link generated by `POST /api/companion/v1/pairing/begin`.
- [ ] The Printers screen lists at least one real printer installed on the test box.
- [ ] The "Test print" button prints the 1-page test PDF to a real printer.
- [ ] The Settings screen's "Revoke this PC" wipes `device.dat` and returns the companion to the Setup screen on next launch.
