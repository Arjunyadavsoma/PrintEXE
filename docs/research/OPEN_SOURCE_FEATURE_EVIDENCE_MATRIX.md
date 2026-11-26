# Open-Source Feature Evidence Matrix

Every row cites real source files in the local research clones
(`research/open-source-printing/`). Existing PrintEXE implementations were
audited in the repo at baseline `27e1898` (v0.7.16). Actions: **Reuse**
(adapt code, license-safe), **Adapt** (port the technique), **Rewrite**
(same engineering approach, fresh code), **Study** (behavior only), **Reject**.

## A. Printer discovery & status

| # | Feature | Reference | Source file | Class/method | PrintEXE today | Action |
|---|---|---|---|---|---|---|
| A1 | Registry-only enumeration that cannot stall on dead print servers | printpapi | `crates/agent/src/backend_windows.rs:229-278` | `EnumPrintersW` level 4, `PRINTER_ENUM_LOCAL\|CONNECTIONS`, 4× size retry | `WindowsPrinterService.cs` uses `System.Printing` (`PrintServer.GetPrintQueues`) | **Adapt** — add a winspool fallback path used when System.Printing throws/stalls |
| A2 | Capability discovery (paper/bins/duplex/color) | printpapi | `backend_windows.rs:33-50` | `DeviceCapabilitiesW`: `DC_PAPERNAMES`, `DC_BINNAMES`, `DC_DUPLEX`, `DC_COLORDEVICE` | Not surfaced (printer list shows names only) | **Adapt** — enrich printer records via `PrintQueue.GetPrintCapabilities()` (managed equivalent) |
| A3 | Printer status mapping (ready/busy/error/offline) | PrintHub | `src/PrintHub.Infrastructure/Backends/WindowsPrintBackend.cs:438-453` | `MapPrinterStatus` (WMI 3→Ready, 4/5→Busy, 6→Error, 7/WorkOffline→Offline) | Status dot shown green for all printers (no real status) | **Adapt** — map `PrintQueue.QueueStatus`/`IsOffline`/`IsPaused` into the existing status vocabulary |
| A4 | Spooler job/printer change NOTIFICATIONS (no polling) | qz-tray | `src/qz/printer/status/WmiPrinterStatusThread.java:103-134` | `FindFirstPrinterChangeNotification(PRINTER_CHANGE_JOB)` + `WaitForSingleObject` + decode | None — `SpoolerOutcomeTracker.cs` polls job status after submit | **Adapt** — add change-notification watcher to confirm spooler outcome events |
| A5 | Printer-set change watchdog (1 s EnumPrinters rescan → relaunch listeners) | qz-tray | `src/qz/printer/status/WmiPrinterConnectionsThread.java:19-31` | poll loop + thread relaunch | PrintersView refresh button only | **Adapt** — event-driven printer list refresh for the UI |
| A6 | Lazy/cached slow printer attributes | qz-tray | `src/qz/printer/info/NativePrinter.java:198-207`, `CachedPrintServiceLookup.java:13-15` | TTL cache + slow-attribute warm-up | Enumeration synchronous on UI demand | **Adapt** — warm capability cache in background after first list |

## B. Print engine

| # | Feature | Reference | Source file | Class/method | PrintEXE today | Action |
|---|---|---|---|---|---|---|
| B1 | PDFium render straight to printer HDC (no intermediate bitmaps) | print-it | `src/PrintIt.Core/Pdfium/NativeMethods.cs:151-187`, `PdfPage.cs:82-84` | `FPDF_RenderPage(hdc, …)` with `OptimizeForPrinting` | `CompositePdfRenderer.cs` renders via PDFtoImage then GDI-draws bitmaps | **Study** — keep current pipeline (works, avoids double maintenance); revisit only if perf issues measured |
| B2 | Auto-rotate + aspect-fit + center destination math | print-it | `PdfPage.cs:34-84` | `RenderTo` rect computation | Present in `CompositePdfRenderer` (equivalent math) | **Reject** (already implemented) |
| B3 | Page-range grammar (tokenizer/reducer/emitter, `"1,3-5,2-,-7"`) | print-it | `src/PrintIt.Core/Internal/PageRangeString/*` | Tokenizer + reducers | `RawGdiPrinter.cs` handles ranges from cloud settings | **Reject** (cloud drives ranges; no free-text input) |
| B4 | Thread-safe PDFium interop (single global lock, SafeHandles) | print-it | `NativeMethods.cs:13-21, 107-149` | interned `SyncRoot` + `SafeHandleZeroOrMinusOneIsInvalid` | PDFtoImage library internalizes this | **Reject** (dependency owns it) |
| B5 | RAW winspool printing with chunked writes | printpapi | `backend_windows.rs:178-227` | `OpenPrinter→StartDocPrinterW(pDatatype="RAW")→WritePrinter ≤1MB chunks→EndDoc` | `RawGdiPrinter.cs` implements GDI path; raw ESC/POS not a PrintEXE need | **Study** |
| B6 | External renderer multi-path discovery (exe dir/binaries/ProgramFiles/PATH) | PrintBridge | `agent/src/print/windows.rs:26-68` | SumatraPDF locator probe | `SumatraPdfPrinter.cs` locates bundled Sumatra | **Study** (we bundle; no need for PATH probing) |

