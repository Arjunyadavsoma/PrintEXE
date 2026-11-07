# Printing — Audit & Current Capabilities

## Architecture

The companion's print pipeline is composed of these layers:

1. **`JobProcessor`** (in `Infrastructure/Queue/JobProcessor.cs`) — the
   background loop. Pulls jobs from the SQLite queue, downloads the PDF,
   acks the job, submits to the printer, waits for spooler confirmation,
   reports the result back to the backend.
2. **`IJobQueue`** (in `Core/Contracts/IJobQueue.cs`) — SQLite-backed
   durable queue. Idempotent on `JobId`. Survives crashes.
3. **`IPrinterService`** (in `Core/Contracts/IPrinterService.cs`) —
   enumerates installed printers, submits print jobs, polls for spooler
   status.
4. **`IPdfRenderer`** (in `Core/Contracts/IServices.cs`) — renders a PDF
   to a printer.
5. **Implementations** (in `Infrastructure/Printers/`):
   - `WindowsPrinterService` — uses `System.Printing.PrintQueue` to enumerate
     printers and submit jobs. This is the primary interface.
   - `SumatraPdfPrinter` — primary renderer. Invokes the bundled
     `SumatraPDF.exe` with `-silent -print-to <printer> <pdf>`. SumatraPDF
     renders the PDF internally and submits to the spooler. Outputs
     `SPOOLER_JOB_ID=<int>` which we parse to track the spooler job.
   - `RawGdiPrinter` — fallback renderer for ESC/POS label printers. Uses
     `System.Drawing.Printing.PrintDocument` and writes raw bytes to
     `winspool.drv` via P/Invoke.
   - `CompositePdfRenderer` — chains SumatraPDF (primary) → RawGDI (fallback).

## Job lifecycle

```
Pending → Downloading → Rendering → Spooled → Printed → ReportedPrinted
                ↓           ↓           ↓
              Failed      Failed    UncertainSpooled
                                    (operator must reconcile)
```

- `Pending`: Job accepted, persisted to queue. Backend `queued`.
- `Downloading`: Companion is fetching the PDF bytes from the backend.
  Backend `downloading`.
- `Rendering`: Companion has the PDF on disk and is about to submit.
  Backend `rendering`.
- `Spooled`: Companion has submitted to the Windows print spooler.
  Backend `printing`.
- `UncertainSpooled`: Companion submitted to the spooler but cannot confirm
  physical print (e.g. crash between Submit and first status poll, or
  spooler job was purged before we observed `Printed`). **NEVER auto-reprint.**
  Operator must explicitly mark resolved.
- `Printed`: Spooler reports the document has physically printed.
  Backend `printed`.
- `ReportedPrinted`: Companion reported `Printed` to the backend and got 2xx.
- `OutcomeUnknown`: Companion reported `Printed` but never got 2xx. Heartbeat
  reconciles.
- `Failed`: Permanent failure; will not auto-retry. Backend `failed`.
- `Cancelled`: Operator cancelled.

## Spooler confirmation

After submitting to the spooler, `JobProcessor` polls the spooler job status
every 2 seconds up to `PrinterOptions.PrintConfirmTimeout` (default 30s).

If the spooler reports `PrintJobStatus.Printed | PrintJobStatus.Completed`,
the job is marked `Printed`.

If the timeout expires without confirmation, the job is marked
`UncertainSpooled`. **The companion never auto-reprints from this state.**
The operator must explicitly mark the job as resolved (after visually
verifying that the paper came out) via the PrintQueue screen.

## Idempotency

The queue is idempotent on `JobId`. If the backend redelivers a job that is
already in a terminal state (`Printed`, `ReportedPrinted`, `Cancelled`), the
`JobProcessor` acks it but does not reprint. This is the "never print twice"
guard.

`UncertainSpooled` and `OutcomeUnknown` are NOT terminal — they require
operator action. The `JobProcessor` skips them in the dequeue loop.

