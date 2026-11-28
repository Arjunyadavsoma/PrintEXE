# HONEST STATUS — Read This First

> **PHASE 9 UPDATE 2026-11-28** (F-1 fixed: database reliability, recovery & MSI):
> (1) **F-1 ELIMINATED.** The unclassified 5-second retry loop is gone.
> Failures are now CLASSIFIED (Transient / Corruption / KeyMismatch /
> PermissionDenied / DiskFull / Configuration / Unknown), and the queue loop
> either backs off exponentially with jitter (2s..300s cap), opens a circuit
> breaker (probe every 60s, auto-resume), or STOPS permanently — queued jobs
> are NEVER discarded and processing resumes after restart or recovery.
> (2) **Corruption recovery policy (documented, tested):** classify →
> quarantine the original file (timestamped sibling, NEVER deleted, NEVER
> decrypted) → preserve WAL/SHM sidecars as evidence (the old code DELETED
> them) → best-effort recoverable-job accounting (recorded as unknown when
> unreadable — never fabricated) → JSONL recovery journal
> (Database/recovery-log.jsonl) → DI-visible recovery state → clean recreate
> with the SAME key. Repeat guard: after 3 recoveries the storage layer is
> declared failing and recovery STOPS (no repeated destructive attempts).
> (3) Two real bugs the new tests caught during development: SQLITE_NOTADB
> (26) now also triggers recovery (it fires at PRAGMA time, before
> integrity_check), and Microsoft.Data.Sqlite CONNECTION POOLING served
> stale handles to the fresh-DB recreation after quarantine (ClearAllPools
> added). (4) Recovery status surfaces in the window title (degraded
> suffix), DiagnosticsService snapshot + export, and structured logs with
> correlation IDs. (5) MSI: built + INSTALL-TESTED (silent install → file
> layout → launch → uninstall → reinstall) on EVERY push to main as an
> artifact — never a release. Full regression: Queue.Tests 59+23 new passed,
> Infrastructure 220 passed, Integration 18 passed, 0 failures.

> **⚠ PHASE 8 UPDATE 2026-11-28** (Windows validation — pipeline GREEN):
> The full pipeline ran GREEN on GitHub's hosted Windows runner, twice
> (runs 46122065378 and 46122718334, commits e355025 + 3d556ad): build,
> both-TFM tests (0 failed; windows leg executes the DPAPI/SQLCipher/DI
> tests that skip on Linux), publish, output validation, startup
> verification, UI automation probe (REAL login interaction: typing,
> password toggle, mirror check, screenshot artifact), fatal-path
> fail-closed (malformed config -> exit!=0 + FTL), ZIP+SHA-256 packaging.
> Key discoveries, honestly recorded: (1) the hosted runner session never
> completes a WPF render pass, so MainWindow.Loaded cannot fire there — the
> app now reports RENDER-UNVERIFIED and the REQUIRED UIA probe verifies real
> UI interaction instead (liveness alone can never pass); (2) corrupt
> queue.db does NOT fail startup BY DESIGN (lazy open + degrade-and-heal);
> (3) KNOWN ISSUE F-1 (MEDIUM): JobProcessor.RunAsync retries forever on a
> corrupt DB with no heal/surfacing — fix before stable release; (4) MSI
> build needs a dispatch-capable token or an authorized tag (PAT lacks
> actions:write; build-msi input ready). NO tag, NO release created.