## C. Cloud connection (Socket.IO agent)

| # | Feature | Reference | Source file | Class/method | PrintEXE today | Action |
|---|---|---|---|---|---|---|
| C1 | Exponential backoff + jitter + cap on reconnect | printpapi | `crates/agent/src/run.rs:90-135` | capped doubling (1,2,4…), tolerates non-JSON proxy pages | `SocketIoRealtimeClient.cs` — need to verify current policy (gap analysis G3) | **Adapt** — audit + harden to capped exponential + jitter |
| C2 | Heartbeat/ping keepalive | zma-printer (negative) | `src/main/cloud/connection.ts` (absent — no ping/pong) | — | Socket.IO has built-in ping/pong — verify enabled | **Verify** existing config; no code change expected |
| C3 | Dedup set of processed job ids | zma-printer (negative) | `connection.ts:168-189` (absent) | — | `SqliteJobQueue` enforces idempotent claim (cloud event id) | **Reject** (already implemented) — add test to prove it |
| C4 | Exactly-once printing via pre-print journal (fsync before report) | printpapi | `crates/agent/src/journal.rs:1-34`, `run.rs:516-585` | append JSONL, fsync, replay on re-offer | `SpoolerOutcomeTracker` + queue states; no journal-of-record before ack | **Adapt** — record durable "outcome intent" row before acking ReportedPrinted |
| C5 | Result outbox with never-discard doubling backoff (30 s→10 min) | printpapi | `run.rs:312-327, 429-470` | `Outbox` flush loop | `SqlitePendingOpsQueue` + `SyncEngine` (pending ops) | **Verify** — appears already designed in; confirm retry curve and no-discard guarantee |
| C6 | Connection state machine with dedup'd transitions to UI | zma-printer | `connection.ts:94-98`, `types.ts:3` | 5-state enum + change-detection | SyncCenterView has connection states | **Reject** (exists) |
| C7 | Poll errors sleep + log only on failing/recovered transition | printpapi | `run.rs:599-627` | transition-only logging | Serilog logs per failure | **Adapt** — reduce log noise on sustained outage |

## D. Queue engine

| # | Feature | Reference | Source file | Class/method | PrintEXE today | Action |
|---|---|---|---|---|---|---|
| D1 | Guarded state-machine transitions (refuse illegal moves) | PrintHub | `src/PrintHub.Core/Models/PrintJob.cs:116-185` | `TryMarkProcessing/Completed/Failed/Canceled` | `SqliteJobQueue` + `JobProcessor` — transitions exist; audit strictness | **Verify + Adapt** stricter guards where missing |
| D2 | Atomic single-statement claim (exactly one consumer) | printlatch | `src/db.rs:504-525` | `UPDATE … WHERE state='queued' RETURNING` | `SqliteJobQueue` claim path — verify CAS semantics | **Verify** — add concurrency test if absent |
| D3 | Crash recovery: `printing→unknown` (at-most-once on paper) | printlatch | `db.rs:171-181`, `main.rs:236-242` | restart sweep | `UncertainSpooled`/`OutcomeUnknown` states exist | **Reject** (already implemented — same philosophy) |
| D4 | Startup restore of interrupted jobs ordered by creation | PrintHub | `src/PrintHub.Api/Workers/PrintJobWorker.cs:61-99` | `RecoverJobsAndCollectDocumentsAsync` | `JobProcessor` recovery — verify ordering | **Verify** |
| D5 | Retry-as-new-job with ParentJobId lineage | PrintHub | `src/PrintHub.Core/Services/PrintJobService.cs:247-282` | manual retry creates child job | Reprint button re-enqueues — verify lineage | **Study** |
| D6 | Pausable queue (pause/drain/resume with TCS gate) | PrintHub | `src/PrintHub.Core/Queues/InMemoryPrintJobQueue.cs:25-138` | lock+semaphore+TCS | Not present (queue always runs) | **Study** — future feature |
| D7 | Additive schema migration (PRAGMA table_info + ALTER) | PrintHub | `SqlitePrintJobStore.cs:531-555` | `EnsureColumnAsync` | `SqliteJobQueue` has schema init — verify migration path | **Verify** |
| D8 | WAL + synchronous=FULL durability | printpapi | `crates/server/src/store.rs:554-568` | pragma set at open | `SqliteConnectionPragmas.cs` exists | **Reject** (already implemented) |

## E. Security

