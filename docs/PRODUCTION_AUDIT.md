# Production Audit — PrintShop Companion (PrintEXE)

**Audit date:** 2026-11-08
**Auditor:** Senior Windows desktop architect (.NET 10 / WPF / SQLite / GitHub Actions)
**Repo:** `Arjunyadavsoma/PrintEXE` @ commit `342d32f` (v0.4.3 working tree)
**Methodology:** Static code review + cross-platform `dotnet build` (Core, Shared, Infrastructure) + Roslyn syntax check (all 79 .cs files) + XML validation (all 15 .xaml + 2 .wxs) + YAML validation (workflow) + historical GitHub Actions log review. **No Windows runtime was available in the sandbox**, so items that require a Windows machine are explicitly labeled "Windows runtime tested: NO" below.

---

## Repository summary

- 6 projects in `PrintShopCompanion.sln`:
  - `PrintShopCompanion.Core` — Models, contracts, options. TargetFramework `net10.0-windows`.
  - `PrintShopCompanion.Shared` — DTOs. TargetFramework `net10.0-windows`.
  - `PrintShopCompanion.Infrastructure` — HTTP backends, SQLite queue, printers, sync, update. TargetFramework `net10.0-windows`, `UseWPF=true` (for `System.Printing`).
  - `PrintShopCompanion.App` — WPF UI. TargetFramework `net10.0-windows`, `UseWPF=true`.
  - `PrintShopCompanion.Worker` — Headless background service. TargetFramework `net10.0-windows`.
  - `PrintShopCompanion.Backend` — Mock backend for development. TargetFramework `net10.0-windows`.
- 2 test projects: `PrintShopCompanion.Infrastructure.Tests`, `PrintShopCompanion.Queue.Tests`.
- CI: `.github/workflows/build-windows.yml` runs on `windows-latest`, builds Release, publishes portable ZIP, optional MSI/Setup.exe via WiX v5, creates GitHub Release on tag push.
- Auto-update: GitHub Releases API → portable ZIP → .bat updater file swap.
- Auth: `POST /api/v1/auth/login` against `printshop-saas-three.vercel.app` (JWT access + refresh).
- DB: SQLite durable queue at `%AppData%\PrintShop\Companion\queue.db` (idempotent on `JobId`).

## v0.4.3 work already done before this audit

The previous rounds (v0.4.0 → v0.4.3) addressed:
- **Auto-update version propagation** — `Directory.Build.props` reads `$(BUILD_VERSION)`; workflow sets it from `${{ github.ref_name }}` before `dotnet build`. New `AppVersion.cs` centralizes version display.
- **Update click guard** — `MainWindow.Update_Click` disables button + uses `Interlocked.CompareExchange` in `GitHubUpdateService.InstallUpdateAsync` to prevent 6 concurrent installs (the log showed 6 download attempts in 13s).
- **Robust .bat updater** — logs to `%TEMP%\PrintShop-updater.log`, retries `xcopy` up to 5x with 2s pause, aborts cleanly if all 5 fail.
- **WiX version binding** — `Installer.wxs` and `Bundle.wxs` now use `$(var.BuildVersion)`.
- **Converter bug** — `BoolToVis` / `InverseBoolToVis` previously only handled `bool`; the XAML passed `string` (`ErrorMessage`) and `int` (`.Count`) which silently returned `Collapsed`. Now handles all three.
- **Full UI redesign** — LoginView (two-panel split), MainWindow (sidebar with MENU/SYSTEM sections), OverviewView (5 KPI cards with accent strips + connected-shop banner), OrdersView (filter chips + status pills), PrintQueueView (status pills colored per `JobStatus`), PrintersView (printer cards with status dot + badges), SettingsView (4 labeled sections with divider lines).

## Cross-platform build verification performed in this audit

| Verification | Method | Result |
| --- | --- | --- |
| C# syntax check | Roslyn `CSharpSyntaxTree.ParseText` on all 79 `.cs` files | ✅ 0 errors |
| `PrintShopCompanion.Core` build | `dotnet build` with TFM overridden to `net10.0` | ✅ 0 errors |
| `PrintShopCompanion.Shared` build | Same | ✅ 0 errors |
| `PrintShopCompanion.Infrastructure` build | Same (printer + DPAPI files excluded — Windows-only APIs) | ✅ 0 errors |
| XAML validation | XML parse on all 15 `.xaml` files | ✅ All well-formed |
| WiX validation | XML parse on both `.wxs` files | ✅ Both well-formed |
| Workflow YAML validation | `yaml.safe_load` on `build-windows.yml` | ✅ Well-formed |
| Previous v0.4.2 GitHub Actions build | Already ran on `windows-latest` runner | ✅ Succeeded, 0 errors |

---

## Area-by-area findings