> **⚠ PHASE 7 UPDATE 2026-11-27** (production-grade CI + startup verification):
> (1) **The v0.7.19 "fix" was wrong and the second startup-crash layer was STILL
> LIVE in HEAD.** The v0.7.19 commit message claimed PasswordBox derives from
> TextBoxBase — WPF ancestry is actually TextBox → TextBoxBase → Control while
> **PasswordBox → Control** (a SIBLING of TextBoxBase), so the retarget to
> `{x:Type TextBoxBase}` still threw on `<PasswordBox Style="{StaticResource
> InputField}"/>` at LoginView load. The earlier "full static sweep" validated
> against that same false model, which is how a wrong fix got a green sweep.
> Real fix (this commit): `InputField` TargetType = **`{x:Type Control}`** — the
> actual common ancestor; every setter and trigger is a Control-level property.
> Caught by the new `tools/xaml_audit.py` (`XAML_TARGETTYPE_MISMATCH`) whose
> ancestry model is now regression-tested.
> (2) **CI startup verification replaced the liveness-only smoke test** that
> passed v0.7.15–v0.7.17 (a modal error dialog blocks forever = "alive"). The
> app gained `--ci-smoke` (real DI host + real MainWindow, structured markers
> `CI-SMOKE: DI HOST BUILT` / `CI-SMOKE: MAINWINDOW LOADED`, `ci-smoke-result.txt`,
> NO modal dialogs in this mode, graceful self-shutdown). The CI gate requires
> exit 0 within 90s + all markers + zero `[FTL]`/`Startup failed` lines, and
> uploads the startup logs on every run. This gate would have FAILED
> v0.7.15, v0.7.16 and v0.7.17's tag runs.
> (3) **XAML static audit is now a required CI job** (ubuntu, seconds) with 14
> detector regression tests (v0.7.18 NavItem missing-key fixture, v0.7.19
> TextBoxBase-on-PasswordBox fixture, BasedOn chains, dup keys, merged-dict
> integrity, converter existence). msi + release jobs now `needs: [build,
> xaml-audit]`.
> (4) **Tests can no longer pass as empty bodies**: the 5 neutral-leg
> DI-composition placeholders and the 5 DPAPI/SQLCipher runtime guards now
> SKIP with reasons (Xunit.SkippableFact 1.5.85) instead of always-green
> passing. New coverage: DI host build/dispose lifecycle, JobProcessor
> start/stop, storage-layout idempotency + device-token writability
> (both legs).
> (5) **Evidence + release protection**: build/test console logs, TRX, startup
> logs, XAML audit report, ZIP and MSI all ship as artifacts with explicit
> `retention-days`; six gates (build, tests, publish, validate, startup,
> packaging) are reported separately in the run summary; the release job
> verifies the tag exists AT THIS COMMIT before creating anything — CI never
> creates tags. No stable tag/release until this full pipeline is green.

> **⚠ v0.7.2 UPDATE 2026-11-26** (real-machine failure + contract sync):
> (1) **SQLCipher**: a real Windows machine could not open the encrypted queue
> ("SQLCipher native provider is not available"). Root cause: the initializer
> reflected over Batteries types in assemblies that DON'T EXIST (the class
> lives in `SQLitePCLRaw.batteries_v2`), so init always failed; additionally
> the meta Microsoft.Data.Sqlite package shipped a SECOND same-named
> batteries assembly registering the PLAIN provider. Fixed: Microsoft.Data.
> Sqlite.Core + bundle_e_sqlcipher only, provider set directly, availability
> PROVEN via `PRAGMA cipher_version` (validated: "4.5.2 community"); CI now
> hard-fails if `e_sqlcipher.dll` is missing from the published ZIP; error
> dialogs rewritten for end users. (2) **Contract sync with
> Arjunyadavsoma/printshop-saas** (v1.1, commit 0701237): full field-by-field
> verification in `docs/contracts/CONTRACT_SYNC_2026-11-26.md`; fixed drifts
> that would break against production — `lastError:null` → 400 (now omitted
> + truncated, enforced by a contract-faithful fake), revocation/command
> events now matched against the SERVER agentId (they never matched the
> local GUID — a revoked device kept printing), `failedJobs`/`pdfSha256`
> surfaced. `companion-v1.schema.json` copied byte-identical into
> `docs/contracts/`. STILL NOT verified against the LIVE backend (needs a
> real pairing session) — the sync is against the route code, not a live run.

