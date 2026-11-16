# PrintEXE Companion — Pre-Coding Audit

**Branch:** `feat/unified-print-exe-cloud-integration`
**Baseline commit (main):** `d65cb19` (2026-11-16)
**Audit date:** 2026-11-16
**Sandbox:** Linux x86_64, **no .NET SDK installed** (`dotnet` not found; no `/usr/share/dotnet`). All 8 projects target `net10.0-windows` and therefore cannot build on this sandbox. Build/test verification is deferred to the Windows GitHub Actions runner (see §14).

This audit reports **what the code actually does**, not what the docs claim. Source of truth is the code on disk + the real SaaS backend at `Arjunyadavsoma/printshop-saas`. Two parallel reports — `docs/CONTRACT_COMPATIBILITY.md` (contract reconciliation) and `docs/UPGRADE_PLAN.md` (the work) — are written separately; this document is the baseline snapshot.

---

## 0. Repository layout

```
PrintShopCompanion.sln        8 projects, net10.0-windows (forced by Directory.Build.props)
├── src/
│   ├── PrintShopCompanion.Core            — models, contracts, enums (no deps)
│   ├── PrintShopCompanion.Shared          — shared DTOs (no deps)
│   ├── PrintShopCompanion.Infrastructure   — HTTP, Socket.IO, SQLite/SQLCipher, printers, queue, storage, updates
│   ├── PrintShopCompanion.App             — WPF app + tray (WinExe, UseWPF)
│   ├── PrintShopCompanion.Worker           — console worker host (Microsoft.NET.Sdk.Worker)
│   └── PrintShopCompanion.Backend          — MOCK backend (Microsoft.NET.Sdk.Web) — local dev only
├── tests/
│   ├── PrintShopCompanion.Infrastructure.Tests  — 33 tests
│   └── PrintShopCompanion.Queue.Tests           — 7 tests
├── installer/                     — WiX v5 sources (NOT built by CI today)
└── .github/workflows/build-windows.yml  — two jobs: build (windows-latest) + release (ubuntu, tag-only)
```