| # | Feature | Reference | Source file | Class/method | PrintEXE today | Action |
|---|---|---|---|---|---|---|
| E1 | DPAPI-at-rest with copy-detection (SHA-256 prefix) | printpapi | `crates/agent/src/keystore.rs:1-25` | machine-scope DPAPI + prefix | `DeviceTokenStore` uses DPAPI CurrentUser | **Reject** (exists; CurrentUser scope is correct for per-user agent) |
| E2 | PDF validation gauntlet (magic, EOF, active-content, bombs, caps) | printlatch | `src/pdf_guard.rs:35-126` | `validate_pdf` | Downloads verify SHA-256 of served bytes; no structural PDF validation | **Adapt** — add lightweight header/trailer/size checks to the download validator |
| E3 | Atomic temp+rename with server-generated names | printlatch | `src/api.rs:289-291, 660-665` | UUID names, `.tmp`→rename | `PersistDeviceTokenAsync` (fixed in v0.7.17); document pipeline paths | **Verify** document temp handling |
| E4 | Hashed tokens at rest (never plaintext) | printlatch | `src/auth.rs:318-320` | SHA-256 storage | Device token is DPAPI-encrypted at rest | **Reject** (equivalent strength for our model) |
| E5 | Log redaction (token masking) | zma-printer | `connection.ts:165` | `secret=***` | Log redaction implemented (baseline feature list) | **Reject** (exists) |

## F. Windows desktop integration

| # | Feature | Reference | Source file | Class/method | PrintEXE today | Action |
|---|---|---|---|---|---|---|
| F1 | Service self-restart via `ServiceBase.ExitCode` + `sc failure` | TapQueue | `src/TapQueue.Client.Windows/WindowsServiceRestarter.cs:8-21` | SCM restart contract | App is tray app, not service | **Study** (Worker service could adopt later) |
| F2 | Squat-proof named-pipe trust (verify pipe owner before sending) | TapQueue | `src/TapQueue.Client.Windows/UpdateCheckPipe.cs:152-203` | owner check + impersonation | Not applicable (no service↔tray pipe yet) | **Study** |
| F3 | Startup folder autostart without admin | PrintHub | `src/PrintHub.Infrastructure/Platform/PlatformAutoStartService.cs:53-79` | per-OS autostart file | Registry Run key implemented | **Reject** (exists) |
| F4 | Size-rolled file logger with retention | PrintHub | `src/PrintHub.Api/Logging/PrintHubFileLoggerProvider.cs:63-100+` | rolling writer | Serilog file sink with roll | **Reject** (exists) |

## G. UI/UX

| # | Feature | Reference | Source file | Class/method | PrintEXE today | Action |
|---|---|---|---|---|---|---|
| G1 | Diagnostics report with recommendations | PrintHub | `WindowsPrintBackend.cs:72-230` | `GetDiagnosticsAsync` (PowerShell found?, printer count, default, PDFtoPrinter, recommendations) | DiagnosticsView lists facts; recommendations absent | **Adapt** — add actionable recommendations to diagnostics |
| G2 | Support bundle zip download | PrintHub | `src/PrintHub.Api/Program.cs:365-372` | `/diagnostics/support-bundle` | Export button writes text report | **Study** — future feature |
| G3 | Test print from embedded base64 PDF | PrintHub | `Program.cs:744, 826-838` | test-print endpoint | TestPrint_Click exists (verify payload) | **Verify** |

## Priority ranking (scored 1-5: reliability, security, .NET-10 fit, Windows support, maintenance, license-safety, simplicity, user benefit)

| Rank | Item | Scores (R,S,F,W,M,L,X,U) | Total | Rationale |
|---|---|---|---|---|
| 1 | A3 printer status mapping | 5,3,5,5,5,5,5,5 | 38 | Real status kills the biggest UI lie (all-green); pure managed API |
| 2 | A4 spooler change notifications | 5,3,5,5,4,5,3,5 | 35 | Confirms outcomes without polling; P/Invoke contained in one class |
| 3 | C1 backoff+jitter+cap reconnect | 5,3,5,5,5,5,5,4 | 37 | Prevents reconnect storms; cheap to verify/harden |
| 4 | E2 PDF validation hardening | 4,5,5,5,5,5,4,3 | 36 | Defense-in-depth on untrusted cloud payloads |
| 5 | C4 pre-ack durable outcome journal | 5,4,5,5,4,5,3,4 | 35 | Closes the "spooler accepted but crash before ack" ambiguity permanently |
| 6 | A2 capability enrichment | 4,2,5,5,4,5,4,4 | 33 | Better defaults for paper/duplex per printer |
| 7 | A1 registry-only enumeration fallback | 5,2,5,5,4,5,3,3 | 32 | Robustness against dead print servers |
| 8 | A5/A6 printer-set watchdog + warm cache | 4,2,5,5,4,5,3,3 | 31 | UX freshness |
| 9 | G1 diagnostics recommendations | 3,2,5,5,5,5,4,4 | 33 | Support cost reducer |
| 10 | C7 transition-only logging | 3,2,5,5,5,5,5,2 | 32 | Log hygiene on sustained outage |

Items marked **Verify** in the matrix feed Stage 7 (gap analysis) before any
code is written — we do not integrate around features that already exist.