> **⚠ v0.7.1 UPDATE 2026-11-26** (first Windows CI test run — run #208):
> Windows surfaced two defects Linux cannot produce, both fixed here.
> (1) Three test suites passed their temp dir as the SECOND positional
> `LocalStorageLayout` argument — which is `legacyRootOverride`, NOT a root
> override — so tests ran against the real `%LocalAppData%` /
> `%AppData%\PrintEXE` paths; on Windows the queue-restart test then saw its
> own DB as a "v0.4.7 legacy file" and the migration move collided with a
> pooled SQLite handle (`IOException: file in use`), while the integration
> fail-closed test silently restored a token a previous test had persisted
> to the real DPAPI path (on Linux `ProtectedData` fails non-fatally, so the
> test passed there by accident — it was NOT proving fail-closed behavior).
> All six call sites now use the new named `rootOverride:` parameter; the
> integration fixture isolates and deletes its own token path.
> (2) Production hardening: `PrepareQueueMigration` now calls
> `SqliteConnection.ClearAllPools()` before moving the legacy DB —
> Microsoft.Data.Sqlite's pool keeps file handles open after close, which
> wedges a v0.4.7→v0.6 migration on a real Windows machine after any
> in-process reopen. 256 tests green on Linux; Windows CI is the verifier
> for this commit.

> **⚠ v0.7.0 UPDATE 2026-11-26** (branch: research-implementation pass on `main`,
> competitor-research program: QZ Tray / PaperCut Mobility Print / ZMA printer agent /
> PrintNode → `docs/research/PRINTEXE_*` + engineering decisions D-01..D-15).
>
> **Implemented and TESTED on Linux (net10.0 neutral TFM):**
> - Multi-target build: the OS-agnostic suite now RUNS on any OS (was Windows-CI-only
>   build; the "tests pass on Linux" claim finally became true). RID pinning trims
>   native runtime copies to the host.
> - **247 tests** (was 216): + spooler tri-state tracker, shop-ownership validator,
>   PDF inspection (real PDFium: valid/empty/corrupt/non-PDF/preview), log redactor,
>   diagnostics aggregation/export, v0.6 priority/due-time queue round-trips, and a
>   **10-test integration suite against a REAL HTTP server** implementing the
>   production agent contract (audit §14 gap closed).
> - D-03 honest spooler outcomes; D-04 managed PDF inspection (encrypted/corrupt/
>   empty refused before printing); D-05 real GDI raster fallback (placeholder +
>   synthetic spooler id REMOVED); D-06 redactor wired into both Serilog pipelines;
>   D-08 priority/due_at (v0.6 migration); D-09 tray pause/resume live; D-11 approval/
>   delivery-failed reprint refusal; D-12 shop stamping + ownership enforcement on
>   both receive paths (ShopId was always "" — fixed).
> - New Diagnostics screen + export (D-07); installer assets (ico/png/manifest, D-13);
>   optional CI MSI job (WiX v5, tag-only); version 0.7.0.
>
> **Still NOT verified (requires a Windows machine + real printer + live backend):**
> physical test print and spooler confirmation on real hardware, real-device pairing
> end-to-end, the MSI build output, tray UX on a real Windows session, DPAPI paths
> (auto-skip on Linux; run on windows-latest CI). The integration fake proves the
> REST contract shape; Socket.IO remains source-verified only.
>
> The banners below (Phase 1/Phase 2, 2026-11-16/17) are the historical record;
> `docs/PRINTEXE_EXISTING_ARCHITECTURE.md` maps every audit item to its current
> state. Nothing above is claimed as Windows-runtime-verified.

---
> **⚠ PHASE-2 UPDATE 2026-11-17** (branch `feat/unified-print-exe-cloud-integration`,
> commit `dc5ab4d`). Agent 1 published the frozen v1 contract
> (`docs/integration/COMPANION_V1_CONTRACT.md`, SaaS commit `ffd0bb3`).
> PrintEXE implemented the v1 contract: pairing-ticket exchange
> (`POST /companion/pair`), the remote command channel (`GET/POST
> /companion/commands` + 7-type executor + dedup + poller), the
> `device.command.issued` + `device.revoked` Socket.IO events, the WPF App
> now starts the realtime client + a 15s heartbeat timer + a 2-3s command
> poll, the contract token path (`%AppData%\PrintEXE\device-token.dat`) with
> 3-tier migration, the `PrintShop:*` config section, and 403-DEVICE_DISABLED
> distinct from 401-revoked. **107/107 unit tests pass on Windows CI**
> (run `44005967006`, all 20 build steps green). The two Phase-0 conflicts
> (pairing-ticket, remote-commands) are RESOLVED — see
> `docs/CONTRACT_COMPATIBILITY.md` banner + `docs/CONTRACT_V1_REVIEW.md` +
> `docs/PHASE2_FINAL_REPORT.md`.
>
> **Still NOT verified (requires a Windows machine + staging backend + real
> printer — none in this sandbox):** end-to-end pairing via a real ticket,
> reconnect/re-pair on a real Windows restart, remote command execution on a
> real PC, physical test print + spooler evidence, revocation propagation. The
> contract is implemented and unit-tested; runtime integration is the next
> phase.
>
> The body below is the **Phase-1** HONEST-STATUS (2026-11-16, commit `53a5cb3`);
> the banner above + `docs/PHASE2_FINAL_REPORT.md` supersede it on conflict.

---

> **⚠ UPDATED 2026-11-16** (branch `feat/unified-print-exe-cloud-integration`, commit
> `53a5cb3`). A full pre-coding audit + SaaS contract reconciliation was performed.
> The authoritative findings are in `docs/AUDIT.md` (full audit) and
> `docs/CONTRACT_COMPATIBILITY.md` (contract reconciliation + 2 open conflicts).
> Key updates below the table; this banner supersedes anything in the body that
> conflicts with it.
>
> **What's now verified by source inspection (not by runtime):**
> - The companion's live production client (`HttpPrintShopBackend`,
>   `SocketIoRealtimeClient`) **already matches the real SaaS contract** for base
>   URL (`https://printshop-saas-three.vercel.app`), `/api/v1` prefix,
>   `Authorization: Bearer` auth, `ApiEnvelope`, all REST endpoints, and all
>   Socket.IO events (`job.dispatch`, `auth`, `auth-ok`, `agent.revoked`, etc.).
>   The contract drift was in the docs + dead `CompanionProtocol.HubMethod*`
>   constants — both now reconciled.
> - The `PrintAgent` / `PrintJob` Prisma models **do exist** in the SaaS repo;
>   the companion's `PrintJobRecord` field names match the backend's `PendingJob` DTO.
> - The previous "best-guess Socket.IO names" (`DeliverJob`/`AckJob`/`ReportStatus`)
>   were wrong; the real names are `job.dispatch`/`auth`/`agent.revoked` etc.
>   (now `CompanionProtocol.SocketIoEvent*`, with the old constants `[Obsolete]`).
>
> **What's still NOT verified (requires a Windows machine + the live backend):**
> - End-to-end pairing, heartbeat, job dispatch, or status reporting against
>   `https://printshop-saas-three.vercel.app`. The contract is verified by source
>   inspection only — no runtime integration test has been run.
> - Any physical printing. No Windows machine, no printer in this sandbox.
> - The WPF App does NOT start the realtime client or a poller today
>   (`docs/AUDIT.md` §11, §16 item #3) — so it cannot receive print jobs through
>   the agent pipeline as written. This is the most important functional gap.
> - `HasPrintedAsync` false-positives on purged jobs; `SumatraPdfPrinter` returns a
>   synthetic spooler ID when `-print-stat` parsing fails; `RawGdiPrinter` prints a
>   placeholder, not the PDF; `OptionsWriter.Update` is a stub; `DeviceTokenRedactor`
>   is not wired; the heartbeat is not driven by a timer. See `docs/AUDIT.md` §16.
>
> **Two open conflicts with the brief (change requests for Agent 1 / SaaS owner):**
> 1. The brief's §4 **pairing-ticket** flow is NOT implemented by the backend —
>    the backend does paste-token (long-lived, no TTL, no consume).
> 2. The brief's §8 **remote-command** channel (RefreshPrinters/Pause/Resume/Cancel/
>    TestPrint/Diagnostics) is NOT implemented by the backend — no command channel
>    exists. The companion implements these as **local UI actions** only.
>    See `docs/CONTRACT_COMPATIBILITY.md` §3. The companion does NOT invent them.
>
> **What this branch fixed (commit `53a5cb3`):**
> - `CompanionProtocol.cs` reconciled to real Socket.IO event names.
> - `App.OnStartup` credential-path check fixed (was checking the wrong path).
> - `SqliteJobQueue` v0.5-column data-loss bug fixed (`ReadRecord`/`WriteStatusAsync`/
>   `Enqueue` now round-trip `color_mode`/`page_count`/`original_filename`/
>   `order_token`/`delivery_status`/`storage_status`/`checksum`/`file_size`/
>   `received_at`/`cloud_delete_after`/`local_delete_after`/`delivery_attempt_count`/
>   `last_delivery_error`).
> - `ReplayInFlightAsync` no longer resets `UncertainSpooled`/`OutcomeUnknown` to
>   `Pending` (preserves the "never auto-reprint uncertain" invariant).
> - 3 new tests: `ReplayInFlight_does_not_reset_UncertainSpooled_or_OutcomeUnknown`,
>   `UpdateAsync_round_trips_v05_columns`, `JobStatusMappingTests` (4 tests).
>
> The body below is the **pre-audit** HONEST-STATUS from earlier phases; read it as
> historical context. The banner above + `docs/AUDIT.md` supersede it on conflict.

