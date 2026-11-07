# Architecture

## High level

```
┌─────────────────────────┐                ┌──────────────────────────────┐
│  PrintShop Website       │                │  PrintShop Companion (WPF)    │
│  ───────────────         │  magic-link    │  ─────────────────────────     │
│  · Order UI              │ ────────────▶  │  Setup / Dashboard / Printers  │
│  · "Add this PC" button   │  pairing URL  │  Print Queue / Settings        │
│  · Backend admin         │                │  Tray icon                    │
└─────────────────────────┘                └──────────────────────────────┘
            │                                              │
            │ HTTPS REST + Socket.IO                       │ in-process
            ▼                                              ▼
┌──────────────────────────────────────┐         ┌────────────────────────┐
│  PrintShop Backend                   │         │  IPrintShopBackend      │
│  ──────────────────                  │◀────────│  (HttpPrintShopBackend) │
│  · PrintAgent / PrintJob tables      │  HTTPS  │  + SocketIoRealtimeClient│
│  · device-token issuance             │         │  + HttpFallbackPoller  │
│  · PDF signed URLs                   │         └────────────────────────┘
└──────────────────────────────────────┘                    │
                                                            ▼
                                            ┌─────────────────────────────────┐
                                            │  IJobQueue (SqliteJobQueue)      │
                                            │  ────────────────────────────    │
                                            │  · Idempotent enqueue            │
                                            │  · In-flight replay on restart   │
                                            │  · Backoff for retryable failures│
                                            └─────────────────────────────────┘
                                                            │
                                                            ▼
                                            ┌─────────────────────────────────┐
                                            │  JobProcessor (loop)             │
                                            │  ─────────────────────────────   │
                                            │  · Dequeue → Download → Submit   │
                                            │  · Wait for spooler confirmation │
                                            │  · MarkPrinted → Report →        │
                                            │    MarkReportedPrinted           │
                                            └─────────────────────────────────┘
                                                            │
                                                            ▼
                                            ┌─────────────────────────────────┐
                                            │  IPrinterService                  │
                                            │  ─────────────────────────────   │
                                            │  · Discover installed printers  │
                                            │  · Submit to Windows spooler     │
                                            │  · Poll spooler for Printed      │
                                            │  · Test print                    │
                                            └─────────────────────────────────┘
                                                            │
                                                            ▼
                                            ┌─────────────────────────────────┐
                                            │  IPdfRenderer (composite)        │
                                            │  ─────────────────────────────   │
                                            │  · PDFium → SumatraPDF → GDI     │
                                            └─────────────────────────────────┘
                                                            │
                                                            ▼
                                            ┌─────────────────────────────────┐
                                            │  Windows Print Spooler          │
                                            │  → USB / Wi-Fi / Network printer │
                                            └─────────────────────────────────┘
```

## Process model

There are **two** runnable entry points that share the same engine:

| Entry point         | Binary                              | When to use                                |
| ------------------- | ----------------------------------- | ------------------------------------------ |
| WPF app             | `PrintShopCompanion.exe`            | Daily shopkeeper use; tray + window + UI.  |
| Worker service      | `PrintShopCompanion.Worker.exe`     | Kiosk / unattended deployments, no UI.     |

Both bring up the **same** `IPrintShopBackend` + `IJobQueue` + `JobProcessor` chain. The WPF app's `JobProcessor` runs in-process alongside the UI; the worker runs it as a hosted background service. They share the same SQLite queue file at `%AppData%\PrintShop\Companion\queue.db` — **but you should not run both at the same time** for the same shop, since two processors would race for the same pending jobs. The first delivery runs the WPF app only.

## Folder layout

See `README.md` at the solution root for the full directory listing.

## Single-instance IPC

The WPF app acquires a named mutex on startup. If a second instance is launched (typically because the OS routed a magic-link URL to it while the app was already running), the second instance writes the URL to a named pipe that the first instance is listening on, then exits. The first instance reads the URL off the pipe and routes it to `MagicLinkPairingService.HandleMagicLinkAsync`.

## Config flow

`appsettings.json` ships sensible defaults. A second `appsettings.Local.json` (gitignored) holds shop-specific overrides — the Settings screen writes to it on Save. The companion also reads `PRINTSHOPCOMPANION__*` environment variables.

## Logging

Serilog writes:
- Rolling daily files under `%AppData%\PrintShop\Companion\logs\companion-YYYYMMDD.log` (14-day retention).
- Windows Event Log under source `PrintShop Companion`.

A custom `DeviceTokenRedactor` enricher redacts any property whose name contains `Token`, `Secret`, or `Password` before the log event is written. The HTTP client also uses a logging handler that strips the `X-PrintShop-Device-Token` header from outbound logs.
