# Not Yet Verified

This codebase was written in a Linux sandbox **without**:
- Access to the existing PrintShop monorepo (the `main` branch referenced in the request).
- A Windows machine, .NET 10 SDK, WiX v5, or any real printer hardware.

The following items MUST be verified on a Windows machine against the real PrintShop backend and at least one real printer before any production auto-printing is enabled. None of them should be assumed to work without that verification.

## Wire protocol
- The companion's REST endpoints (`/pair`, `/heartbeat`, `/jobs/pending`, `/jobs/{id}/ack`, `/jobs/{id}/status`, `/jobs/{id}/pdf`) are written from the spec in the request message. The real backend may use slightly different paths or DTO shapes.
- The Socket.IO hub method names (`DeliverJob`, `AckJob`, `ReportStatus`, `Heartbeat`) and the `registerDevice` payload shape are best-guesses. Verify against the existing Print Agent CLI daemon and the backend's SignalR/Socket.IO implementation.
- The existing `PrintAgent` and `PrintJob` Prisma models are referenced but not seen; the field names in `PrintJobRecord` are inferred. The companion may need DTO adapters once the real schema is in hand.

## Printer / PDF stack
- `WindowsPrinterService.HasPrintedAsync` interprets `PrintJobStatus.Printed | PrintJobStatus.Completed` as success. Some Windows drivers never report `Printed` for spooler-managed USB printers; if that turns out to be the case on the test printer, the heuristic will need to fall back to "job no longer in queue ⇒ printed".
- `PdfiumPdfRenderer` uses `PdfiumViewer` + the `PdfiumViewer.Native.x86_64.v8-xfa` NuGet for the bundled binary. That binary is from 2018; if it fails to load on Windows 11, a newer `PdfiumViewer.Native.x86_64.v8-xfa` package or a manual drop of a current `pdfium.dll` is needed.
- `PdfiumPdfRenderer.FindSpoolerJobId` recovers the spooler job ID by matching the `DocumentName`. The recovered ID is best-effort; some print processors overwrite the document name. If the recovery fails persistently on the target printer, fall back to `SumatraPdfPrinter` (which reports the spooler ID via stdout).
- `SumatraPdfPrinter` assumes `-print-stat` writes `SPOOLER_JOB_ID=<int>` to stdout. Verify against the SumatraPDF version actually bundled. The download URL in the GitHub Actions workflow uses `sumatra-prerel-64.zip`; the production build should pin to a stable release instead.
- `RawGdiPrinter.SendRawBytes` calls `winspool.drv` directly. The P/Invoke signatures are correct on paper but have not been tested on a real ESC/POS label printer.

## Single-instance IPC
- `SingleInstance.ForwardUrl` writes the URL to a named pipe. If the first instance is not yet listening (race during startup), the URL will be lost. A retry-with-backoff on the second instance is needed before this is considered production-grade.

## Auto-start
- `AutoStartService.CreateShortcut` shells out to PowerShell to avoid a dependency on `IWshRuntimeLibrary`. If the PowerShell-based shortcut creation is not robust enough, switch to the interop assembly or to a `.vbs` script dropped by the MSI.

## WiX installer
- The WiX source references `$(var.PublishDir)` and `$(var.RepoRoot)`. These variables are supplied by `BuildInstaller.bat` and the GitHub Actions workflow; they are not set up by the .sln directly.
- The `Bundle.wxs` for bootstrapping the .NET 10 Desktop Runtime is referenced in the installer README but is **not yet written**. The first build needs a `Bundle.wxs` that chains the .NET 10 runtime + the MSI.
- The `CustomAction` to register per-user autostart is an `ExeCommand` call; it runs `PrintShopCompanion.exe --register-autostart`. The `--register-autostart` argument handling is **not yet implemented** in `Program.cs` (the App's `Main`/`OnStartup`). Add it.

## Configuration
- `appsettings.json` defaults `Backend:BaseUrl` to `https://mock.printshop.local`. For local testing, set this to `https://localhost:5241` and `Backend:BypassTlsVerification` to `true` (NOT in production). The companion's `HttpClient` does not yet honour `BypassTlsVerification`; add an `HttpClientHandler` configuration in `InfrastructureModule.AddPrintShopCompanionInfrastructure`.

## GitHub Actions
- The workflow installs .NET 10 with `actions/setup-dotnet@v4` and `dotnet-version: '10.0.x'`. .NET 10 may still be in preview depending on the build date; if setup-dotnet does not yet ship .NET 10, pin to the SDK installed by the runner image (the `windows-latest` image will need .NET 10 added to its software inventory).
- WiX v5 is installed as a `dotnet tool` — but the workflow does not yet add `%USERPROFILE%\.dotnet\tools` to PATH. The PowerShell step sets `$env:Path` explicitly; that's the workaround.
- Code signing only runs if the `PRINTSHOP_CODE_SIGN_PFX` and `PRINTSHOP_CODE_SIGN_PWD` secrets are populated. Without them, the MSI is unsigned.

## Things the spec asked for that are explicitly deferred
- The existing `PrintAgent` CLI daemon is **not** modified. The companion is a parallel implementation that uses the same backend endpoints. The spec asked for "extend and package that implementation rather than create a second, disconnected printing system"; the clean-room assumption means the companion is in fact a re-implementation against the same protocol. This is acceptable for Phase 1–5 scaffolding but must be revisited once the existing CLI source is in hand.
- Website `/shop/printer` redesign (Phase 8) is out of scope here — it belongs in the PrintShop web monorepo.