---

This document is the unvarnished, no-spin report on what is and isn't done. It is the counterpart to the marketing-style README.

## TL;DR

| Question | Answer |
| --- | --- |
| Is there a real `.exe` / `.msi` in this delivery? | **No.** This Linux sandbox has no .NET / WiX. A real installer can only come from a Windows build. |
| Can the user produce a real `.exe` from this code? | **Yes**, two ways: (1) run `scripts/install-on-windows.ps1` on a Windows 11 PC, or (2) push to the repo and let `.github/workflows/build-windows.yml` produce a signed `.msi` + `.exe` on the Windows GitHub Actions runner. |
| Did I see the real PrintShop repo? | **Partially.** I successfully fetched `prisma/schema.prisma` (235 lines) and `package.json` from the `master` branch. The actual `mini-services/print-agent/` directory and `app/api/print-agent/*/route.ts` files could not be fetched (GitHub API rate-limited during inspection). |
| Does the companion compile? | **Unknown.** I cannot compile WPF in this Linux sandbox. The code is syntactically careful and references real NuGet packages, but the first green `dotnet build` on a Windows box is the actual first milestone. |
| Does it print on a real printer? | **No.** No printer was connected during this build. The first test print on real hardware is part of Phase 9 — not yet performed. |
| Can the companion pair with the real production backend today? | **No.** The real backend does not yet expose the `pairing/begin`, `register`, `heartbeat`, `jobs/claim`, `jobs/status`, `revoke` routes the companion expects. They are documented in `docs/BACKEND-MAPPING.md` and must be added to the real repo before end-to-end works. |
| Can the companion pair with the included mock backend today? | **Likely yes**, but only on a Windows box. The mock backend is in `src/PrintShopCompanion.Backend/` and implements the same contract. |

