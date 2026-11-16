# Not Yet Verified

> **⚠ UPDATED 2026-11-16** after the full SaaS backend inspection. Several items
> below that were previously listed as "best-guess" / "not seen" are now **verified
> or corrected** against the real SaaS source (`Arjunyadavsoma/printshop-saas`).
> See `docs/CONTRACT_COMPATIBILITY.md` for the full reconciliation. Items still
> genuinely unverified are marked **[STILL UNVERIFIED]**.

This codebase was written in a Linux sandbox **without**:
- Access to the existing PrintShop monorepo (the `main` branch referenced in the request).
- A Windows machine, .NET 10 SDK, WiX v5, or any real printer hardware.

The following items MUST be verified on a Windows machine against the real PrintShop backend and at least one real printer before any production auto-printing is enabled. None of them should be assumed to work without that verification.

## Wire protocol

**[VERIFIED 2026-11-16]** The companion's live production client (`HttpPrintShopBackend`,
`SocketIoRealtimeClient`) was reconciled against the real SaaS route handlers and Socket.IO
mini-service. Findings:
- **[VERIFIED]** REST endpoints: `POST /api/v1/agents/heartbeat`, `POST /api/v1/agents/token`,
  `GET /api/v1/agents/jobs/pending`, `POST /api/v1/agents/jobs/{id}/status`,
  `POST /api/v1/agents/jobs/{id}/received`, `GET /api/v1/sync/{pull,push,status}`. The old
  `/pair`, `/jobs/{id}/ack`, `/jobs/{id}/pdf` paths from the spec are **NOT** in the real
  backend. Pairing is paste-token via `POST /api/v1/agents/register` (owner JWT, web-only —
  not called by PrintEXE). See `docs/PROTOCOL.md` §2.
- **[VERIFIED]** Socket.IO event names: the real backend's single inbound handler is `auth`
  with `{token: <jwt>}`. Server→client events: `auth-ok`, `auth-error`, `job.dispatch`,
  `job.printing`, `job.completed`, `job.failed`, `job.needs_review`, `order.ready`,
  `order.paid`, `order.collected`, `agent.revoked`, `agent.heartbeat`, `delivery.received`,
  `sync.pushed`. The old `DeliverJob`/`AckJob`/`ReportStatus`/`Heartbeat`/`registerDevice`
  names were wrong and are now `[Obsolete]` in `CompanionProtocol.cs`. See `docs/PROTOCOL.md` §4.1.
- **[VERIFIED]** The `PrintAgent` and `PrintJob` Prisma models **do exist** in
  `prisma/schema.prisma`. `PrintAgent {id, shopId, name, tokenHash, isOnline, lastSeenAt,
  printerName, version, os, createdAt, updatedAt}`. `PrintJob {id, shopId, fileId, status,
  lastError, startedAt, printedAt}`. The companion's `PrintJobRecord` field names match the
  backend's `PendingJob` DTO field-for-field. No DTO adapters needed.
- **[STILL UNVERIFIED]** No end-to-end pairing, heartbeat, job dispatch, or status report
  has been exercised against the live `https://printshop-saas-three.vercel.app` backend
  from a Windows machine. The contract is verified by source inspection only.

**[NEW OPEN CONFLICTS]** (see `docs/CONTRACT_COMPATIBILITY.md` §3):
- The brief's §4 pairing-**ticket** flow (short-lived, redeemable, atomic consume) is NOT
  implemented by the backend. The backend does paste-token. Change request for Agent 1.
- The brief's §8 remote-**command** channel (RefreshPrinters/Pause/Resume/Cancel/TestPrint/
  Diagnostics) is NOT implemented by the backend. No command channel exists. Change request
  for Agent 1.

## Printer / PDF stack
- **[STILL UNVERIFIED]** `WindowsPrinterService.HasPrintedAsync` interprets `PrintJobStatus.Printed | PrintJobStatus.Completed` as success. Some Windows drivers never report `Printed` for spooler-managed USB printers; if that turns out to be the case on the test printer, the heuristic will need to fall back to "job no longer in queue ⇒ printed". **Audit note (docs/AUDIT.md §8):** the current code returns `true` when a spooler job is NOT found in any queue ("assuming completed and purged") — this is a false-positive risk and must be revisited.
- **[STILL UNVERIFIED]** `SumatraPdfPrinter` (the **primary** renderer as of v0.1.1) assumes `-print-stat` writes `SPOOLER_JOB_ID=<int>` to stdout. Verify against the SumatraPDF version actually bundled. **Audit note (docs/AUDIT.md §9):** when `-print-stat` parsing fails, the code returns a *synthetic random* spooler ID → `HasPrintedAsync` will never find it and immediately return `true` (guaranteed false-positive). This must be fixed.
- **[STILL UNVERIFIED]** `RawGdiPrinter.SendRawBytes` calls `winspool.drv` directly. The P/Invoke signatures are correct on paper but have not been tested on a real ESC/POS label printer. **Audit note (docs/AUDIT.md §9):** `RawGdiPrinter.PrintAsync` currently prints a placeholder string, NOT the PDF content. The GDI fallback is not a real PDF renderer.
- **Removed (v0.1.1):** `PdfiumPdfRenderer` and the `PdfiumViewer` / `PdfiumViewer.Native.x86_64.v8-xfa` NuGet dependencies. Reason: NU1102 (native binary version 2018.4.12.1858 not published), NU1701 (PdfiumViewer targets .NET Framework), NU1903 (vulnerable transitive deps: Microsoft.NETCore.App 1.1.0, Microsoft.NETCore.Jit 1.1.0, Newtonsoft.Json 9.0.1, SQLitePCLRaw.lib.e_sqlite3 2.1.11). SumatraPDF provides the same PDF printing capability without loading a native renderer into the companion process.