## Print options

Print options come from the backend's `PrintFile` Prisma model, not from the
companion's UI. The companion does NOT let the user pick paper size,
orientation, copies, or page range at print time — those decisions are made
upstream (by the customer or shopkeeper on the SaaS web app) and passed to
the companion via the `PrintJobRecord`.

The companion passes these options to SumatraPDF via its `-print-settings`
flag:
- `paper=<size>` (A4, A3, A5, Letter, Legal, custom)
- `color` / `monochrome`
- `duplexlong` / `duplexshort` / `simplex`
- `copies=<n>`

## Test print

The Printers screen has a "Test Print" button that calls
`PrintersViewModel.TestPrintAsync` → `IPrinterService.TestPrintAsync` →
`SumatraPdfPrinter.PrintAsync` with a built-in sample PDF. This is a
diagnostic tool to verify a printer is working, not a production print job.

## What is NOT supported in the companion

| Capability | Status | Reason |
| --- | --- | --- |
| Print preview | ❌ Not implemented | User can open the local PDF in the system default viewer (`Process.Start("explorer.exe", pdfPath)`) — but no in-app preview. |
| Per-print paper/orientation/copies UI | ❌ Not implemented | These come from the backend's `PrintFile` model. Adding a UI would conflict with the backend. |
| Print job cancellation | ❌ Not implemented | The companion's `JobProcessor` does not currently cancel a job mid-flight. Once it's submitted to the spooler, the only way to stop it is via the Windows print queue UI. |
| Page range selection | ❌ Not implemented | Comes from the backend. |
| ESC/POS label printer support | ⚠️ Implemented but untested | The `RawGdiPrinter` writes raw bytes to `winspool.drv`. The P/Invoke signatures are correct on paper but have not been tested on a real ESC/POS-capable label printer. |
| Multi-printer routing | ❌ Not implemented | The companion uses the shopkeeper-selected printer for all jobs. There's no rule-based routing (e.g. "A3 jobs go to printer X"). |

## Test plan (must be run on a Windows machine with a real printer)

| Test | Expected |
| --- | --- |
| Test Print on a laser printer | A test PDF prints; status banner shows "Test print sent to <printer>" |
| Trigger a print job from SaaS web | Companion auto-prints; queue shows "Printed" status |
| Trigger a print job while printer is offline | Spooler submission fails; queue marks job Failed; backend is notified |
| Trigger a print job while printer out of paper | Spooler holds job; companion waits up to PrintConfirmTimeout; if confirmed → Printed; if not → UncertainSpooled (operator reconciles) |
| Disconnect network mid-download | Download fails; queue marks job Failed; backend is notified |
| Restart app mid-print | Queue replays in-flight jobs on next start; idempotency prevents double-print |
| Trigger a job for a PDF that doesn't exist on the backend | Backend returns 404; queue marks job Failed |
| Trigger a job with a corrupt PDF | SumatraPDF fails to render; queue marks job Failed |
| Manually cancel a spooler job while companion is waiting | Spooler status returns Completed without Printed; companion marks UncertainSpooled |

## Files

- `src/PrintShopCompanion.Core/Contracts/IPrinterService.cs` — printer interface
- `src/PrintShopCompanion.Core/Contracts/IServices.cs` — `IPdfRenderer` interface
- `src/PrintShopCompanion.Infrastructure/Printers/WindowsPrinterService.cs` — `System.Printing` integration
- `src/PrintShopCompanion.Infrastructure/Printers/SumatraPdfPrinter.cs` — primary PDF renderer
- `src/PrintShopCompanion.Infrastructure/Printers/RawGdiPrinter.cs` — ESC/POS fallback
- `src/PrintShopCompanion.Infrastructure/Printers/CompositePdfRenderer.cs` — chain
- `src/PrintShopCompanion.Infrastructure/Queue/JobProcessor.cs` — orchestration