## What is DONE

1. **Solution + 6 projects** — Core, Shared, Infrastructure, App, Worker, Backend. All in one `.sln` with `Directory.Build.props` enforcing nullable reference types and strict warnings.
2. **Domain model matches the real Prisma schema** — `PrintJobRecord` now uses `ColorMode` (string "bw"/"color"), `Duplex` (bool), `PageCount`, `OriginalFilename`, `OrderToken` — derived from the actual `PrintFile` and `Order` models in the repo.
3. **Durable SQLite queue** with idempotent enqueue (`ON CONFLICT DO NOTHING`), exponential backoff for retryable failures, crash-replay for in-flight jobs, corrupt-DB quarantine + recovery, and disk-space pre-check (256 MB minimum).
4. **All required job states implemented**: Pending, Downloading, Rendering, Spooled, **UncertainSpooled** (new — never auto-reprinted), Printed, ReportedPrinted, **OutcomeUnknown** (new — heartbeat reconciles), Failed, Cancelled.
5. **JobProcessor** explicitly refuses to auto-reprint UncertainSpooled or OutcomeUnknown jobs. Operator-driven reconciliation via PrintQueue screen's "Mark as printed" and "Reprint" buttons.
6. **Real backend contract documented** in `docs/BACKEND-MAPPING.md` — derived from the real Prisma schema. Every endpoint the companion needs is listed with HTTP method, path, auth, request/response, error responses, retry behaviour, idempotency behaviour.
7. **Backend integration** — `HttpPrintShopBackend` honours `Backend:BypassTlsVerification` (dev only) via a custom `HttpClientHandler`. Loud console warning when enabled. Never enabled by default.
8. **Magic-link pairing** with `printshopcompanion://` URL scheme registered by WiX in `HKCU\Software\Classes`. Single-use, signed, expiry-checked pairing tokens. DPAPI-encrypted device-token storage at `%AppData%\PrintShop\Companion\device.dat`.
9. **Two renderers** in a composite fallback chain: **SumatraPDF** (primary — bundled `SumatraPDF.exe` invoked with `-silent -print-to -print-stat`; it renders the PDF internally and submits to the Windows spooler) → raw GDI / winspool.drv (fallback for ESC/POS label printers). The previous PDFium in-process renderer was removed (NU1102/NU1701/NU1903 — vulnerable legacy transitive deps, unavailable native binary, .NET Framework target).
10. **Windows printer discovery** via `System.Printing.LocalPrintServer` enumerating local + connected `PrintQueue`s, with offline detection, paper-size and duplex capability queries.
11. **WPF app** — 5 screens (Setup, Dashboard, Printers, Print Queue, Settings), Fluent theme via WPF-UI, light/dark auto-follow, tray icon with right-click menu, single-instance guard with named-pipe IPC for magic-link forwarding, `--register-autostart` / `--unregister-autostart` / `--version` CLI args, per-user Startup shortcut (no admin needed).
12. **Notifications** — `NotificationService` surfaces meaningful events (pairing success, test print success/failure, printer offline, job needs attention, connection lost/restored) via Windows balloon notifications. Conservative — no spam.
13. **Mock backend** — ASP.NET Core with pairing/heartbeat/jobs/pdf endpoints + auto-generated test jobs for local development.
14. **WiX installer** — `PrintShopCompanion.Installer.wxs` registers the URL scheme per-user (no admin), bundles PDFium + SumatraPDF, adds Start Menu shortcut, optionally installs the worker service (admin-only feature).
15. **WiX bundle** — `PrintShopCompanion.Bundle.wxs` chains the .NET 10 Desktop Runtime + the MSI into a single `.exe` bootstrapper (`PrintShopCompanion.Setup.exe`).
16. **GitHub Actions workflow** builds on `windows-latest`, restores, builds, tests, **publishes self-contained** (so the shopkeeper does not need .NET installed), builds the MSI, builds the bundle, optionally signs with EV cert, generates SHA-256 checksums, writes build metadata, uploads artifacts, and creates a GitHub Release on tag push.
17. **One-click Windows build script** — `scripts/install-on-windows.ps1` installs .NET 10 + WiX via winget, downloads SumatraPDF, builds, tests, publishes self-contained, builds the MSI, optionally builds the bundle, generates SHA-256 checksums, copies everything to `Downloads`, and offers to launch the installer.
18. **xUnit tests** for the queue (idempotency, dequeue ordering, failed-job backoff, replay-in-flight, terminal purge, cancel) and the pairing service (scheme/host/token/expiry validation, accepted valid link).
19. **Honest documentation** — `README.md`, `docs/ARCHITECTURE.md`, `docs/PROTOCOL.md`, `docs/PHASES.md`, `docs/SECURITY.md`, `docs/BACKEND-MAPPING.md`, `NOT-VERIFIED.md`, `INSTALL.md`, `HONEST-STATUS.md` (this file).