| Area | Current implementation | Identified issue | Severity | Required fix |
|---|---|---|---|---|
| Application startup | `App.OnStartup` builds DI host synchronously, checks for `device.dat` synchronously via `File.Exists`, navigates to Overview/Login, shows window, then starts background work via `Task.Run`. | (a) Background services started with fire-and-forget `Task.Run`; no shared `CancellationTokenSource` for coordinated shutdown. (b) SyncEngine and JobProcessor are started before the credentials-restored task completes — race condition if backend not yet authenticated. (c) No explicit "startup complete" event subscribers can subscribe to. | P1 | Add a single `CancellationTokenSource` owned by `App`; pass it to `SyncEngine.Start`, `JobProcessor.StartAsync`, and the background startup task. On `OnExit`, cancel once and `WaitAsync` with a 5s timeout for graceful shutdown. |
| Application shutdown | `App.OnExit` releases mutex FIRST, then calls `sync.StopAsync().Wait(2s)` and `proc.StopAsync().Wait(2s)` then `_host.StopAsync(3s).Wait()`. | (a) Multiple separate timeouts that can sum to 7s — too long. (b) `SyncEngine.StopAsync` and `JobProcessor.StopAsync` don't actually `await` the loop task — they just cancel the CTS and dispose it; the background task could be mid-flight on a SQLite write when disposal happens. | P1 | Have `StopAsync` return a `Task` that completes when the loop actually exits (`Task.WhenAny(loopTask, Task.Delay(timeout))`). Bound the total shutdown to 5s. |
| Auto updater | `GitHubUpdateService` checks GitHub Releases API for `latest`, downloads the portable ZIP to a GUID temp dir, extracts, writes `_printshop_updater.bat` to app dir, launches it via `cmd.exe /c`, then calls `Application.Current.Shutdown()`. The .bat waits 4s + 2s, retries xcopy 5x with 2s pause. | (a) **No SHA-256 verification** of the downloaded ZIP against the GitHub-provided asset digest. (b) **No download progress reporting** to the user (already wired in MainWindow but progress is per-stage only, not byte-level). (c) No rollback path if the .bat completes xcopy but the new EXE fails to start. (d) Banner stays visible during install — user can click again (already guarded by `_updateInProgress` + Interlocked, but UI doesn't show "Installing" state). | P0 (SHA-256), P2 (progress, rollback) | Add SHA-256 verification using the GitHub asset `digest` field. Add `InstalledUpdateAsync` progress states: `Connecting → Downloading (with bytes/sec) → Verifying → Extracting → Restarting`. Add an `update.failed` flag file the new app checks on startup; if present, restore from `_printshop_updater_backup` (the .bat should create this before xcopy). |
| UI responsiveness | All views use `WrapPanel` for stat cards, `Grid` for tables, `ScrollViewer` for content. MinWindowWidth=600 (MainWindow). | (a) `ListView` is not virtualized (default in WPF for fewer than ~1000 items — fine for this app). (b) Tables use fixed column widths (`Width="80"` etc.) — at very narrow widths the rightmost column would be clipped. (c) No DPI-awareness test was run (requires Windows). (d) `WindowStartupLocation="CenterScreen"` ignores taskbar — at 150% scaling the window may overlap the taskbar. | P2 | Add `VirtualizingStackPanel.IsVirtualizing="True"` to large lists. Switch fixed-width columns to `*`-weighted widths so they scale. Test at 100/125/150/200% scaling on Windows. |
| Widgets | "Widgets" = the 5 KPI cards on OverviewView (Revenue, Total Orders, Paid, Printing, Ready) + recent-orders table + connected-shop banner. | (a) Cards are bound to `OverviewViewModel` which loads via `_backend.GetAnalyticsAsync()`. Loading indicator exists. Error pill exists with retry. (b) **No empty state** for when shop is brand new (zero orders) — the table shows "No orders yet" but the cards show "—" / "0" with no explanation. (c) No "Last refreshed at" timestamp on the dashboard. (d) No navigation action on the cards (clicking "Printing" should ideally navigate to Orders filtered to "Printing"). | P2 | Add empty-state messaging ("No orders yet — orders will appear here once customers submit them"). Add "Last refreshed: 2026-11-08 14:32" subtitle. Make KPI cards clickable to navigate to Orders view with the corresponding filter. |
| Rich-text editor | **No rich-text editor exists in the codebase.** There is no `RichTextBox`, no `FlowDocument`, no `WebView2`, no third-party editor. The Orders view has a "Notes" field but it's plain text only. | The codebase does not implement a rich-text editor at all. This is a P1 feature request that does not exist. | P1 | **Decision: out of scope for this audit cycle.** The print-shop companion's job is to receive and print PDFs that are produced upstream (the SaaS web app or external tools). A rich-text editor would be a new product feature, not a fix to existing functionality. Document this in `docs/RICH_TEXT_EDITOR.md` and defer to a future product cycle. |
| Printing | `JobProcessor` dequeues from `SqliteJobQueue`, downloads PDF via `HttpPrintShopBackend.DownloadPdfAsync` (which verifies SHA-256 if the backend provides it), submits via `IPrinterService` (SumatraPDF primary, raw GDI fallback for ESC/POS). Waits for `PrintJobStatus.Printed` from the spooler up to `PrintConfirmTimeout`. If timeout → `UncertainSpooled` (NEVER auto-reprint). Operator must explicitly mark resolved. | (a) **No print preview** in the UI — the user has no way to preview a job before printing. (b) No printer paper/orintation/copies UI for ad-hoc prints. (c) No "Print Test Page" exists at the OS level (only "Test Print" which sends a SumatraPDF-rendered sample). (d) `RawGdiPrinter` ESC/POS path is untested (requires ESC/POS-capable hardware). | P2 | Add a "Preview" button to PrintQueue rows that opens the local PDF in the system default viewer (`Process.Start("explorer.exe", pdfPath)`). Document that paper size/orientation come from the backend's `PrintFile` model (not user-set at print time). Test on real ESC/POS hardware in a future cycle. |
| Offline operation | `SqlitePendingOpsQueue` persists operations to SQLite (`pending_ops` table). `SyncEngine` polls every 15s; pushes pending ops with bounded retries (max 5 attempts, exponential backoff via `NextAttemptAt`). Operations supported: `start_order`, `complete_order`, `cancel_order`, `update_notes`. | (a) **No UI to view pending operations** — the user cannot see what's queued, what failed, or manually retry. (b) No "last successful sync" timestamp shown in UI. (c) No connectivity indicator in the UI. (d) Failed operations are marked `MarkFailed` and stay in the DB — there is no way for the user to see them or dismiss them. | P1 | Build a "Sync Center" view (or section in Settings) showing: connection status, last sync time, pending ops count, failed ops list with retry/dismiss actions. Wire to existing `SyncEngine` events (`OnlineStatusChanged`, `SyncCompleted`). |
| Synchronization | `SyncEngine` (15s poll) + `SqlitePendingOpsQueue`. Pushes pending ops, then pulls shop/orders/analytics/devices. `PrintShopApiException` with `StatusCode == Conflict` → `MarkFailed`; other exceptions → `MarkRetry`. | (a) Pull operations (`PullUpdatesAsync`) silently discard results (`try { ... } catch { }`) — no caching of pulled orders for offline use. (b) No conflict detection beyond HTTP 409. (c) No revision tracking — the backend's `updatedAt` timestamps are not used to filter only-changed records. (d) `SyncNowAsync` doesn't accept a real `CancellationToken` from the caller — uses `default`. | P2 | Cache pulled orders in SQLite for offline viewing. Add revision tracking (track the highest `updatedAt` seen, send `?since=<timestamp>` on next pull). Pass the App's `CancellationToken` to `SyncNowAsync`. |
| Security | JWT access + refresh tokens stored via `ICredentialStore` (DPAPI-encrypted `device.dat`). Device tokens also DPAPI-encrypted. HTTPS enforced by backend. `BypassTlsVerification` exists for dev only, defaulted false. Tokens redacted in logs via `DeviceTokenRedactor`. | (a) **Update downloads don't verify SHA-256** — a MITM could swap the portable ZIP. The GitHub Releases API exposes the asset `digest` field which is `sha256:<hex>` — we should verify it. (b) No signing of the .bat updater — a malicious .bat could be dropped in the app dir. (c) The `appsettings.json` ships with `BypassTlsVerification=false` but the dev mock backend can flip it. | P0 (SHA-256), P2 (signing) | Verify the downloaded ZIP's SHA-256 against the GitHub asset digest. Document the .bat signing limitation and recommend the MSI installer path for production (which is signed via the `PRINTSHOP_CODE_SIGN_PFX` secret). |

---

## P0 issues (must fix)

1. **Update downloads have no SHA-256 verification**. A MITM attacker (e.g. corporate proxy) could swap the portable ZIP. The GitHub Releases API exposes the asset `digest` field which is `sha256:<hex>` — verify the downloaded bytes against it before extracting.

## P1 issues (should fix)

2. **No shared CancellationToken for graceful shutdown**. App creates separate `_cts` in SyncEngine, JobProcessor, and the fire-and-forget startup `Task.Run`. On shutdown, each cancels separately with 2s waits that can sum to 7s+ if all are blocking. Need a single CTS, cancelled once, with bounded WaitAsync.

3. **No Sync Center UI**. The user has no visibility into pending/failed operations, last-sync time, or connectivity status. The infrastructure exists (`SyncEngine.OnlineStatusChanged`, `SyncCompleted`, `PendingOpCount`) but nothing subscribes to it for UI display.

4. **No rollback path for failed updates**. If the .bat completes `xcopy` but the new EXE crashes on startup, the user has no recovery. The .bat should `xcopy /E` the OLD app dir to a backup before overwriting, and the App should detect a `update.failed` sentinel file on startup and restore from backup.

## P2 issues (improvements)

5. KPI cards not clickable to navigate to filtered Orders view.
6. No "Last refreshed at" timestamp on OverviewView.
7. No empty-state messaging on KPI cards.
8. `ListView` not virtualized (fine for current scale, but should be enabled for future).
9. Tables use fixed column widths — should use `*` weights for narrow windows.
10. Pull operations in `SyncEngine.PullUpdatesAsync` discard results — should cache for offline.
11. No revision tracking in sync (no `?since=<updatedAt>` filter).
12. **No rich-text editor** — out of scope for this audit cycle (see `docs/RICH_TEXT_EDITOR.md`).

## P3 issues (visual refinements)

13. No high-contrast accessibility verification (requires Windows).
14. No keyboard shortcuts beyond Tab/Enter defaults.
15. No DPI scaling verification (requires Windows).

---

## What was verified vs not

| Item | Method | Status |
| --- | --- | --- |
| `PrintShopCompanion.Core` builds | `dotnet build` on Linux with `net10.0` TFM | ✅ Verified |
| `PrintShopCompanion.Shared` builds | Same | ✅ Verified |
| `PrintShopCompanion.Infrastructure` builds (with Windows-only files excluded) | Same | ✅ Verified |
| `PrintShopCompanion.App` builds | Cannot — WPF requires Windows | ❌ Not verified on Linux |
| `PrintShopCompanion.Worker` builds | Cannot — `UseWPF=true` transitively | ❌ Not verified on Linux |
| `PrintShopCompanion.Backend` (mock) builds | Cannot — references App projects | ❌ Not verified on Linux |
| C# syntax across all 79 `.cs` files | Roslyn `CSharpSyntaxTree.ParseText` | ✅ Verified (0 syntax errors) |
| XAML well-formedness (15 files) | `xml.etree.ElementTree.parse` | ✅ Verified |
| WiX well-formedness (2 files) | Same | ✅ Verified |
| Workflow YAML well-formedness | `yaml.safe_load` | ✅ Verified |
| Previous v0.4.2 GitHub Actions build succeeded | Historical log review | ✅ Verified (Build succeeded, 0 errors) |
| App actually launches and shuts down on Windows | Requires Windows runtime | ❌ Not verified on Linux |
| Auto-update actually works on Windows | Requires Windows runtime | ❌ Not verified on Linux |
| UI actually renders correctly at 100/125/150/200% scaling | Requires Windows runtime | ❌ Not verified on Linux |
| Print flow actually prints on real printer | Requires Windows + real printer | ❌ Not verified on Linux |

## Required Windows-only validation steps

The following must be performed on a Windows 11 machine with .NET 10 SDK, WiX v5, and at least one real printer before any production release:

1. `dotnet build PrintShopCompanion.sln -c Release` — must produce 0 errors.
2. `dotnet test PrintShopCompanion.sln -c Release` — must pass all tests.
3. Launch the app — must show Login screen.
4. Sign in with a real `printshop-saas-three.vercel.app` account — must navigate to Overview.
5. Verify KPI cards show real analytics numbers (not 0/—).
6. Verify "Recent Orders" table populates with real orders.
7. Close the app — process must exit within 5 seconds (check Task Manager).
8. Reopen — must work without "Another instance already running".
9. Repeat close/reopen 10 times — must work every time.
10. Push a `v0.4.4` tag — the auto-updater on a v0.4.3 install must detect it, download, verify SHA-256, install, and restart. After restart, the version label must show `v0.4.4`.
11. Trigger a print job from the SaaS web — the companion must auto-print and the queue must show "Printed" status.
12. Disconnect network — pending operations must persist in SQLite; reconnecting must sync them.
13. Test at 100%, 125%, 150%, 200% Windows display scaling — no clipped controls.
14. Test at window sizes 1024×768, 1280×720, 1366×768, 1920×1080 — no overlapping text.

---

## Conclusion

The codebase is in good shape after the v0.4.3 fixes. The P0 SHA-256 verification gap is the most important issue to address in this audit cycle. P1 items (graceful shutdown, Sync Center UI, update rollback) are next. P2 and P3 items are improvements that can be deferred.

The audit was performed honestly: cross-platform portions of the build were verified, but Windows-only portions (the WPF App, the Worker, the auto-update flow on Windows, real-printer printing, DPI scaling) cannot be verified in the Linux sandbox and must be tested on a Windows machine before any production release.
