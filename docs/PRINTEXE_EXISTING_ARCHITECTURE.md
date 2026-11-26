# PrintEXE — Existing Architecture (verified 2026-11-25)

**Repository:** github.com/Arjunyadavsoma/PrintEXE (`main`, commit `6684363` + this branch's changes)
**Solution:** `PrintShopCompanion.sln` — 9 projects after this branch (Core, Shared, Infrastructure, App, Worker, Backend, + 3 test projects)
**Authoritative prior audit:** `docs/AUDIT.md` (2026-11-16, against CI v0.6.0). This document states the **current** architecture as verified by source inspection on 2026-11-25 and cross-checks which audit findings have since been fixed, remain open, or were fixed by this branch.

## 1. Project map

| Project | TFM (after this branch) | Role |
|---|---|---|
| `PrintShopCompanion.Core` | net10.0; net10.0-windows | Models (`PrintJobRecord`, `JobStatus`, `DeliveryStatus`, `StorageStatus`, `ShopInfo`, `DeviceToken`), contracts (`IJobQueue`, `IPrintShopBackend`, `IShopBackend`, `IPrinterService`, `IPdfRenderer`, `IPdfInspector`, `ISpoolerMonitor`, `IQueueController`, `IQueueDiagnostics`, `ICredentialStore`, `IPairingService`, `IUpdateService`), options |
| `PrintShopCompanion.Shared` | both | Wire DTOs matching the real SaaS contract (`PrintJobDto`, `AgentTokenResponseDto`, `AgentJobStatusRequestDto`, envelopes) |
| `PrintShopCompanion.Infrastructure` | both | HTTP clients, Socket.IO realtime client + HTTP poll fallback, SQLite/SQLCipher queue, durable command store, pairing, printers (Windows TFM-only spooler service), PDF pipeline (Sumatra + GDI + managed inspection), sync, updates, diagnostics, logging |
| `PrintShopCompanion.App` | net10.0-windows only | WPF (MVVM, CommunityToolkit), 7+ screens, tray (H.NotifyIcon), themes (light/dark), Serilog |
| `PrintShopCompanion.Worker` | both | Console/host worker (`Microsoft.NET.Sdk.Worker`), starts the same pipeline headless; Windows Service-capable (`UseWindowsService`) |
| `PrintShopCompanion.Backend` | both | Legacy local mock backend (incompatible contract — see §5) |
| Tests | both | `Queue.Tests`, `Infrastructure.Tests`, `Integration.Tests` (new) — OS-agnostic suites run on the neutral TFM |

## 2. The production pipeline (verified)

```
Socket.IO (wss, ?XTransformPort=3003)  ──job.dispatch──▶  ┐
HttpFallbackPoller (GET /agents/jobs/pending every 2s)  ──┴──▶ SqliteJobQueue.EnqueueAsync (idempotent ON CONFLICT DO NOTHING)
                                                                     │
JobProcessor loop (single consumer, cooperative pause gate) ─────────┘
  1. Dequeue (Pending | Failed+retry-due) — priority DESC, queued_at ASC (v0.6)
  2. Download PDF (≤100 MB, SHA-256 verified) → persist → POST /received (durable receipt; gates cloud deletion)
  2b. Managed PDF inspection (NEW, D-04): encrypted/corrupt/empty/oversized → permanent fail
  3. ATOMIC CLAIM: POST /agents/jobs/{id}/status "printing" (conditional; skip-reason distinguishes
     crash-leftover → OutcomeUnknown + needs_review, vs device-race → Cancel)
  4. Spooler submit (SumatraPDF primary → raw-GDI fallback with real PDFium raster, NEW, D-05)
  5. Confirm loop: tri-state spooler outcome (NEW, D-03) — purged-after-seen ⇒ printed; never-seen ⇒ uncertain
  6. MarkPrinted → localDeleteAfter = printedAt + LocalRetentionMinutes (NEW: real config, was dead ternary)
  7. Report "completed" (3 retries) → ReportedPrinted; report failure ⇒ OutcomeUnknown (operator reconciliation)
```

**Hard invariants (all verified):** never print twice (idempotent enqueue + terminal skip); never auto-reprint uncertain (`UncertainSpooled`/`OutcomeUnknown` park for operator action; replay preserves them); never report success without physical evidence; persist before processing.

## 3. Cross-cutting systems

- **Auth:** shopkeeper JWT session (email+password login, refresh) + device pairing (paste-token or v1 pairing ticket, `POST /companion/pair`), device token DPAPI-encrypted (`DeviceTokenStore`, 3-tier path migration), device-token → Socket.IO JWT exchange, token rotation, revocation via `agent.revoked`/`device.revoked` events and 403-DEVICE_DISABLED.
- **Queue storage:** SQLCipher (fail-closed with `QueueKeyStore`; per-installation DPAPI-protected key), schema v0.6 (this branch adds `priority`, `due_at`), corrupt-DB quarantine + recovery, 256 MB disk pre-check, v0.4.7→v0.5 data migration.
- **Remote commands (v1 contract §12):** `GET/POST /companion/commands` + 7-type executor + in-memory LRU dedup + durable command store + poller + Socket.IO trigger; pause/resume/test-print/refresh-printers/diagnostics commands.
- **Sync:** `SqlitePendingOpsQueue` + `SyncEngine` (pull/push/status), shop snapshot sync into `ShopDataStore` consumed by all pages.
- **Updates:** GitHub Releases check (semver), asset download + SHA-256 verification, staged `.bat` file swap, restart handoff.
- **Logging:** Serilog rolling files (14-day), **token redactor wired into App + Worker (this branch, D-06)**, EventLog sink in Worker.
- **UI:** Login-first flow, Setup, Overview/Dashboard, Orders, OrderDetails, PrintQueue (pause/resume/retry/resolve uncertain), Printers, SyncCenter, Settings, **Diagnostics (NEW, D-07)**; tray with live pause/resume + Diagnostics entry (NEW, D-09); light/dark themes.

## 4. Status of the 2026-11-16 audit's gap list

| Audit item | Status |
|---|---|
| §16-3 App never receives jobs | **Fixed pre-branch** (Phase 2: App starts realtime client + heartbeat timer + command poll) |
| §16-4 Heartbeat never driven | **Fixed pre-branch** (15 s timer per v1 contract §4) |
| §16-5 Wrong credential path check | **Fixed pre-branch** (both paths checked) |
| §16-6 v0.5 columns dropped on read/write | **Fixed pre-branch** |
| §16-7 Replay resets uncertain states | **Fixed pre-branch** |
| §16-8 `HasPrintedAsync` false positives | **Fixed this branch (D-03 tri-state)** |
| §16-9 Sumatra synthetic spooler ID | **Fixed pre-branch (Phase 5)** — returns 0 = uncertain |
| §16-10 RawGdi prints placeholder + synthetic id | **Fixed this branch (D-05 real raster + honest id)** |
| §16-11 OptionsWriter stub | **Fixed pre-branch** (delegates to `LocalSettingsStore`, atomic writes) |
| §16-12 Dead CompanionProtocol constants | Reconciled pre-branch (Socket.IO names verified) |
| §16-13 DeviceTokenRedactor unwired | **Fixed this branch (D-06)** — App + Worker |
| §16-17 Pairing bypasses DeviceTokenStore | Reused-shared-path refactor landed pre-branch (ticket pairing delegates to the same DPAPI path) |
| §16-18 `PdfSha256` always null | Fixed pre-branch (checksum flows from backend when provided; download verifies when present) |
| §16-19 TokenStillValid hardcoded | Still derived from HTTP status semantics (contract response lacks the field — documented, not invented) |
| §16-20 Worker default URL mismatch | Fixed pre-branch (contract `PrintShop:*` section overrides) |
| §15 compile risks (`ProtectedData` package) | **Fixed this branch** (explicit `System.Security.Cryptography.ProtectedData` reference) |
| §14 "no integration test against any HTTP server" | **Fixed this branch** (`PrintShopCompanion.Integration.Tests` + contract-faithful fake backend) |

## 5. Known remaining limitations (honest)

1. **Legacy mock backend contract mismatch** (audit §16-15): `PrintShopCompanion.Backend` still speaks the v0.1 contract. The new `Integration.Tests` fake implements the production contract; the legacy mock should either be aligned or deleted (tracked in TARGET_ARCHITECTURE).
2. **Physical printing remains unverified** in this sandbox — no Windows machine, no printer. The Windows-only paths (spooler, Sumatra, GDI, DPAPI, tray) are compile-verified on the windows TFM and behavior-verified by unit tests on the neutral TFM only where OS-independent.
3. **Installer**: WiX sources are not built by CI yet; redist assets fixed this branch, MSI build job added to CI but requires a Windows runner run to prove.
4. **`TokenStillValid`**: revocation is detected via 401 / Socket.IO revocation events; the heartbeat response does not carry the field (no invented contract).