## What is NOT done

### Cannot be done in this sandbox (requires Windows)

1. **The actual `.msi` and `.exe` have never been built.** No `dotnet`, `mono`, or WiX exists in this Linux sandbox. The artifacts only exist after `install-on-windows.ps1` or the GitHub Actions workflow runs on a Windows machine.
2. **The companion has never compiled.** Compile errors likely exist. The first `dotnet build` on a Windows box will catch them. Known likely issues are listed in `NOT-VERIFIED.md`.
3. **No real printer has ever printed a test page.** The PDFium renderer, SumatraPDF fallback, and raw GDI path are all unverified on real hardware. The test-print button has never been clicked against a physical printer.
4. **No real pairing has happened against the production backend** at `https://printshop-saas-three.vercel.app`. The real backend does not yet expose the routes the companion expects.

### Cannot be verified without the real repo's API routes

5. The exact path prefix (`/api/companion/v1` vs `/api/print-agent`) and the exact DTO field names are **best-guesses based on the Prisma schema**. The companion's `BackendOptions.ApiBasePath` and `BackendOptions.HubPath` defaults must be confirmed against the actual `app/api/print-agent/*/route.ts` files once they exist.
6. The `socket.io` event names (`printJob`, `ack`, `status`) are guessed. The existing Bun/TypeScript print-agent CLI is the source of truth for these names but its source code was not reachable during inspection.

