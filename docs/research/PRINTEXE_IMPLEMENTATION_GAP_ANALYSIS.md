# PrintEXE Implementation Gap Analysis

Date: 2026-11-26 · Baseline audited: `27e1898` (v0.7.16) on branch
`research/opensource-integration`. Every "existing implementation" claim
below cites our own source, verified this session.

## Baseline verification (what we already do well — do not replace)

| Feature | Our implementation | Evidence | Reference comparison |
|---|---|---|---|
| Idempotent enqueue (duplicate cloud events) | `INSERT` returning 0 rows → "Duplicate enqueue ignored", existing job returned | `src/PrintShopCompanion.Infrastructure/Queue/SqliteJobQueue.cs:622-627` | Stronger than zma-printer (none) — matches printpapi's idempotency-key goal |
| Crash recovery without double-print | `ReplayInFlightAsync` moves `Downloading→Pending`; deliberate design comment | `SqliteJobQueue.cs:651-691` | printlatch `printing→unknown` philosophy, already ours |
| Priority + due-time ordering | `ORDER BY priority DESC, queued_at ASC LIMIT 1` with failed-retry gate | `SqliteJobQueue.cs:665-671` | Equivalent to PrintHub worker loop |
| Claim serialization | process-wide `SemaphoreSlim(1,1)` gate around every queue op | `SqliteJobQueue.cs:39,570,638` | Equivalent outcome to printlatch atomic claim in our single-writer design |
| SHA-256 verified downloads + durable receipt before cloud delete | `DownloadPdfAsync(jobId, url, expectedSha)` then `MarkJobReceivedAsync` gates `cloudDeleteAfter` | `Queue/JobProcessor.cs:238-262` | Stronger than PrintHub (no hash) and PrintBridge (none) |
| Structural PDF inspection before print | `PdfDocumentInspector`: %PDF- header, page count, encryption | `Pdf/PdfDocumentInspector.cs:37-100`, called at `JobProcessor.cs:294` | printlatch gauntlet partially covered (no bomb budgets — see G-4) |
| Printer enumeration incl. offline flag + capabilities | `PrintServer.GetPrintQueues(Local\|Connections)`, `GetPrintCapabilities()`, `IsOffline` bit, default detection | `Printers/WindowsPrinterService.cs:42-69` | Matches printpapi A2 intent via managed API |
| SQLCipher + WAL pragmas | `SqliteConnectionPragmas.cs` | — | Matches printpapi durability stance |
| DPAPI token at rest | `ProtectedData.Protect` CurrentUser + round-trip verify + .bak | `Backend/HttpPrintShopBackend.cs:795-844` | printpapi uses machine-scope; ours is per-user (correct for tray app) |
| Log redaction | Baseline feature, verified in earlier audits | — | — |

## Confirmed gaps (ranked; source-verified)

### G-1 — Printer status is computed but never shown (UI lies)
`PrinterInfo.IsOffline` exists (`PrintShopCompanion.Core/Models/PrinterInfo.cs:20`)
and is populated (`WindowsPrinterService.cs:56`), but `PrintersView.xaml`
renders a **hardcoded green dot** for every printer
(`Views/PrintersView.xaml`, StatusDot `Background="{DynamicResource SuccessBrush}"`
bound to nothing). A shopkeeper with an offline printer sees green.
**Fix**: bind dot color to a status field; extend `PrinterInfo` with a
`Status` vocabulary (Ready/Busy/Error/Offline — PrintHub's mapping) fed by
`QueueStatus`/`IsOffline`/`IsPaused`.

### G-2 — Socket.IO reconnect is fixed-delay (storm risk)
`SocketIOOptions { ReconnectionDelay = 2000 }` with no jitter and no cap
(`Backend/SocketIoRealtimeClient.cs:70-78`). After a network blip, all
agents of all shops reconnect in lockstep every 2 s — the thundering-herd
pattern printpapi explicitly avoids with capped doubling + jitter
(`crates/agent/src/run.rs:90-135`).
**Fix**: exponential backoff with jitter and a ceiling (configurable).

### G-3 — Spooler outcome confirmed by polling, not events
`SpoolerOutcomeTracker` uses a poll-cycle observation model (class doc
`Printers/SpoolerOutcomeTracker.cs:9,32`). QZ Tray gets job/printer state
pushed via `FindFirstPrinterChangeNotification`
(`WmiPrinterStatusThread.java:103-134`), including
`PRINTER_ATTRIBUTE_WORK_OFFLINE` synthesis.
**Fix**: event-driven watcher alongside the poll (defense in depth; poll
remains the fallback).

### G-4 — Printer-set changes require manual refresh
No watcher on printer add/remove; the PrintersView "Refresh" button is the
only path (QZ Tray runs a 1 s connections watchdog,
`WmiPrinterConnectionsThread.java:19-31`).
**Fix**: `PrinterChangeNotification`-based add/remove event → UI refresh
(rides on G-3 watcher).

### G-5 — Diagnostics lists facts, gives no recommendations
DiagnosticsView enumerates health rows only. PrintHub's diagnostics returns
recommendations (`WindowsPrintBackend.cs:72-230`).
**Fix**: append actionable recommendations to the diagnostics report
(no Windows-specific hard dependency — computed from the facts we already
collect).

### G-6 (accepted, no action) — PDF bomb budgets
printlatch caps decompression size/megapixels (`pdf_guard.rs:89-117`).
Our PDFtoImage render would OOM rather than be exploited (single-tenant
device, cloud is the only document source, receipts trusted). Risk accepted
and documented; revisit if the platform ever accepts third-party uploads.

### G-7 (fixed this session) — Parallel-testhost file contention
`PersistDeviceTokenAsync` shared `.tmp` name raced between CI legs
(observed: 1/213 flake in v0.7.16 run 45904759397). Fixed with
process-unique staging name + retry (`HttpPrintShopBackend.cs:806-831`).
The sibling `ThisApp` race was fixed in v0.7.16.

## Architectural duplication check

None of the reference implementations overlap our cloud contracts
(Socket.IO auth-ok/job events, receipt/retry REST, pending-ops sync) — all
reference agents use different protocols (raw WS, long-poll, IPP), so there
is no risk of double-maintaining an equivalent component. The print stack
(System.Printing + PDFtoImage + Sumatra + GDI raw) remains single-sourced.

## What we deliberately did NOT port

- **IPP endpoint emulation** (TapQueue) — different product shape; our cloud
  pushes to a known agent.
- **Port monitor / virtual printer** (clawPDF) — we are a physical-print
  agent, not a document converter.
- **Thermal/ESC-POS/ZPL pipelines** (printpapi, zma) — not in the
  print-shop product scope today.
- **Hot-folder / file-based job intake** — cloud events only, by contract.
