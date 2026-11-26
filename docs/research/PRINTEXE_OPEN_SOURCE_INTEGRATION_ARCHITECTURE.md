# PrintEXE Open-Source Integration Architecture

Date: 2026-11-26 · Governs the v0.7.17 integration work. Design rule:
**technique-level adaptation, zero new dependencies, zero contract changes.**

## Component decisions

| Component | Decision | Source of technique | Notes |
|---|---|---|---|
| Printer status vocabulary + UI binding | **Adapt** | PrintHub `MapPrinterStatus` (MIT) | New `PrinterStatus` enum in Core; `WindowsPrinterService` maps `QueueStatus`/`IsOffline`/`IsPaused`; PrintersView binds dot+pill to it |
| Spooler change-notification watcher | **Rewrite (fresh C#)** | qz-tray `WmiPrinterStatusThread` behavior (LGPL — API pattern only, from MS docs) | New `Printers/SpoolerChangeWatcher.cs`: P/Invoke `FindFirstPrinterChangeNotification` on a background thread; raises .NET events for job-completed / printer-status-changed / printer-set-changed |
| Socket.IO reconnect policy | **Adapt** | printpapi backoff (Elastic — standard technique) | `SocketIOOptions.ReconnectionDelayMax` + jitter via `ReconnectionDelay` hook on the client wrapper; 2 s base, 30 s cap, full jitter |
| Printer-set change → UI refresh | **Rewrite** | qz-tray connections watchdog behavior | Rides the SpoolerChangeWatcher's `PrintersChanged` event; PrintersView/ViewModel subscribe (WPF dispatcher marshaling) |
| Diagnostics recommendations | **Adapt** | PrintHub `GetDiagnosticsAsync` (MIT) | Pure-C# computed advice appended to `DiagnosticsSnapshot` (Core model) — e.g. "no default printer set", "selected printer offline", "spooler restart suggested if jobs stuck" |
| PDF bomb budgets | **Study only** | printlatch `pdf_guard` (Apache) | Not integrated (gap analysis G-6); documented acceptance |
| Ack outbox | **Verify only** | printpapi outbox | Already implemented as `SqlitePendingOpsQueue` + `SyncEngine` with backoff ticks (15/30 s, max attempts) — no change |

## Interfaces preserved (non-negotiable)

1. Printelyx cloud contracts: Socket.IO event names/payloads
   (`auth-ok`, job delivery, receipts), REST endpoints (pair, heartbeat,
   download, mark-received, report-printed, pending-ops) — **untouched**.
2. `IPrinterService`, `IJobQueue`, `IPdfInspector`, `IPrintShopBackend`
   interfaces — new capability is added *behind* them (additive members
   only, default implementations where practical to avoid breaking fakes).
3. SQLite schema — additive columns only (`printer_status` on any
   persistence if needed; none required by design).
4. The Worker project and installer layout — unchanged.

## New code map

```
src/PrintShopCompanion.Core/Models/PrinterStatus.cs        (enum: Unknown/Ready/Busy/Error/Offline)
src/PrintShopCompanion.Core/Models/PrinterInfo.cs          (+ Status, + StatusText additive)
src/PrintShopCompanion.Infrastructure/
  Printers/WindowsPrinterService.cs                        (status mapping into existing DiscoverPrinters)
  Printers/SpoolerChangeWatcher.cs                         (new: P/Invoke change notifications → events)
  Backend/SocketIoRealtimeClient.cs                        (backoff+ jitter options)
  Diagnostics/…                                            (recommendation strings into snapshot)
src/PrintShopCompanion.App/Views/PrintersView.xaml         (bind status; event-refresh)
src/PrintShopCompanion.App/Views/PrintersView.xaml.cs      (subscribe watcher, dispatcher refresh)
tests/…                                                    (new test classes; see testing approach)
```

## Security boundaries

- Watcher runs read-only against the spooler (`PRINTER_CHANGE_JOB` +
  printer-status fields); it never calls `SetJob`/`WritePrinter`.
- No new file paths, no new network endpoints, no new secrets.
- P/Invoke surface limited to `winspool.drv` notifications, all
  documented Microsoft signatures; handles released on dispose.
- All watcher state stays in memory; no persistence changes.

## Testing approach

1. **Unit (simulated, honestly labeled)**: status mapping table tests
   (offline/paused/error bit combos → expected vocabulary); backoff
   calculator properties (monotonic cap, jitter bounds, average preserved);
   diagnostics recommendation rules (fact fixture → expected advice list).
2. **Existing suites**: full run must stay green (Infrastructure 213,
   Integration 12, Queue 38) — proves no contract regression.
3. **CI Windows leg**: compiles the P/Invoke surface and runs the app
   smoke test; real spooler events are exercised on real Windows hardware
   only — the tests label themselves simulated where they fake the
   notification source.
4. **Not claimed**: physical printing verification (no printer in this
   environment).

## Rollout

Single branch `research/opensource-integration` → PR → CI both TFMs →
merge to main → tag `v0.7.17` (rides the established build → ZIP → MSI →
Release pipeline proven in v0.7.14). CHANGELOG records each adapted
technique with its origin; THIRD-PARTY-NOTICES.md gains an inspiration
section (no code copied, no license obligations triggered — see the
license audit).