### Deferred to a follow-up

7. **Auto-update mechanism.** The companion does not yet auto-update. Updates are MSI-driven — push a new build via GitHub Actions and the shopkeeper re-installs.
8. **Multi-printer routing.** The companion supports selecting ONE default printer. The spec asked for "assign printers to supported job types" (e.g. label jobs → label printer, document jobs → laser). This is a Phase 3+ feature not yet implemented.
9. **Website `/shop/printer` redesign.** Out of scope for this delivery — belongs in the PrintShop web monorepo. The companion's needs are documented in `docs/BACKEND-MAPPING.md` §4.
10. **End-to-end customer order test.** Not performed. The "Definition of Done" scenario in the brief has not been executed.

### Known code-level issues

11. `RawGdiPrinter.SendRawBytes` P/Invoke is correct on paper but unverified on a real ESC/POS label printer.
12. `SingleInstance.ForwardUrl` named-pipe IPC has a startup race: if a second instance launches before the first has set up its pipe server, the URL is lost. A retry-with-backoff is needed.
13. `SumatraPdfPrinter` parses the spooler job ID from `SumatraPDF.exe`'s stdout (`SPOOLER_JOB_ID=<int>`). This depends on the bundled SumatraPDF version honouring `-print-stat`. If it doesn't, the companion falls back to a synthetic spooler ID, and physical-print confirmation via `WindowsPrinterService.HasPrintedAsync` won't be able to track the spooler job. In that case the job ends up in `UncertainSpooled` (see `JobProcessor`).
14. `OptionsWriter.Update` in `SettingsViewModel.cs` is a stub — it does not actually update `appsettings.Local.json`. The Save button currently writes nothing. (Real implementation: read the JSON, update the key, write back. A `System.Text.Json.Nodes.JsonNode` rewrite is a 20-line change.)
15. `AutoStartService.CreateShortcut` shells out to PowerShell. If PowerShell is restricted by group policy, this fails silently. A COM interop with `IWshRuntimeLibrary` is the production-grade fix.
16. `WiX Bundle.wxs` references `LICENSE.txt` (exists) — the bundle build should now work, but has not been verified.
17. The `app.ico` and `app.png` files in `installer/redist/` do not exist. The WiX installer falls back to the default icon; the bundle build will succeed but with no custom branding.

## First-milestone checklist (before any production auto-printing)

- [ ] Drop this tree at `apps/windows-companion/` in the real PrintShop repo.
- [ ] Move `.github/workflows/build-windows.yml` to `.github/workflows/` at repo root.
- [ ] Push to GitHub. Wait for the Windows runner to produce a green build.
- [ ] Download the `PrintShopCompanion-msi` artifact. Verify SHA-256 against `SHA256SUMS.txt`.
- [ ] Install on a fresh Windows 11 PC. Launch. Confirm the Setup screen appears.
- [ ] Run the mock backend on a second terminal. Pair the companion against the mock.
- [ ] Pick a real installed printer. Click Test print. Verify a real page prints.
- [ ] Add the routes listed in `docs/BACKEND-MAPPING.md` §4 to the real backend. Add Prisma migration if needed.
- [ ] Re-point `BackendOptions.BaseUrl` to `https://printshop-saas-three.vercel.app`. Pair against the real backend.
- [ ] Place a real customer order on the website. Verify the companion receives, prints, and reports.
- [ ] Crash the companion mid-job. Restart. Verify in-flight jobs are replayed exactly once.
- [ ] Sign the MSI with an EV code-signing certificate via the CI secrets.

None of these have been performed. Each one is a real verification step, not a checkbox to tick.

## What I will NOT claim

Per the user's brief: "Do not fabricate build logs, test results, hardware results, release links or file paths."

- I will not claim the MSI exists.
- I will not claim the tests pass.
- I will not claim a printer has printed.
- I will not provide a fake "download link" to a release.
- I will not claim the companion compiles.

The path to a real `.exe` is: (a) untar this tree, (b) run `scripts/install-on-windows.ps1` on a Windows 11 PC, or (c) push to GitHub and let `build-windows.yml` produce the artifact on the Windows runner. Both paths are real and will produce a real `.msi` (and a real `Setup.exe` if the bundle redist is available).