`Directory.Build.props` forces `net10.0-windows`, `Nullable=enable`, `ImplicitUsings=enable`, `LangVersion=latest`, **`TreatWarningsAsErrors=false`** (README's "strict warnings" claim is inaccurate), `EnforceCodeStyleInBuild=true`. Version `0.4.7` hardcoded, overridable via `BUILD_VERSION` env var. Adds global `using System.IO;` and `using System.Net.Http;` to every project.

---

## 1. Documentation — actual state vs claims

| File | Actual content | Drift vs code |
|---|---|---|
| `HONEST-STATUS.md` | "No-spin" status. Says no `.exe`/`.msi` produced in Linux sandbox; real backend only partially fetched; companion compile status **Unknown**; no real printer tested; mock backend pairs on Windows only. Flags `OptionsWriter.Update` is a stub; `SingleInstance.ForwardUrl` race; `app.ico`/`app.png` missing in `installer/redist/`. | Accurate. (Itself the most honest doc.) |
| `NOT-VERIFIED.md` | Lists assumptions to verify on Windows: REST endpoints written from spec, Socket.IO hub method names "best-guesses", `PrintAgent`/`PrintJob` Prisma models "not seen". | The Prisma models **are** in the SaaS repo (see `docs/CONTRACT_COMPATIBILITY.md`); the "best-guess" Socket.IO names are actually confirmed correct against the real backend. |
| `README.md` | Claims app lives at `apps/windows-companion/` (false — repo root is `/home/z/my-project`). Claims "two renderers: SumatraPDF → raw GDI". Claims local HTTP listener binds `127.0.0.1` and validates `Origin`. | Path claim false. Local HTTP listener **does not exist** in code. |
| `INSTALL.md` | Shopkeeper-facing. References `PrintShopCompanion.Setup.exe` + `.msi`. Describes a magic-link pairing flow. | MSI/Setup.exe no longer built by CI. Pairing is actually **paste-token**, not magic-link (see §2). |
| `docs/ARCHITECTURE.md` | Claims `DeviceTokenRedactor` enricher in logger; claims `EventLog` sink. | `DeviceTokenRedactor` exists but is **never wired** into the logger. EventLog sink declared in csproj but not configured in `App.OnStartup`. |
| `docs/PROTOCOL.md` | §2.3: `POST /pair` with `{pairingToken, deviceId, hostname, operatingSystem, appVersion}`. §4.1: Socket.IO hub methods `registerDevice`, `DeliverJob`, `AckJob`, `ReportStatus`. | **Wrong.** Backend has no `/pair` endpoint (paste-token only). Socket.IO has only one inbound handler: `auth`. See `docs/CONTOCOL-DRIFT.md`. |
| `docs/BACKEND-MAPPING.md` | §3.2: same stale `/pair` + `printJob`/`ack` event names. §3: "real prefix may be `/api/print-agent`". | Wrong. Real prefix is `/api/v1` (confirmed against SaaS source). |
| `docs/SECURITY.md` | Threat model includes "Local API binds to 127.0.0.1 only" row; "MSI is per-user". | Local API does not exist. MSI is **per-machine** (`Scope="perMachine"` in `Installer.wxs:21`). |
| `docs/PHASES.md` | Says `UncertainSpooled` never auto-reprints. | `ReplayInFlightAsync` resets it to `Pending` on startup — logic inconsistency (§5). |
| `docs/BUILD_AND_RELEASE.md` | Documents the Windows CI workflow accurately. §10 confirms MSI/Setup.exe steps were removed in favor of ZIP. | Accurate. |

---

## 2. Pairing — actual behavior

**Files:** `src/PrintShopCompanion.Infrastructure/Pairing/MagicLinkPairingService.cs`, `src/PrintShopCompanion.Core/Contracts/IPrintShopBackend.cs`, `src/PrintShopCompanion.Infrastructure/Backend/HttpPrintShopBackend.cs`

- `MagicLinkPairingService.ValidateUriAsync` validates scheme `printshopcompanion://` + host `pair` + `token` query + `expires` (Unix seconds, rejects if past). Then calls `_backend.PairWithDeviceTokenAsync(token, ct)`. The comment at lines 80-87 admits this is "mostly obsolete" and kept "for backward compatibility" — **the URL `token` is forwarded to the backend as a device token**, not a short-lived pairing ticket.
- `IPrintShopBackend` has **no `PairAsync`** (magic-link) — only `PairWithDeviceTokenAsync(string deviceToken, ...)`. The production backend uses **paste-token**, not magic-link.
- `HttpPrintShopBackend.PairWithDeviceTokenAsync` writes the token directly with `ProtectedData.Protect` (line 110-112), **bypassing `DeviceTokenStore`** — duplicates encryption logic.
- `App.OnStartup` calls **neither** `MagicLinkPairingService` nor `UriActivator` — they are dead code from the App's perspective. The App checks for saved credentials by **file existence at the wrong path** (`%AppData%\PrintShop\Companion\device.dat` instead of the actual `DeviceTokenStore` path `%LocalAppData%\PrintShop\Companion\Security\device.dat`), so a user with only the new path lands on Login even with a valid token (§12).
- `SingleInstance` is **just a named mutex** — no named-pipe server, no URL forwarding. A second instance that launches while the first is running simply exits and **drops the URL**. README's "single-instance guard with named-pipe IPC for magic-link forwarding" is false.
- The `printshop://` URI scheme mentioned in the brief **does not exist** in this codebase. The scheme is `printshopcompanion://` (`CompanionProtocol.UrlScheme`, registered by the installer under `HKCU\Software\Classes\printshopcompanion`).

**Compiles:** syntactically clean. No `NotImplementedException`.

**Backend contract assumption baked in:** pairing is paste-token; the backend has no `/pair` endpoint and no ticket TTL (confirmed in `docs/CONTRACT_COMPATIBILITY.md`).

---

## 3. DPAPI / device token storage

**Files:** `src/PrintShopCompanion.Infrastructure/Storage/DeviceTokenStore.cs`, `QueueKeyStore.cs`, `src/PrintShopCompanion.Core/Contracts/CompanionProtocol.cs`

- `DeviceTokenStore` implements `ICredentialStore`. `LoadAsync` reads `%LocalAppData%\PrintShop\Companion\Security\device.dat`, `ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser)`, deserializes `StoredCredentials {ShopSession?, DeviceToken?}`. `SaveAsync` JSON-serializes → `ProtectedData.Protect` → writes. `ClearAsync` deletes. Constructor calls `MigrateDeviceToken()` to auto-migrate from the old `%AppData%\PrintShop\Companion\device.dat` path.
- `QueueKeyStore` generates a 32-byte SQLCipher key via `RandomNumberGenerator.GetBytes(32)`, DPAPI-encrypts at `Security/queue.key`, returns hex form `x'<64hex>'` for the SQLite `Password=` parameter. Per-installation; fail-closed if lost.
- `CompanionProtocol.DeviceTokenHeader = "X-PrintShop-Device-Token"` — **defined but unused** by the production client (`HttpPrintShopBackend.AddAuth` uses `Authorization: Bearer <token>` instead).
- `System.Security.Cryptography.ProtectedData` is **not explicitly referenced in any .csproj**. On `net10.0-windows` this may come transitively from the Windows Desktop SDK, but it's an unverified compile risk on a Windows box.
- `DeviceTokenStore.SaveAsync` is called **nowhere** in the production path — `HttpPrintShopBackend.PairWithDeviceTokenAsync` writes the token directly, bypassing the store.

**Compiles:** clean (modulo the `ProtectedData` package reference risk above).

---

## 4. Backend HTTP client — production

**File:** `src/PrintShopCompanion.Infrastructure/Backend/HttpPrintShopBackend.cs`

Implements `IPrintShopBackend`. Registered via `HttpClientFactory` in `InfrastructureModule`. Honors `Backend:BypassTlsVerification` (installs a no-op cert validator + logs a warning).

**Endpoints called (production):**

| Method | Path | Request body | Response DTO |
|---|---|---|---|
| POST | `{ApiBase}/agents/heartbeat` | `{printerName, version, os}` | `{agentId, online, ts}` — `tokenStillValid` not in response, hardcoded `true` locally |
| POST | `{ApiBase}/agents/token` | (empty) | `{accessToken, expiresIn, shopId}` — JWT exchange |
| GET | `{ApiBase}/agents/jobs/pending` | — | `{jobs: List<PrintJobDto>, agentId}` |
| POST | `{ApiBase}/agents/jobs/{jobId}/status` | `{status, printerName, lastError}` (`status ∈ {"printing","completed","failed"}`) | `{jobId, status, applied, skipped, reason}` |
| POST | `{ApiBase}/agents/jobs/{jobId}/received` | `{checksum (hex no prefix), fileSize, receivedAt, deliveryAttemptId?, transferId?}` | `{jobId, received, cleanupScheduled, cleanupScheduledAt?, message}` |
| GET | `{pdfUrl}` | — (no auth, presigned S3) | `application/pdf`, refuses >100 MB |
| GET | `{ApiBase}/sync/pull?cursor=&limit=` | — | `SyncChange[]` |
| POST | `{ApiBase}/sync/push` | `{changes: [...]}` | `{results: [...]}` |
| GET | `{ApiBase}/sync/status` | — | `{deviceId, shopId, syncCursor, lastSyncAt, lastPushAt, agentName, isOnline}` |

- `ApiBase = BaseUrl + /api/v1` (from `BackendOptions.ApiBasePath` default `/api/v1`).
- `appsettings.json` BaseUrl = `https://printshop-saas-three.vercel.app` (App) — matches the real SaaS production URL. Worker `appsettings.json` instead defaults to `https://mock.printshop.local` — **mismatch**.
- Auth header: `Authorization: Bearer <deviceToken>` + `User-Agent: PrintShopCompanion/<version>`. Does NOT send `X-PrintShop-Device-Id` or `X-PrintShop-Companion-Version` (both defined in `CompanionProtocol`, unused).
- Envelope: `ApiEnvelope<T> = {success, data, error: {code, message}, pagination}`.
- `MapToRecord` (line 544-563) leaves `ShopId = ""`, `DeviceId = ""`, `PdfSha256 = null` — never populated from backend response. **`PdfSha256` being null means SHA-256 verification is skipped for backend-delivered jobs** (only downloaded files get a hash).
- `RestoreAsync` (line 137-159) re-validates the token via heartbeat on every startup → if backend offline, user is bounced to Login even with a valid cached token.

**Compiles:** clean. These endpoints and the `Authorization: Bearer` scheme **match the real SaaS contract** (`docs/CONTRACT_COMPATIBILITY.md`).

---

## 5. Queue — SQLite/SQLCipher

**File:** `src/PrintShopCompanion.Infrastructure/Queue/SqliteJobQueue.cs`

- Schema `jobs` (line 42-90): `job_id TEXT PRIMARY KEY` + columns for shop/device/title/printer/copies/paper/color/duplex/pdf_url/local_pdf_path/pdf_sha256/status INTEGER/spooler_job_id/attempt_count/last_error/timestamps + v0.5 columns: `delivery_status, storage_status, checksum, file_size, received_at, cloud_delete_after, local_delete_after, delivery_attempt_count, last_delivery_error, original_filename, order_token, page_count`.
- **Idempotency:** `EnqueueSql` uses `INSERT ... ON CONFLICT(job_id) DO NOTHING`. If insert count is 0, reads existing row unchanged. ✓
- **States** (`PrintJobRecord.cs:10-52`): `Pending, Downloading, Rendering, Spooled, UncertainSpooled, Printed, ReportedPrinted, OutcomeUnknown, Failed, Cancelled` — all 10 implemented.
- **`DequeueAsync`** (line 539-600): selects only `Pending` OR (`Failed` AND `next_attempt_at <= now`), `ORDER BY queued_at LIMIT 1`. Bumps `attempt_count`, transitions Pending→Downloading in same transaction. Excludes Downloading/Rendering/Spooled (was a bug fix).
- **Backoff** (line 943-949): `Math.Min(Math.Pow(3, attempt) * 5, 600)` + 0-30% jitter → 5s, 15s, 45s, 135s, 405s, cap 600s.
- **Corrupt-DB quarantine** (line 481-494): `PRAGMA integrity_check` failure → rename to `queue.db.corrupt-<ts>`, create fresh.
- **Disk-space pre-check** (line 215-231): 256 MB min, throws if below.
- **SQLCipher** (line 144-172): **fail-closed** — if keyStore provided AND `SqlCipherInitializer.IsEncryptionAvailable`, sets `Password`; if keyStore provided but encryption unavailable, **throws**. Tests pass `keyStore: null` for plain SQLite (Linux-buildable).
- **v0.4.7→v0.5 migration** (line 316-414): detects old `%AppData%\PrintShop\Companion\queue.db`, renames to `.v047.bak`, copies rows into the new encrypted DB, writes `.migrated` sentinel. (The recent `Pooling=False` fix in `tests/MigrationTests.cs` resolved the 8 Windows CI test failures.)

**Bugs found in this audit:**
1. **`ReadRecord` (line 1002-1029) does NOT read the v0.5 columns** (`delivery_status, storage_status, checksum, file_size, received_at, cloud_delete_after, local_delete_after, delivery_attempt_count, last_delivery_error, color_mode, original_filename, order_token, page_count`). They are written on enqueue but lost on read across restarts.
2. **`WriteStatusAsync` (line 951-991) does not write v0.5 columns** either. So `color_mode`, `original_filename`, `order_token`, `page_count`, `delivery_status`, `storage_status`, `checksum`, `file_size`, `received_at`, `cloud_delete_after`, `local_delete_after`, `delivery_attempt_count`, `last_delivery_error` are **never persisted on UpdateAsync** — data-loss bug across restarts.
3. **`ReplayInFlightAsync` (line 780-806) resets `UncertainSpooled`/`OutcomeUnknown` → `Pending` on startup**, contradicting `docs/PHASES.md` ("UncertainSpooled never auto-reprints"). `JobProcessor.ProcessOneAsync` (line 127-131) does guard against processing them, but they stay Pending in the DB forever — dead-loop / inconsistent state.

**Compiles:** clean.

**Queue safety risks:** invariant #1 (persist before process) is satisfied architecturally (realtime handler enqueues before raising `JobDelivered`; `JobProcessor` only dequeues). Invariant #4 (idempotency on duplicate realtime delivery) is satisfied by `ON CONFLICT(job_id) DO NOTHING`. Invariant #5 (never retry uncertain physical print) is **violated** by `ReplayInFlightAsync` resetting `UncertainSpooled` (though `JobProcessor` won't act on it). The two `ReadRecord`/`WriteStatusAsync` bugs weaken invariant #7 (preserve queue data across restarts).

---

## 6. Job processor

**File:** `src/PrintShopCompanion.Infrastructure/Queue/JobProcessor.cs`

Single-consumer loop (`RunAsync` line 83-105), `SemaphoreSlim(1,1)`. `ProcessOneAsync` (line 107-299):
1. Dequeue. If null, delay `PollFallbackInterval` (2s).
2. Skip `Printed`/`ReportedPrinted`/`Cancelled`.
3. Skip `UncertainSpooled`/`OutcomeUnknown` (log + return — dead branch given `DequeueAsync` filter).
4. If `PdfUrl` null → permanent fail.
5. If `LocalPdfPath` missing → `DownloadPdfAsync(job.JobId, job.PdfUrl, job.PdfSha256, ct)` → set `Rendering` → `UpdateAsync` → `MarkJobReceivedAsync` (wrapped try/catch; comment "backend may not have the /received route yet").
6. **Atomic claim** (line 198-220): `ReportJobStatusAsync(job.JobId, "printing", printerName, null, ct)`. If `claim.Skipped` → cancel locally. If `!claim.Applied` → return without state change.
7. **Spooler submit** (line 224-238): `_printers.SubmitAsync(job, ct)` → set `Spooled`.
8. **Wait for spooler confirmation** (line 240-249): poll `_printers.HasPrintedAsync` every 2s up to `PrintConfirmTimeout` (5 min).
9. Not printed within timeout → `MarkUncertainAsync` + report `failed`.
10. Printed → `MarkPrintedAsync`, set `LocalDeleteAfter = printedAt + 60 min`. Report `completed` → `MarkReportedPrintedAsync`. If report fails → `MarkOutcomeUnknownAsync`.

**Dead code** (line 279): `var retentionMinutes = ... > 0 ? 60 : 60;` — both branches are 60; ternary is meaningless.

**Compiles:** clean.

**Stubbed:** the WPF App starts `JobProcessor` (App.xaml.cs:220) but **never starts the realtime client or the poller** (§7) — so in the WPF app, `JobProcessor` will never receive a job through the agent pipeline. Only the Worker starts `SocketIoRealtimeClient`.

---

## 7. Realtime client

**File:** `src/PrintShopCompanion.Infrastructure/Backend/SocketIoRealtimeClient.cs`

- `SocketIOClient` 3.1.2 (NuGet; csproj comment claims 3.2.0 — inaccurate). Transport `WebSocket`, `Path = "/"`, `Reconnection = true`, `ReconnectionAttempts = int.MaxValue`, `ReconnectionDelay = 2000ms`.
- URL `BuildUri` (line 175-189) prefers `BackendOptions.SocketIoUrl` (default `https://printshop-saas-three.vercel.app/?XTransformPort=3003`). The `?XTransformPort=3003` query is **required** — without it Caddy routes to the Next.js app on port 3000 which doesn't serve Socket.IO. **Matches the real SaaS contract.**
- Auth handshake (line 78-99, 191-208): pre-exchange JWT via `POST /agents/token`, then on connect emit `auth` with `{token: <jwt>}`. Listen for `auth-ok` (join shop room) / `auth-error` (refresh JWT, reconnect).
- **Events listened:** `auth-ok`, `auth-error`, `job.dispatch` (deserialize `PrintJobDto`, enqueue, raise `JobDelivered` — **no wire-level ACK**, claim is later via REST `status:"printing"`), `agent.revoked` (if `payload.AgentId == ThisApp.DeviceId`, raise `Revoked`), `sync.pushed` (logs only — "avoid hammering backend").
- `HttpFallbackPoller` (line 263-322): polls `FetchPendingJobsAsync` every 2s on disconnect.
- **Heartbeat: NOT driven anywhere in this codebase.** `BackendOptions.HeartbeatInterval` is documented in `docs/PROTOCOL.md` §3 but no timer calls `HeartbeatAsync` periodically. The only `HeartbeatAsync` call is in `HttpPrintShopBackend.PairWithDeviceTokenAsync` for token validation on startup.
- `SocketIoRealtimeClient` is registered in DI but **never started by the WPF App** — only by the Worker (`Worker.cs:115`). The WPF app therefore relies on... nothing. It does not poll either. The only way jobs reach the WPF app's queue is via `SyncEngine.PullUpdatesAsync` calling `_backend.ListOrdersAsync` (a shopkeeper endpoint, not the agent jobs endpoint). **The WPF app will never receive a print job through the agent pipeline as written.**

**Compiles:** clean.

**Backend contract assumptions baked in (all match the real SaaS):**
- URL `https://printshop-saas-three.vercel.app/?XTransformPort=3003`
- Path `/`, WebSocket transport
- Emit `auth {token: <jwt>}` — the only emit
- Listen `auth-ok`, `auth-error`, `job.dispatch`, `agent.revoked`, `sync.pushed`
- `job.dispatch` payload = `PrintJobDto` (same shape as REST)
- `agent.revoked` payload = `{agentId, agentName}`

**Drift:** `CompanionProtocol.cs` defines `HubMethodDeliverJob = "DeliverJob"`, `HubMethodAckJob = "AckJob"`, `HubMethodReportStatus = "ReportStatus"`, `HubMethodHeartbeat = "Heartbeat"` — **all dead**. `docs/PROTOCOL.md` §4.1 repeats those dead names. Three different naming conventions coexist (CompanionProtocol.cs vs SocketIoRealtimeClient.cs vs docs).

---

## 8. Printer discovery + capabilities

**File:** `src/PrintShopCompanion.Infrastructure/Printers/WindowsPrinterService.cs`

- `DiscoverPrinters` uses `System.Printing.LocalPrintServer.GetPrintQueues({Local, Connections})`. Per queue: `q.Refresh()`, `q.GetPrintCapabilities()`, builds `PrinterInfo {FullName, DisplayName, IsDefault, IsOffline, IsNetwork, SupportedPaperSizes, SupportedDuplexModes, SupportsColor, IsSelected}`. Per-printer exception catch.
- `GetSelectedPrinter` returns `PrinterOptions.SelectedPrinter` or Windows default.
- `SelectPrinterAsync` mutates `_options.CurrentValue.SelectedPrinter` in memory — **does not persist** (comment says "App via IOptionsMonitor + JSON writer" but `OptionsWriter.Update` is a stub).
- `SubmitAsync` calls `_renderer.PrintAsync` and tags the spooler ID.
- `HasPrintedAsync` (line 119-133): iterates all queues, `q.GetJob(spoolerJobId)`. If found → `JobStatus.HasFlag(Printed | Completed)`. **If NOT found in any queue → returns `true`** ("Spooler job not found; assuming completed and purged"). **False-positive risk**: a purged/failed job is assumed printed.
- `TestPrintAsync` calls `TestPdfGenerator.GenerateAsync` (hand-writes a minimal PDF with hardcoded xref offsets — fragile).

**Compiles:** clean (Windows-only; `System.Printing` requires `UseWPF=true`).

---

## 9. PDF renderer

**Files:** `CompositePdfRenderer.cs`, `SumatraPdfPrinter.cs`, `RawGdiPrinter.cs`

- `CompositePdfRenderer` picks primary from `PrinterOptions.Renderer` (default `"sumatra"`), walks `FallbackRenderers` (default `["gdi"]`). Calls each `PrintAsync` in order.
- `SumatraPdfPrinter` invokes `SumatraPDF.exe -silent -print-to "<printer>" -print-settings "..." -print-stat "<pdf>"`. Parses stdout for `SPOOLER_JOB_ID=<int>`. **If no SPOOLER_JOB_ID line is found, returns `Random.Shared.Next(1_000_000, int.MaxValue)`** — a synthetic spooler ID → `HasPrintedAsync` will never find it and immediately return `true` (false-positive confirmation).
- `RawGdiPrinter.PrintAsync` uses `System.Drawing.Printing.PrintDocument` with a `PrintPage` handler that draws **only a placeholder string** "PrintShop Companion (raw GDI fallback)" — **does NOT render the PDF**. Comment: "the actual production path is to render the PDF page-by-page." `SendRawBytes` (P/Invoke `winspool.drv`) is **never called** by `PrintAsync` — dead code.
- No PDFium renderer (removed v0.1.1).

**Compile risk on Windows:** `RawGdiPrinter` uses `System.Drawing.Printing.PrintDocument` (WindowsForms-only type) but Infrastructure csproj has `UseWindowsForms=false`. `UseWPF=true` may not bring `System.Drawing.Printing`. **Likely Windows-box compile risk** — to verify on the runner.

---

## 10. Windows spooler status tracking

Handled inside `WindowsPrinterService.HasPrintedAsync` (§8). Uses managed `PrintJobStatus` enum flags. `SpoolerJobId` stored on `PrintJobRecord` + `spooler_job_id` column. No raw `JOB_STATUS_*` constants. `RawGdiPrinter.FindSpoolerJobId` iterates `queue.GetPrintJobInfoCollection()` matching by `JobName`.

---

## 11. App startup + tray lifecycle

**File:** `src/PrintShopCompanion.App/App.xaml.cs`

`OnStartup` (line 42-304):
1. CLI args: `--register-autostart`, `--unregister-autostart`, `--version`.
2. Serilog rolling file at `%AppData%\PrintShop\Companion\logs\companion-.log`, 14-day retention. **No EventLog sink, no `DeviceTokenRedactor` enricher wired** despite docs claiming both.
3. SingleInstance mutex `PrintShop-Companion-Mutex-v4` (no pipe forwarding).
4. DI host loads `appsettings.json` (NOT optional) — **`appsettings.Local.json` not loaded in App** (only in Worker).
5. Checks saved credentials by **file existence at the wrong path** (`%AppData%\PrintShop\Companion\device.dat`) — `DeviceTokenStore` actually reads from `%LocalAppData%\...\Security\device.dat`. So a user with only the new path lands on Login even with a valid token.
6. `_host.StartAsync().Wait()` (sync wait).
7. Navigates to Overview or Login.
8. Background `_startupTask`: `HttpPrintShopBackend.RestoreAsync` → fallback `HttpShopBackend.RestoreSession` → `queue.ReplayInFlightAsync` → `JobProcessor.StartAsync` → `SyncEngine.Start` → `FileRetentionManager.Start`. **Does NOT start `SocketIoRealtimeClient`** — confirmed (no reference).
9. Tray `TrayIconHost.Show()`.
10. `AutoStartService.SyncFromOptions()` (shells out to PowerShell — see §13).
11. `_updateCheckTask`: `update.CheckForUpdatesAsync`, show banner.

`OnExit` (line 306-402): releases mutex, cancels `_appCts`, waits up to 5s for startup/update tasks, stops JobProcessor/SyncEngine/FileRetentionManager (2s each), stops+disposes host, hides tray. Final kill timer: `Process.GetCurrentProcess().Kill()` 3s after OnExit returns — "last line of defense".

`App.xaml`: `ShutdownMode="OnExplicitShutdown"` — closing window does NOT exit process. Merges 7 theme ResourceDictionaries.

**Tray** (`Tray/TrayIcon.cs`): `H.NotifyIcon.TaskbarIcon` context menu: Open Dashboard, Test print…, **Pause queue (empty — comment "Phase 6")**, Settings…, Quit. Double-click activates MainWindow.

**Stubbed:** `Pause queue` tray menu empty. `UriActivator` never called. `DeviceTokenRedactor` never wired. `appsettings.Local.json` not loaded in App. Wrong credential path check. **WPF App never starts realtime client or poller.**

**Compiles:** clean (modulo `MainWindow.ShowUpdateBanner` method — assumed exists).

---

## 12. Installer / URL protocol registration

**Files:** `installer/PrintShopCompanion.Installer.wxs` (WiX v5 MSI), `installer/PrintShopCompanion.Bundle.wxs` (bootstrapper), `installer/BuildInstaller.bat`

- `Installer.wxs` `Scope="perMachine"` — **contradicts docs which claim "per-user"** (`HONEST-STATUS` item 14, `SECURITY.md` line 14). Installs to `C:\Program Files\PrintShop\Companion\`. Three features: App (Level 1), Worker (Level 1 — installed by default despite "optional" comment), MockBackend (Level 2, optional).
- URL protocol (lines 102-115): writes `HKCU\Software\Classes\printshopcompanion` + `URL Protocol` value + `shell\open\command = "[INSTALLDIR]PrintShopCompanion.exe" "%1"`. Scheme is `printshopcompanion://` (not `printshop://`). Per-user (`HKCU`) — no admin needed for this part even though the package is per-machine.
- Worker service install (lines 117-131): writes `HKLM\SYSTEM\CurrentControlSet\Services\PrintShopCompanionWorker` registry keys directly. Comment: "Service install/uninstall is done via a deferred custom action (omitted here)." So the service is "installed" only by registry writes — no `sc.exe create`, no ServiceControl table. **Incomplete.**
- Custom action (line 55-58): `SetPerUserStartup` runs `[INSTALLDIR]PrintShopCompanion.exe --register-autostart` after `InstallFinalize`. `App.OnStartup` handles `--register-autostart` (line 49) creating the Startup folder shortcut via PowerShell (line 404-417).
- `Bundle.wxs` chains `NetDesktopRuntime_x64` (ExePackage, downloads `dotnet-runtime-10-win-x64.exe` from `download.visualstudio.microsoft.com`) + `CompanionMsi`. `WixStandardBootstrapperApplication.RtfLicense` with `LicenseFile=$(var.RepoRoot)\installer\redist\LICENSE.txt` (file exists).
- `installer/redist/` contents: only `LICENSE.txt`. **`SumatraPDF.exe` absent** (Installer.wxs line 97 references it). **`dotnet-runtime-10-win-x64.exe` absent**. **`app.ico`/`app.png` absent** (HONEST-STATUS item 17). **`PrintShopCompanion.exe.manifest` not produced** by the App csproj (comment line 9-11) but Installer.wxs line 87 references it — **build will fail at `wix build` time**.
- **MSI/Setup.exe in CI:** per `docs/BUILD_AND_RELEASE.md` §10 and confirmed in `build-windows.yml`, the workflow does NOT build MSI/Setup.exe — only the portable ZIP. The WiX sources remain but are not exercised by CI. **No code-signing step is present** (`PFX_B64`/`PFX_PWD` env vars are hoisted for `if:` conditions but no signing step exists).

---

## 13. Auto-update

**File:** `src/PrintShopCompanion.Infrastructure/Updates/GitHubUpdateService.cs` + tests

- Checks GitHub Releases API for the latest tag, compares semver (`IsNewer`), downloads the release asset, verifies SHA-256 against the digest in the GitHub release JSON. The 7 unit tests (semver comparison, hash verification, JSON parsing) all build on Linux and are real value.
- `InstallUpdateAsync` references `System.Windows.Application.Current.Dispatcher.Invoke` (line 327-330) — Windows-only (WPF); not exercised by the unit tests.
- `AutoStartService.CreateShortcut` shells out to PowerShell (the README's "no admin autostart" claim depends on this).
- **Update preservation:** DPAPI token + SQLCipher queue + logs all live under `%LocalAppData%\PrintShop\Companion\` and `%AppData%\PrintShop\Companion\logs\` — the portable ZIP distribution (no MSI) means the user extracts to a folder and never touches those AppData paths, so updates by re-extracting preserve credentials/queue/logs. (The MSI path would need a MajorUpgrade with `KeepUserSettings` — not currently wired.)

---

## 14. Tests + CI workflow

### Test projects (40 tests total; `docs/BUILD_AND_RELEASE.md` claim of "48" is inaccurate)

| Project | File | Tests | Linux-buildable? |
|---|---|---|---|
| Queue.Tests | `SqliteJobQueueTests.cs` | 7 | Body yes (keyStore: null); project TFM blocks build |
| Infrastructure.Tests | `MagicLinkPairingServiceTests.cs` | 5 | Body yes; project TFM blocks build |
| Infrastructure.Tests | `JobProcessorIdempotencyTests.cs` | 1 | Body yes (FakePrinter stubs throw NIE); project TFM blocks build |
| Infrastructure.Tests | `GitHubUpdateServiceTests.cs` | 7 | Body yes; project TFM blocks build |
| Infrastructure.Tests | `MigrationTests.cs` | 10 | Body yes (Pooling=False); project TFM blocks build |
| Infrastructure.Tests | `SqlitePendingOpsQueueTests.cs` | 10 | Body yes; project TFM blocks build |

**Windows-only tests:** none of the 40 test bodies directly invoke Windows printing APIs, registry, or DPAPI — all use `keyStore: null` + fakes. The `FakePrinter` throws `NotImplementedException` for `DiscoverPrinters`/`GetSelectedPrinter`/`SelectPrinterAsync`/`TestPrintAsync`. However, all test projects target `net10.0-windows` and reference the Infrastructure project (`UseWPF=true`), so the test assemblies inherit the Windows-only TFM and **cannot build on Linux even though the test bodies are OS-agnostic.**

### CI workflow `.github/workflows/build-windows.yml`

Two jobs:
- **build (windows-latest):** checkout, setup-dotnet 10.0.x, NuGet cache, set BUILD_VERSION from git tag, restore, build Release, **test (no `--collector` — was breaking pwsh parsing)**, publish `win-x64 --self-contained true -p:PublishSingleFile=false`, validate output (EXE + native DLLs + no `.env`/`.db`/`.pfx`/`.key`), 7-Zip to `PrintEXE-vX.Y.Z-win-x64.zip`, write `BUILD-INFO.txt` + `README.txt`, generate `SHA256SUMS.txt`, verify round-trip, upload `PrintEXE-win-x64` artifact, upload test-results.
- **release (ubuntu-latest, tag-only):** needs build, runs on `v*` tags or manual dispatch with `release-tag`. Checks release doesn't already exist (refuses overwrite), downloads artifact, re-verifies SHA-256 on Linux, creates GitHub Release via `softprops/action-gh-release@v2` with ZIP + checksums + metadata.

Triggers: push to main, PR to main, tag `v*`, `workflow_dispatch` with optional `release-tag`. Concurrency: `windows-build-${{ github.ref }}`, cancel-in-progress except tag pushes. The latest run `43946951958` on tag `v0.6.0` is fully green (20 build steps + 8 release steps) and produced release `PrintEXE-v0.6.0-win-x64.zip` with independently-verified checksum `4a7e0b86...`.

---

## 15. What compiles / does NOT compile / Windows-only / backend assumptions

### What compiles
Cannot be verified in this sandbox (`dotnet` absent). The code is syntactically careful. `HONEST-STATUS.md` states compile status is Unknown. **CI run `43946951958` (v0.6.0) built the solution successfully on `windows-latest`** — so the current `main` builds on Windows. The risk items below are flagged for the next Windows build.

### What does NOT compile on Linux
All 8 projects. Reason: `Directory.Build.props` forces `net10.0-windows`. Even with `dotnet` installed, `Microsoft.WindowsDesktop.App.WPF` / `.WindowsForms` frameworks are unavailable on Linux.

### Likely Windows-box compile risks (flag for the next CI run)
- `RawGdiPrinter.cs` uses `System.Drawing.Printing.PrintDocument` (WindowsForms) but Infrastructure csproj has `UseWindowsForms=false`. `UseWPF=true` may not bring `System.Drawing.Printing`.
- `HttpPrintShopBackend.cs` + `DeviceTokenStore.cs` use `System.Security.Cryptography.ProtectedData` with no explicit NuGet reference (may come transitively from the Windows Desktop SDK).
- `Installer.wxs` references `PrintShopCompanion.exe.manifest` (not produced), `SumatraPDF.exe` (absent from `installer/redist/`), `dotnet-runtime-10-win-x64.exe` (absent) — WiX build would fail, but CI doesn't build WiX today.

### Windows-only tests
None of the 40 test *bodies* invoke Windows APIs, but all test *projects* target `net10.0-windows`. No test exercises `WindowsPrinterService`, `SumatraPdfPrinter`, `RawGdiPrinter`, `DeviceTokenStore` (DPAPI), `QueueKeyStore` (DPAPI), the registry, or the tray. **There is no integration test against any HTTP server** — the mock backend is never started in CI.

### Backend contract assumptions found in code (exhaustive)
1. Base URL `https://printshop-saas-three.vercel.app` (App `appsettings.json` line 3) — **matches real SaaS.**
2. API path prefix `/api/v1` (production client) — **matches.** Mock backend uses `/api/companion/v1` — incompatible.
3. Auth `Authorization: Bearer <deviceToken>` (production) — **matches.** Mock backend uses `X-PrintShop-Device-Token` + `X-PrintShop-Device-Id` — incompatible. `CompanionProtocol.cs` defines the X-headers but the production client ignores them.
4. Envelope `ApiEnvelope<T> = {success, data, error: {code, message}, pagination}` — **matches.**
5. Heartbeat `POST /agents/heartbeat` body `{printerName, version, os}` response `{agentId, online, ts}` — **matches.** `TokenStillValid` not in response, hardcoded `true` locally.
6. JWT exchange `POST /agents/token` response `{accessToken, expiresIn, shopId}` — **matches.**
7. Pending jobs `GET /agents/jobs/pending` envelope `{jobs: List<PrintJobDto>, agentId}` — **matches.**
8. Job status `POST /agents/jobs/{jobId}/status` body `{status, printerName, lastError}` `status ∈ {"printing","completed","failed"}` response `{jobId, status, applied, skipped, reason}` — **matches.**
9. Receipt `POST /agents/jobs/{jobId}/received` body `{checksum (hex), fileSize, receivedAt, deliveryAttemptId?, transferId?}` response `{jobId, received, cleanupScheduled, ...}` — **matches.**
10. PDF download: presigned S3 URL, no auth, max 100 MB — **matches.**
11. Socket.IO URL `https://printshop-saas-three.vercel.app/?XTransformPort=3003` — **matches.**
12. Socket.IO path `/`, WebSocket transport — **matches.**
13. Socket.IO emit `auth {token: <jwt>}` (only emit) — **matches.**
14. Socket.IO listen `auth-ok`, `auth-error`, `job.dispatch`, `agent.revoked`, `sync.pushed` — **matches.**
15. `job.dispatch` payload = `PrintJobDto` — **matches.**
16. `agent.revoked` payload = `{agentId, agentName}` — **matches.**
17. No wire-level ACK on `job.dispatch` — claim is via REST — **matches.**
18. URL scheme `printshopcompanion://` host `pair` query `token` + `expires` — **does NOT match the real backend** (backend has no `/pair` endpoint and no ticket TTL; pairing is paste-token via `POST /agents/register` with owner JWT — see `docs/CONTRACT_COMPATIBILITY.md`).
19. Job status string values sent: `"printing"`, `"completed"`, `"failed"` — **matches.**
20. Sync endpoints `GET /sync/pull`, `POST /sync/push`, `GET /sync/status` — **matches.**
21. Shopkeeper endpoints (`HttpShopBackend`) — not companion-facing, not audited here.
22. `CompanionProtocol.cs` constants `HubMethodDeliverJob="DeliverJob"`, `HubMethodAckJob="AckJob"`, `HubMethodReportStatus="ReportStatus"`, `HubMethodHeartbeat="Heartbeat"` are **dead** — actual client uses string literals. Three different naming conventions coexist.
23. Mock backend (`PrintShopCompanion.Backend`) implements a **different contract** (`/api/companion/v1/*`, X-headers, `LegacyPrintJobDto`, `JobStatusReportDto`) — cannot interoperate with the production client.

---

## 16. Missing production integration (the work ahead)

1. **Pairing model conflict.** The brief wants a short-lived redeemable ticket flow; the backend implements paste-token (long-lived, no TTL, no consume). **Do not invent a ticket flow** — see `docs/CONTRACT_COMPATIBILITY.md`.
2. **Remote command channel conflict.** The brief wants RefreshPrinters/Pause/Resume/Cancel/TestPrint/Diagnostics commands from the cloud; the backend has **no command channel** at all. **Do not invent commands.**
3. **WPF App never receives jobs.** It starts `JobProcessor` but not `SocketIoRealtimeClient` or the poller. Must start the realtime client (or at minimum the poller) in `App.OnStartup`.
4. **Heartbeat never driven.** No timer calls `HeartbeatAsync` periodically. The 60-s heartbeat documented in `docs/PROTOCOL.md` §3 is not implemented.
5. **`App.OnStartup` checks the wrong credential path.** Should use `DeviceTokenStore` (or `ICredentialStore`) consistently.
6. **`ReadRecord`/`WriteStatusAsync` drop v0.5 columns.** Data-loss across restarts for `color_mode`, `original_filename`, `order_token`, `page_count`, `delivery_status`, `storage_status`, `checksum`, `file_size`, `received_at`, `cloud_delete_after`, `local_delete_after`, `delivery_attempt_count`, `last_delivery_error`.
7. **`ReplayInFlightAsync` resets `UncertainSpooled`/`OutcomeUnknown` to `Pending`.** Contradicts the "never auto-reprint uncertain" invariant.
8. **`HasPrintedAsync` false-positives.** A purged/failed spooler job is assumed printed (returns `true` if not found). Should distinguish "not found (purged after success)" from "not found (never spooled)".
9. **`SumatraPdfPrinter` synthetic spooler ID.** When `-print-stat` parsing fails, returns a random ID → guaranteed false-positive confirmation.
10. **`RawGdiPrinter.PrintAsync` does not render the PDF.** It prints a placeholder string. The GDI fallback is not a real fallback.
11. **`OptionsWriter.Update` is a stub.** `SelectPrinterAsync` mutates in-memory only; selected printer is lost on restart.
12. **`CompanionProtocol.cs` constants are dead/misleading.** Three naming conventions coexist. Reconcile to the real Socket.IO event names.
13. **`DeviceTokenRedactor` not wired** into the Serilog config.
14. **`appsettings.Local.json` not loaded in App** (only Worker).
15. **Mock backend incompatible with production client.** Two different contracts. Either align the mock or document it as local-only and not a contract reference.
16. **`Installer.wxs` will not build** (missing `.manifest`, `SumatraPDF.exe`, `dotnet-runtime-10-win-x64.exe`, `app.ico`). CI doesn't build it today; needs fixing if MSI is required.
17. **`httpPrintShopBackend.PairWithDeviceTokenAsync` bypasses `DeviceTokenStore`** — duplicates DPAPI logic.
18. **`PdfSha256` is always null for backend-delivered jobs** — SHA-256 verification skipped.
19. **`TokenStillValid` hardcoded `true`** — backend's heartbeat response doesn't carry it; revocation is only detected via 401 on the next authenticated call or the `agent.revoked` Socket.IO event.
20. **Worker `appsettings.json` defaults to `https://mock.printshop.local`** — mismatch with the App's real production URL.

Items 1-2 are contract conflicts (handled in `docs/CONTRACT_COMPATIBILITY.md`, not by inventing). Items 3-20 are real implementation gaps the upgrade plan addresses.

---

## 17. Honest summary

- The solution **builds green on Windows CI** (run `43946951958`, tag v0.6.0) and **40 tests pass** on the Windows runner.
- The production HTTP client (`HttpPrintShopBackend`) and the Socket.IO client (`SocketIoRealtimeClient`) **already match the real SaaS contract** for endpoints, auth scheme, envelope, event names, and DTO shapes. This is the single most important finding: the contract drift is mostly in the docs and the dead `CompanionProtocol.cs` constants, not in the live client code.
- The WPF App, however, **does not start the realtime client**, so it cannot receive print jobs through the agent pipeline as written. This is the most important functional gap.
- Two **contract conflicts with the brief** (pairing-ticket vs paste-token; remote-commands vs none) must be resolved by Agent 1 (SaaS owner) — not by inventing on the companion side.
- Several queue/data-loss bugs (`ReadRecord`/`WriteStatusAsync` drop v0.5 columns; `ReplayInFlightAsync` resets uncertain states) and false-positive risks (`HasPrintedAsync`, `SumatraPdfPrinter` synthetic ID) are real and need fixing.
- No claim of Windows runtime correctness, printer execution, or end-to-end pairing is made in this audit — those require a Windows machine + real backend, neither available in this sandbox.