## Single-instance IPC
- **[CORRECTED]** `SingleInstance` does NOT use a named pipe. It is a named mutex only (no IPC, no URL forwarding). If a second instance launches while the first is running, it exits and the URL is dropped. The README's "named-pipe IPC" claim is inaccurate. See `docs/AUDIT.md` §2.
- **[STILL UNVERIFIED]** A real URL-forwarding mechanism (named pipe or WPF `IUriActivation`) is needed for the brief's §4 magic-link experience. This is contract-independent but depends on conflict #1 being resolved first.

## Auto-start
- **[STILL UNVERIFIED]** `AutoStartService.CreateShortcut` shells out to PowerShell to avoid a dependency on `IWshRuntimeLibrary`. If the PowerShell-based shortcut creation is not robust enough, switch to the interop assembly or to a `.vbs` script dropped by the MSI.

## WiX installer
- **[STILL UNVERIFIED]** The WiX source references `$(var.PublishDir)` and `$(var.RepoRoot)`. These variables are supplied by `BuildInstaller.bat` and the GitHub Actions workflow; they are not set up by the .sln directly.
- **[STILL UNVERIFIED]** The `Bundle.wxs` for bootstrapping the .NET 10 Desktop Runtime is now written (`installer/PrintShopCompanion.Bundle.wxs`). It chains the .NET 10 Desktop Runtime + the MSI into a single `PrintShopCompanion.Setup.exe`. It requires `installer/redist/dotnet-runtime-10-win-x64.exe` to exist for the `wix build` to succeed — the `install-on-windows.ps1 -BuildBundle` and the GitHub Actions workflow download this redist automatically.
- **[CORRECTED]** The CI workflow (`docs/BUILD_AND_RELEASE.md` §10) **does NOT build the MSI/Setup.exe** — it produces a portable ZIP distribution only. The WiX sources remain but are not exercised by CI. The `installer/redist/` directory is missing `SumatraPDF.exe`, `dotnet-runtime-10-win-x64.exe`, and `app.ico`; `Installer.wxs` references a `PrintShopCompanion.exe.manifest` the App csproj does not produce. WiX build would fail; CI sidesteps this by not building WiX.
- The `CustomAction` to register per-user autostart is an `ExeCommand` call; it runs `PrintShopCompanion.exe --register-autostart`. The `--register-autostart` argument is **implemented** in `App.OnStartup` (drops a per-user Startup folder shortcut).

## Configuration
- **[CORRECTED]** The **App's** `appsettings.json` defaults `Backend:BaseUrl` to `https://printshop-saas-three.vercel.app` (the real production backend — correct). The **Worker's** `appsettings.json` still defaults to `https://mock.printshop.local` — a mismatch to fix. For local testing, set `https://localhost:5241` and `Backend:BypassTlsVerification` to `true` (NOT in production). `BypassTlsVerification` is **implemented** in `InfrastructureModule.AddPrintShopCompanionInfrastructure` — it installs an `HttpClientHandler` with `ServerCertificateCustomValidationCallback = (_,_,_,_) => true`. The companion logs a loud warning when this is enabled.
- **[NEW]** `App.OnStartup` previously checked the wrong credential path (`%AppData%\PrintShop\Companion\device.dat` instead of `%LocalAppData%\...\Security\device.dat`). **Fixed on this branch** (commit `53a5cb3`) — now checks both via `LocalStorageLayout`.

## GitHub Actions
- The workflow installs .NET 10 with `actions/setup-dotnet@v4` and `dotnet-version: '10.0.x'`. .NET 10 may still be in preview depending on the build date; if setup-dotnet does not yet ship .NET 10, pin to the SDK installed by the runner image (the `windows-latest` image will need .NET 10 added to its software inventory).
- WiX v5 is installed as a `dotnet tool` — but the workflow does not yet add `%USERPROFILE%\.dotnet\tools` to PATH. The PowerShell step sets `$env:Path` explicitly; that's the workaround.
- Code signing only runs if the `PRINTSHOP_CODE_SIGN_PFX` and `PRINTSHOP_CODE_SIGN_PWD` secrets are populated. Without them, the MSI is unsigned. **[CORRECTED]** The current workflow has no code-signing step at all (the `PFX_B64`/`PFX_PWD` env vars are hoisted for `if:` conditions but no signing step exists).

## Things the spec asked for that are explicitly deferred
- The existing `PrintAgent` CLI daemon is **not** modified. The companion is a parallel implementation that uses the same backend endpoints. The spec asked for "extend and package that implementation rather than create a second, disconnected printing system"; the clean-room assumption means the companion is in fact a re-implementation against the same protocol. This is acceptable for Phase 1–5 scaffolding but must be revisited once the existing CLI source is in hand.
- Website `/shop/printer` redesign (Phase 8) is out of scope here — it belongs in the PrintShop web monorepo.
- The brief's §4 pairing-ticket flow and §8 remote-command channel are **deferred to Agent 1** (SaaS owner) — the backend does not implement them. See `docs/CONTRACT_COMPATIBILITY.md` §3.
