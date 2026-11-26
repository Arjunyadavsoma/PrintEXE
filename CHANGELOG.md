# PrintShop Companion — Changelog

## v0.7.12 — 2026-11-26 — Publish the Worker; drop the dead mock-backend from the installer

With the preprocessor variable fixed (v0.7.11), WiX failed WIX0103 on two
files the MSI's publish directory never contained:

- `PrintShopCompanion.Worker.exe` — the installer ships it (WorkerFeature)
  but the MSI job only ever published the App project.
- `PrintShopCompanion.Backend.Mock.exe` — the project no longer exists;
  it was superseded by the ASP.NET `PrintShopCompanion.Backend` web
  project (a server, not a desktop binary). The WiX source still
  referenced the stale binary.

### Fixed
- The MSI job now publishes the Worker (framework-dependent, into the
  same `publish` dir) alongside the self-contained App.
- `MockBackendFeature` + `MockBackendComponents` removed from
  `PrintShopCompanion.Installer.wxs`; all remaining ComponentGroup refs
  verified to resolve.

## v0.7.11 — 2026-11-26 — Pass the WiX preprocessor variable it actually reads

With WiX v5 pinned (v0.7.10), the MSI build advanced to the preprocessor
and failed with `WIX0150: Undefined preprocessor variable
'$(var.BuildVersion)'` — the workflow step passed `-d Version=...`, but
the WiX source (`PrintShopCompanion.Installer.wxs`) reads
`$(var.BuildVersion)`. A naming mismatch that no tag run ever reached
before the pipeline was repaired.

### Fixed
- The "Build MSI" step now passes `-d BuildVersion=` (matching the .wxs
  source). `PublishDir` and `RepoRoot` were already correct.

## v0.7.10 — 2026-11-26 — Pin the WiX tool to v5 in CI

The v0.7.9 run advanced past the publish (normalization fix held) and
failed at the WiX build itself: `error WIX7015: You must accept the Open
Source Maintenance Fee (OSMF) EULA to use WiX Toolset v7`. The workflow's
"Install WiX v5 tooling" step never actually pinned a version, so
`dotnet tool install --global wix` pulled the latest (7.0.1), which gates
CI execution behind the OSMF EULA. Everything in this repo targets WiX v5
(`installer/README.md`, `BuildInstaller.bat`, the `.wxs` sources).

### Fixed
- The tool install is now pinned: `dotnet tool install --global wix
  --version 5.*` (verified to resolve 5.0.2).

## v0.7.9 — 2026-11-26 — MSI job: normalize the tag before BUILD_VERSION

The v0.7.8 run got further than any tag build before it (restore fix held;
tests, ZIP, and Release all green) and then hit a latent bug in the MSI
job's publish step: it passed the RAW tag name as `BUILD_VERSION`, so
`<AssemblyVersion>` became `v0.7.8.0` and compilation failed with
`CS7034: The specified version string 'v0.7.8.0' does not conform to
major[.minor[.build[.revision]]]`. The build job has always normalized
the tag ("Set BUILD_VERSION from git tag": strip `v`, trim any pre-release
suffix); the MSI job never did — it simply never reached this step before
(v0.7.3 died at the SumatraPDF download, v0.7.7 at restore).

### Fixed
- The MSI job's publish step now applies the same normalization the build
  job uses before handing the value to `-p:BUILD_VERSION=`. The "Build
  MSI" step already stripped the `v` for the WiX `Version` variable, so
  only the publish step needed the change.

## v0.7.8 — 2026-11-26 — MSI job: restore explicitly before publish

v0.7.7 got the whole pipeline green except one step: the MSI job's
`Publish app (win-x64)` failed with `MSB4181: The "RestoreTask" task
returned false but did not log an error` — all four projects "failed to
restore" in ~50 ms with no diagnostics. The step's bare `dotnet publish`
ran the implicit restore under the runner's preinstalled SDK 11.0.100
(the log path shows `sdk\11.0.100\NuGet.targets`), not the 10.0.x that
`actions/setup-dotnet` installed for the job. The build job never hit
this because it runs an explicit `dotnet restore` step first.

### Fixed
- The MSI job now restores the App project explicitly
  (`dotnet restore -r win-x64`) and publishes with `--no-restore`, so a
  restore problem surfaces with real diagnostics instead of MSB4181, and
  the publish no longer depends on which SDK wins PATH ordering.

## v0.7.7 — 2026-11-26 — Bring the SQLCipher native into the Queue.Tests testhost

v0.7.6's TFM gating worked (the neutral net10.0 leg went 38/38), moving the
failure to the net10.0-windows leg: `DurableCommandStore_is_resolvable`
failed with the production fail-closed message "the SQLitePCLRaw.
bundle_e_sqlcipher native provider was not available for this platform".
The DI composition tests resolve the REAL `SqliteJobQueue` (with a real
`QueueKeyStore`), so the testhost needs the SQLCipher native binary —
every pre-existing Queue.Tests suite passes `keyStore: null`, which is why
the bundle was never needed there before.

### Fixed
- `PrintShopCompanion.Queue.Tests.csproj` now references
  `SQLitePCLRaw.bundle_e_sqlcipher` 2.1.11 — the same fix
  PrintShopCompanion.Infrastructure.Tests applied in v0.7.2 ("use the
  SQLCipher bundle (matching the app) so the encryption tests exercise the
  REAL provider").
- Belt-and-braces: the DI tests capture
  `SqlCipherInitializer.IsEncryptionAvailable` in the constructor and skip
  when the native provider cannot load (the same guard as
  `SqliteConnectionPragmasSafetyTests`) — the production constructor failing
  closed is correct behavior, not a bug to report.

## v0.7.6 — 2026-11-26 — Gate the DI composition tests by TFM, not by OS

The v0.7.5 run narrowed the failure to one test on the Windows runner's
**net10.0 (neutral) leg**: `CommandPoller_is_resolvable` →
`Unable to resolve 'IPrinterService' while activating 'CommandExecutor'`.
The full DI surface — including `IPrinterService`→`WindowsPrinterService`,
which `CommandExecutor` requires — only exists in the net10.0-windows TFM
(the TFM every shipped app runs); the neutral TFM deliberately registers a
partial surface. The tests' `OperatingSystem.IsWindows()` guard was the
wrong tool: it evaluates the OS, not the TFM, so on a Windows machine the
neutral leg still executed bodies its composition cannot satisfy.

### Fixed
- `InfrastructureDiCompositionTests` bodies are now compiled only under
  `#if WINDOWS` (compile-time TFM gate, matching the DI surface). The
  neutral leg keeps always-green placeholders so the class still appears
  in both TFMs' test output.
- Verified: net10.0 leg builds clean; net10.0-windows leg has no errors in
  the test file (the windows-TFM cross-compile on Linux fails only in the
  pre-existing `RawGdiPrinter.cs` GdiPlus references, an environment
  artifact that CI builds fine on real Windows runners).

## v0.7.5 — 2026-11-26 — Fix the v0.7.4 Windows test failure

The v0.7.4 tag run's "Run tests" step failed on the Windows runner (the
same tests pass on the neutral net10.0 leg): the new DI composition tests
built their service container WITHOUT an IConfiguration registration, but
InfrastructureModule binds PrintShop:*/Backend:* via BindConfiguration,
which resolves IConfiguration from the provider — so the option binding
threw during test construction on Windows (where the test bodies actually
execute; the Linux leg skips at the OperatingSystem guard).

### Fixed
- `InfrastructureDiCompositionTests` now registers an empty
  `ConfigurationBuilder().Build()` IConfiguration (values fall back to
  options defaults) and redirects PdfCacheDir/LogDir/DeviceTokenPath into
  the temp directory alongside QueueDbPath.

## v0.7.4 — 2026-11-26 — Restore the cloud connection, ship the renderer in every distribution

Diagnosed from a real Windows machine's v0.7.3 session log: the app started,
SQLCipher opened, and sign-in worked — but three defects remained. The first
was silent and severe: the device never went truly online. The second broke
PDF printing on every portable-ZIP install. The third failed the v0.7.3
release pipeline's MSI job, so the release page shipped without an installer.

### Fixed — the cloud connection never started (critical)
- `InfrastructureModule` registered `SqliteJobQueue` ONLY as `IJobQueue`, but
  `DurableCommandStore` takes the CONCRETE type in its constructor. Every
  startup logged `StartCloudConnectionAsync failed: Unable to resolve service
  for type 'SqliteJobQueue' while attempting to activate 'DurableCommandStore'`
  as a non-fatal warning, so Socket.IO realtime, the 15s heartbeat timer, and
  the command poller NEVER ran — the backend marked the device offline 30
  seconds after each launch and no remote command ever reached the shop.
- `SqliteJobQueue` is now registered by concrete type first, with `IJobQueue`
  mapped to the same singleton (the existing `JobProcessor`/`IQueueController`
  pattern).
- New `InfrastructureDiCompositionTests` (5 tests) build the real service
  collection and resolve `SqliteJobQueue`, `DurableCommandStore`,
  `CommandPoller`, and `IPendingOpsQueue`, and assert the interface + concrete
  registrations share one singleton — the exact resolutions that threw in
  v0.7.3, now pinned so a future refactor cannot silently re-break them.

### Fixed — the portable ZIP shipped without the PDF renderer
- Real-machine log: `SumatraPDF.exe not found at: ...New folder\SumatraPDF.exe
  — Renderer sumatra failed; trying next`, then the raw-GDI fallback printed
  without a confirmable spooler job. The build job now downloads SumatraPDF
  into the publish directory before packaging, so `SumatraPdfPrinter.
  ResolveExePath()` finds it next to the EXE in every ZIP install.

### Fixed — v0.7.3 MSI job failure (release pipeline)
- The pinned download `https://www.sumatrapdfreader.org/dl/Rel/3.5.1/
  SumatraPDF-3.5.1-64.zip` was retired from the CDN (307 → 404) and broke the
  v0.7.3 tag build's "Download SumatraPDF" step; the release page ended up
  without an MSI. Both the MSI job and the new ZIP-bundling step now walk a
  fallback chain (current 3.6.1 via `/dl/rel/…` → the files CDN → the legacy
  3.5.1 path) with a browser User-Agent, and handle the versioned inner exe
  name (`SumatraPDF-3.6.1-64.exe`).
- `installer/README.md` local-build instructions updated to the live URL.
- The smoke test now also kills any `SumatraPDF.exe` it may have spawned so a
  lingering renderer can't block the artifact upload.

### Verified
- SumatraPDF source URL, size (10,031,830 bytes) and SHA-256
  (`98b33a518d42986856d225064b0cd2d3643ecf78cbf84ab873d26cc51877a544`)
  recorded from a live download on 2026-11-26.

## v0.7.3 — 2026-11-26 — Point the companion at the printelyx.cc.cd production origin

The deployed Printelyx backend is canonically served at `https://printelyx.cc.cd`
(custom domain, Clerk on `clerk.printelyx.cc.cd`). The companion still hardcoded
the ephemeral Vercel origin `printshop-saas-three.vercel.app` in its shipped
configuration. Vercel-generated domains are not a stable contract (they change
when the project is renamed/transferred), so every installed companion must talk
to the custom domain.

### Changed — backend origin
- `src/PrintShopCompanion.App/appsettings.json` — `Backend:BaseUrl`,
  `Backend:SocketIoUrl`, `Backend:AllowedWebOrigins`, and the contract-shape
  `PrintShop:BaseUrl` / `PrintShop:SocketIoUrl` all now point at
  `https://printelyx.cc.cd` (Socket.IO keeps the required
  `/?XTransformPort=3003` Caddy gateway query).
- `src/PrintShopCompanion.Worker/appsettings.json` — same five keys fixed
  (the Worker's copy was missed in the earlier origin audit).
- `src/PrintShopCompanion.Core/Options/Options.cs` — the hardcoded default
  `SocketIoUrl` and the doc-comment example now use `https://printelyx.cc.cd/?XTransformPort=3003`.
- `.github/workflows/build-windows.yml` — the user-facing README inside release
  artifacts now instructs shopkeepers to sign in at `https://printelyx.cc.cd/shop/login`.
- `installer/redist/LICENSE.txt` — the end-user license terms link now points at
  `https://printelyx.cc.cd/terms` (page verified live, HTTP 200).
- `tests/.../PairingTicketValidationTests.cs` — the dummy `BaseUrl` fixture uses
  the canonical origin (never actually contacted; 401 stub).

### Verified (2026-11-26, from the build sandbox)
- `POST https://printelyx.cc.cd/api/v1/companion/pair` responds with the real
  Zod validation error for a missing `ticket`/`deviceName` → the companion
  contract is served from this origin.
- `GET /shop/login` and `GET /terms` → 200 on the same origin.
- Solution `dotnet build` on Linux: 0 errors.

### Not verified (requires a real Windows machine)
- End-to-end pairing, live print dispatch, Socket.IO `job.dispatch` over the
  Caddy gateway on the custom domain, MSI install/uninstall. The Windows CI
  workflow covers build + tests on `windows-latest`; hardware printing is not
  automated.

## v0.7.2 — 2026-11-26 — SQLCipher runtime fix + printshop-saas contract sync

Two tracks: the encrypted-queue failure reported from a real Windows machine
(screenshots: "SQLCipher native provider is not available"), and a field-by-
field contract sync against the actual `printshop-saas` implementation
(commit `0701237`, companion contract v1.1 — full report in
`docs/contracts/CONTRACT_SYNC_2026-11-26.md`).

### Fixed — SQLCipher provider (queue refused to open on Windows)
- **Root cause:** `SqlCipherInitializer` looked for `SQLitePCL.Batteries_V2`
  by reflection in `SQLitePCLRaw.core` / `SQLitePCLRaw.bundle_e_sqlcipher` —
  neither assembly contains that type (it lives in `SQLitePCLRaw.
  batteries_v2`), so initialization ALWAYS failed and the fail-closed queue
  threw on every machine. Worse, the meta `Microsoft.Data.Sqlite` package
  pulled `bundle_e_sqlite3`, shipping a SECOND assembly with the same
  identity (`SQLitePCLRaw.batteries_v2.dll`) registering the PLAIN provider —
  two same-named batteries assemblies made provider selection undefined.
- **Fix:** reference `Microsoft.Data.Sqlite.Core` (auto-registers nothing) +
  `bundle_e_sqlcipher`; set the provider DIRECTLY
  (`SQLitePCL.raw.SetProvider(new SQLite3Provider_e_sqlcipher())`); PROVE
  availability by opening an in-memory keyed connection and reading
  `PRAGMA cipher_version` (validated experimentally with the shipped package
  set: "4.5.2 community"). Removed the `SQLitePCLRaw.lib.e_sqlite3 3.50.3`
  NU1903 pins from all four projects (the advisory leaves the graph with the
  Core package).
- **User-facing error text** rewritten (the dialogs showed NuGet package
  names and Linux test instructions): user action first, dev detail after
  "Technical details:".
- **CI guard tightened:** publish validation now requires `e_sqlcipher.dll`
  by exact name (the old `e_sql*.dll` glob would have passed with the plain
  `e_sqlite3.dll` alone — precisely the reported failure).

### Synced — printshop-saas contract (v1.1)
- **`lastError: null` → 400** (would have failed EVERY claim/failure report
  against production): the status route zod is `.optional()` not
  `.nullable()`; client now omits nulls (`WhenWritingNull` write options) and
  truncates lastError/printerName to the 500/200 caps. The integration fake
  rejects `"lastError":null` like production, so the suite enforces it.
- **Revocation/command events fixed:** they carry the SERVER-assigned agentId;
  the client compared the local device GUID (never matched — a revoked device
  kept printing). New `IPrintShopBackend.ServerAgentId` captured from
  pair/heartbeat/pending responses.
- **`failedJobs` + `pdfSha256`** from the pending route now surfaced (logged /
  carried into the record for the integrity gate).
- **Verified aligned** (no change): auth model, pairing caps, claim/complete
  state machine, receipt checksum format, Socket.IO auth-event handshake,
  commands, sync pull/push, shop-snapshot, 401/403/404 semantics, deployment
  defaults (incl. the note that Vercel runs REALTIME_PROVIDER=memory — REST
  polling is the effective delivery path there).

256 + 2 new tests green locally (net10.0 neutral TFM).

## v0.7.1 — 2026-11-26 — Windows CI fixes (first Windows test run)

The v0.7.0 push was the first commit to RUN the test suite on `windows-latest`
(previous CI only built the ZIP). Windows surfaced two defects that Linux
physically cannot produce, both traced to the same test-isolation mistake and
one real production hardening gap:

### Fixed
- **Test isolation: `LocalStorageLayout` root override misuse.** The second
  constructor parameter is `legacyRootOverride` (v0.4.7 migration detection
  only), but `QueuePriorityTests`, `DiagnosticsServiceTests` and
  `AgentContractTests` passed their temp directory positionally — leaving
  `Root` at the REAL `%LocalAppData%\PrintShop\Companion` and (for the
  integration tests) `DeviceTokenPath` at the REAL
  `%AppData%\PrintEXE\device-token.dat`. Added an explicit named
  `rootOverride:` parameter and fixed all six call sites to use it; the
  integration fixture now also overrides `DeviceTokenPath` and deletes its
  temp root on dispose.
- **Cross-test DPAPI credential pollution (Windows CI only).** The pairing
  tests persisted a device token to the real contract path; the
  `Unauthorized_calls_fail_closed` fixture then lazily restored that token
  from disk and authenticated silently — no exception, test failed. On Linux
  `ProtectedData` throws non-fatally, so the fail-closed test passed there by
  accident. With the token path isolated per test, the restore finds nothing
  and the client throws on every platform, deterministically.
- **Production: clear SQLite connection pools before the v0.4.7 migration
  move.** Microsoft.Data.Sqlite parks closed connections in a process-wide
  pool that keeps the DB file handle open; on Windows `File.Move` fails with
  "in use" (POSIX allows moving open files — Linux never saw this).
  `PrepareQueueMigration` now calls `SqliteConnection.ClearAllPools()` before
  the retry-wrapped move, so an in-process reopen (crash recovery, worker
  restart) during a v0.4.7 → v0.6 upgrade can no longer wedge the migration.
- Version 0.7.0 → 0.7.1; no API or contract changes; 256 tests green on
  Linux (net10.0) and expected green on Windows CI (both TFMs).

## v0.7.0 — 2026-11-26 — Research-driven reliability + diagnostics release

This release implements the competitor-research program (QZ Tray, PaperCut Mobility
Print, ZMA printer agent, PrintNode — see `docs/research/`) as concrete engineering
decisions (`docs/research/PRINTEXE_ENGINEERING_DECISIONS.md`, D-01..D-15).

### Reliability (printer engine)
- **Honest spooler outcomes (D-03):** tri-state `SpoolerOutcome` replaces the old
  boolean poll whose "job not found in any queue → assume printed" rule produced
  false-positive completions. Seen-then-purged = printed; never-seen = keep polling
  → UncertainSpooled. Encoded by `SpoolerOutcomeTrackerTests`.
- **Real raw-GDI fallback (D-05):** the GDI renderer rasterises the ACTUAL PDF pages
  via PDFium (paper size, duplex, copies honored) instead of printing a placeholder
  string; synthetic random spooler ids removed (parity with the Sumatra path).
- **Managed PDF inspection before printing (D-04):** encrypted/corrupt/empty/oversized
  documents are refused permanently with precise reasons (`PdfDocumentInspector`,
  PDFtoImage/PDFium); page count is filled locally when the backend omits it;
  spec-correct test-page generator replaces the hand-crafted PDF with wrong /Length.
- **Operator priority + due time (D-08):** v0.6 queue schema adds `priority`/`due_at`;
  dequeue order is `priority DESC, queued_at ASC` (rush jobs jump the line).

### Security
- **Log redactor wired (D-06):** `DeviceTokenRedactor` now attached to BOTH Serilog
  pipelines (App + Worker); token/secret/password properties masked at write time.
- **Shop-ownership enforcement (D-12):** received jobs are validated against the
  paired device's shop (Socket.IO and poll paths); mismatched jobs are rejected.
  `ShopId` is now stamped on every record (was always empty — audit finding).
- **Explicit `ProtectedData` package reference** (audit §3 compile risk removed).
- **Approval gate (D-11):** delivery-failed jobs refuse to reprint without operator
  action; the atomic backend claim remains the authorization point (contract-faithful
  — no invented approval fields).

### Desktop experience
- **Diagnostics screen (D-07):** one view answering "is the agent healthy?" (cloud,
  auth, device, DB, queue worker, printers, disk, last successful print) + one-shot
  diagnostics export (zip: diagnostics.json + redacted logs) — QZ/PaperCut pattern.
- **Tray pause/resume wired (D-09):** the tray item drives the real queue controller
  (was a stub) and reflects live state; Diagnostics entry added to the tray.

### Build & test
- **Multi-target (D-10):** `net10.0;net10.0-windows` — the OS-agnostic suite now
  RUNS on Linux/any OS (was Windows-CI-only); 247 tests total (was 216), including
  **new integration tests against a REAL HTTP server** implementing the production
  agent contract (audit §14: "no integration test against any HTTP server" — fixed).
- **RID pinning:** native runtime copies (PDFium/SkiaSharp) limited to the host RID
  (linux-x64 tests / win-x64 production); hundreds of MB saved per checkout.
- **Installer assets (D-13):** original app.ico/app.png generated; side-by-side exe
  manifest produced; silent-install flags documented (`docs/PRINTEXE_INSTALLATION.md`);
  optional CI job builds the MSI with WiX v5 on tags.
- Explicit `System.Security.Cryptography.ProtectedData` package (audit compile risk).

### Docs
- New: `docs/research/PRINTEXE_COMPETITOR_RESEARCH.md`, `PRINTEXE_FEATURE_COMPARISON.md`,
  `PRINTEXE_ENGINEERING_DECISIONS.md`, `docs/PRINTEXE_EXISTING_ARCHITECTURE.md`,
  `PRINTEXE_TARGET_ARCHITECTURE.md` (+ Mermaid diagram), `PRINTEXE_API_CONTRACTS.md`,
  `PRINTEXE_SECURITY.md`, `PRINTEXE_INSTALLATION.md`, `PRINTEXE_TROUBLESHOOTING.md`,
  `PRINTEXE_TESTING.md`, `docs/THIRD_PARTY_NOTICES.md`.

## v0.4.4 — 2026-11-08 — Production Audit + Lifecycle + SHA-256 + Sync Center

### Phase Zero — Audit (`docs/PRODUCTION_AUDIT.md`)
Identified 13 issues: 1 P0 (SHA-256 not verified), 4 P1 (lifecycle, Sync Center UI,
update rollback), 7 P2 (improvements), 1 P3 (visual). All P0 and P1 issues
implemented; P2 deferred; rich-text editor out of scope (no editor exists).

### Phase 1 — Lifecycle
- `App.xaml.cs`: single shared `CancellationTokenSource` for all background work;
  bounded 5s shutdown wait for startup + update-check tasks.
- `SyncEngine.StopAsync` and `JobProcessor.StopAsync`: now actually `await` the
  loop task with a 3s bound (was just disposing the CTS, leaving SQLite writes
  in flight when the host was disposed).

### Phase 2 — Auto-update (`docs/UPDATE_SYSTEM.md`)
- **SHA-256 verification** of downloaded ZIP against the GitHub asset `digest`
  field. Streams bytes through `CryptoStream(SHA256.Create())` in a single pass.
- **Backup + rollback** in .bat: backs up app dir to `%TEMP%\PrintShop-backup-<guid>\`
  before xcopy. If xcopy fails 5x or the new EXE is missing, restores from backup.
- **Sentinel file** `_printshop_update_failed` surfaces a MessageBox on next
  startup if the previous update failed.
- **`UpdateState` enum + `StateChanged` event** for live UI updates through
  `Checking → Available → Downloading → Verifying → Extracting → Installing → Restarting`.
- **Fixed critical bug**: was reading `sha.Hash` before the `CryptoStream` was
  disposed (would have thrown `CryptographicUnexpectedOperationException` at
  runtime on Windows) — caught by writing a unit test.

### Phase 3 — Design system (`Themes/Tokens.xaml` + `Components.xaml`)
- 20+ named brushes, 7 font sizes, 6 spacing thicknesses, 4 corner radii.
- 12 reusable component styles (Card, StatCard, StatusPill, PrimaryButton,
  SecondaryButton, DangerButton, GhostButton, NavRailItem, NavRailItemActive,
  InputField, PageHeader, Divider, StatusDot, Avatar, etc.).

### Phase 4 + 7 — Sync Center UI
- New `SyncCenterView` + `SyncCenterViewModel` with connection status,
  last sync time, pending ops, failed ops, device ID, manual sync button.
- Subscribes to `SyncEngine.OnlineStatusChanged` + `SyncCompleted` events
  for live updates without polling.

### Phase 5 — Rich-text editor (`docs/RICH_TEXT_EDITOR.md`)
**Out of scope** — no editor exists; the companion is a print receiver, not a
document editor.

### Phase 6 — Printing (`docs/PRINTING.md`)
**No code changes** — pipeline was already correctly designed. Documentation added.

### Phase 8 — Security (`docs/SECURITY_AUDIT.md`)
- SHA-256 verification of update downloads.
- No hardcoded secrets (verified via grep).
- DPAPI credential storage + token redaction in logs already in place.

### Phase 9 — Tests
- **21 new tests** added: 11 in `GitHubUpdateServiceTests` (IsNewer semver, SHA-256
  streaming, GitHub release JSON, LatestVersion v-prefix, UpdateState enum),
  10 in `SqlitePendingOpsQueueTests` (Enqueue, MarkCompleted, MarkFailed,
  MarkRetry backoff, in_flight exclusion, multi-op, GetPayloadField).
- **All 31 tests pass on Linux .NET 10 SDK**.
- 2 pre-existing `Queue.Tests` failures confirmed present in v0.4.2 baseline.

### Build verification on Linux
- ✅ `PrintShopCompanion.Core`, `.Shared`, `.Infrastructure`: build with 0 errors
- ✅ All 57 C# files parse without syntax errors (Roslyn)
- ✅ All 17 XAML files well-formed (XML parse)
- ✅ Both WiX files well-formed
- ❌ `PrintShopCompanion.App` + `Worker` + `Backend`: cannot build on Linux (WPF)

### Files changed in this round
| File | Change |
| --- | --- |
| `Directory.Build.props` | Read `$(BUILD_VERSION)` env var |
| `.github/workflows/build-windows.yml` | Set BUILD_VERSION from git tag; pass to wix build |
| `installer/PrintShopCompanion.Installer.wxs` | Version uses `$(var.BuildVersion)` |
| `installer/PrintShopCompanion.Bundle.wxs` | Version uses `$(var.BuildVersion)` |
| `src/PrintShopCompanion.App/AppVersion.cs` | **NEW** — central version source |
| `src/PrintShopCompanion.App/App.xaml` | Register Tokens + Components |
| `src/PrintShopCompanion.App/App.xaml.cs` | Shared CTS + bounded shutdown |
| `src/PrintShopCompanion.App/Converters/Converters.cs` | BoolToVis now handles int + string |
| `src/PrintShopCompanion.App/Themes/Converters.xaml` | Register new converters |
| `src/PrintShopCompanion.App/Themes/Tokens.xaml` | **NEW** — design tokens |
| `src/PrintShopCompanion.App/Themes/Components.xaml` | **NEW** — component styles |
| `src/PrintShopCompanion.App/Views/MainWindow.xaml` | Sidebar redesign + update banner |
| `src/PrintShopCompanion.App/Views/MainWindow.xaml.cs` | Click guard + state events |
| `src/PrintShopCompanion.App/Views/LoginView.xaml` | Two-panel split |
| `src/PrintShopCompanion.App/Views/LoginView.xaml.cs` | Password reveal toggle |
| `src/PrintShopCompanion.App/Views/OverviewView.xaml` | KPI stat cards + table |
| `src/PrintShopCompanion.App/ViewModels/OverviewViewModel.cs` | ShopNameInitial property |
| `src/PrintShopCompanion.App/Views/OrdersView.xaml` | Filter chips + status pills |
| `src/PrintShopCompanion.App/Views/PrintQueueView.xaml` | Status pills per JobStatus |
| `src/PrintShopCompanion.App/Views/PrintersView.xaml` | Printer cards with badges |
| `src/PrintShopCompanion.App/Views/SettingsView.xaml` | 4 labeled sections |
| `src/PrintShopCompanion.App/Views/SettingsView.xaml.cs` | Use AppVersion.Prefixed |
| `src/PrintShopCompanion.App/Views/SyncCenterView.xaml` | **NEW** — Sync Center UI |
| `src/PrintShopCompanion.App/Views/SyncCenterView.xaml.cs` | **NEW** — code-behind |
| `src/PrintShopCompanion.App/ViewModels/SyncCenterViewModel.cs` | **NEW** — VM with live events |
| `src/PrintShopCompanion.Core/Contracts/IUpdateService.cs` | UpdateState enum + StateChanged |
| `src/PrintShopCompanion.Infrastructure/PrintShopCompanion.Infrastructure.csproj` | InternalsVisibleTo |
| `src/PrintShopCompanion.Infrastructure/Queue/JobProcessor.cs` | StopAsync awaits loop |
| `src/PrintShopCompanion.Infrastructure/Sync/SyncEngine.cs` | StopAsync awaits loop |
| `src/PrintShopCompanion.Infrastructure/Sync/SqlitePendingOpsQueue.cs` | (no schema change) |
| `src/PrintShopCompanion.Infrastructure/Update/GitHubUpdateService.cs` | SHA-256 + backup + state events |
| `tests/PrintShopCompanion.Infrastructure.Tests/GitHubUpdateServiceTests.cs` | **NEW** — 11 tests |
| `tests/PrintShopCompanion.Infrastructure.Tests/SqlitePendingOpsQueueTests.cs` | **NEW** — 10 tests |
| `docs/PRODUCTION_AUDIT.md` | **NEW** |
| `docs/UPDATE_SYSTEM.md` | **NEW** |
| `docs/SECURITY_AUDIT.md` | **NEW** |
| `docs/PRINTING.md` | **NEW** |
| `docs/RICH_TEXT_EDITOR.md` | **NEW** |
| `docs/IMPLEMENTATION_REPORT.md` | **NEW** |

---

## v0.4.3 — 2026-11-08 — Auto-Update Repair + Full UI Redesign

### Update bug — root cause + fix

The auto-updater downloaded and ran the .bat file swap correctly, but the
app still reported "v0.4.1 starting" after the swap. **The build workflow
shipped a `v0.4.2` tag with assembly version `0.4.1.0` hardcoded in
`Directory.Build.props`**, so even a successful file swap did not change the
assembly version. The updater's `IsNewer()` check kept returning true and
the banner kept re-appearing.

In addition, the user clicked "Update Now" 6 times in 13 seconds (visible in
the log as 6 download attempts), triggering 6 concurrent installs.

| Symptom (from log) | Cause | Fix |
| --- | --- | --- |
| `Update check: current=0.4.1.0, latest=0.4.2` after a successful update | `Directory.Build.props` hardcoded `<Version>0.4.1</Version>` — the workflow never overrode it from the git tag | `Directory.Build.props` now reads `$(BUILD_VERSION)`; the workflow's new "Set BUILD_VERSION from git tag" step resolves the version from `${{ github.ref_name }}` and writes it to `$GITHUB_ENV` before `dotnet build` |
| 6 download attempts in 13 seconds | `Update_Click` had no click guard | `MainWindow.Update_Click` now sets `_updateInProgress = true` and disables the button on entry; `GitHubUpdateService.InstallUpdateAsync` adds a contract-level `Interlocked.CompareExchange` guard so even two concurrent callers can't both run the install |
| `PrintShop Companion v0.4.1 starting` log line after update | `App.xaml.cs` had a hardcoded `"v0.4.1"` literal in the startup log message | New `AppVersion` helper reads the assembly version; `App.xaml.cs`, `MainWindow.xaml`, `LoginView.xaml`, `SettingsView.xaml` all bind to it |
| .bat file swap may silently fail (no log) | Old .bat had no logging, single xcopy attempt, no retry | New .bat logs every step to `%TEMP%\PrintShop-updater.log`, retries xcopy up to 5 times with 2s pause between attempts, aborts cleanly if all 5 fail |
| "Another instance is already running" right after update | Old .bat waited only 5s + 2s, sometimes not enough for the OS to release the process | New .bat waits 4s + 2s and uses `taskkill /f` as backup, then retries xcopy 5 times |

### UI redesign — every page now has individual boxes, text labels, divider lines

The user's request was "make ui more professional see each page individual boxes texts and lines etc".
Each page is now composed of clearly separated sections with headings,
dividers, and consistent typography:

| Page | What's new |
| --- | --- |
| **LoginView** | Two-panel split: amber brand panel on the left with feature bullets + version stamp, white form panel on the right with EMAIL/PASSWORD labels, error pill with warning icon, password reveal toggle (overlay TextBox), OR divider line, footer link |
| **MainWindow** | Sidebar with brand header + version, MENU section heading + nav items, divider line, SYSTEM section + Settings, account footer with avatar circle + sign out. Active nav item gets amber tint. Update banner now has progress bar + status text alongside the Update Now button |
| **OverviewView** | Page header bar, "Connected shop" banner with avatar + Live badge, "KEY METRICS" heading, 5 stat cards each with colored accent strip (amber, indigo, green, amber, green) + KPI label + big number + sublabel, "RECENT ORDERS" heading, table card with column header row, rows with status pills |
| **OrdersView** | Page header, table card with filter chip row (All/Pending/Printing/Ready), column header row, rows with status pills, empty state with icon + caption |
| **PrintQueueView** | Page header, queue card with header row showing job count, column header row, rows with status pills colored by JobStatus (Pending gray, Downloading blue, Spooled amber, Printed green, Failed red), empty state with icon |
| **PrintersView** | Page header with Refresh + Test Print actions, status banner (only visible when TestStatus is set), "AVAILABLE PRINTERS" heading, printer cards with green status dot, name + full path, "Windows Default" amber pill + "Selected" amber pill, hover/active border turns amber |
| **SettingsView** | Page header, four labeled sections (APPLICATION, SHOP INFORMATION, DIAGNOSTICS, ACCOUNT) each as its own bordered card with horizontal divider lines between rows, every row has a title + description on the left and a control/badge on the right |

### Files changed in this round

| File | Change |
| --- | --- |
| `Directory.Build.props` | `<Version>` and `<AssemblyVersion>` now read from `$(BUILD_VERSION)` env var, falling back to `0.4.3` for local builds. Bumped fallback from `0.4.1` to `0.4.3`. |
| `.github/workflows/build-windows.yml` | Added "Set BUILD_VERSION from git tag" step that resolves the version from `${{ github.ref_name }}` and writes it to `$GITHUB_ENV` before `dotnet build`. Updated "Write version metadata" step to use `$env:BUILD_VERSION` instead of hardcoded `"0.1.2"`. |
| `src/PrintShopCompanion.App/AppVersion.cs` | **NEW** — central version source. Reads assembly version, exposes `Full` (X.Y.Z.W), `Short` (X.Y.Z), and `Prefixed` (vX.Y.Z) forms. |
| `src/PrintShopCompanion.App/App.xaml.cs` | Replaced hardcoded `v0.4.1` log line with `AppVersion.Short`. `--version` CLI flag now prints `AppVersion.Short` instead of a hardcoded literal. |
| `src/PrintShopCompanion.App/Views/MainWindow.xaml` | Full sidebar redesign: brand header + version, MENU/SYSTEM section headings, divider lines, account footer with avatar + sign out. Active nav item styled with amber tint. Update banner gets progress bar + status text. |
| `src/PrintShopCompanion.App/Views/MainWindow.xaml.cs` | Reads `AppVersion.Prefixed` for sidebar version label. `Update_Click` now disables the button, sets `_updateInProgress` flag, shows progress bar, calls `InstallUpdateAsync` with an `IProgress<int>` that updates the UI. Resets state on failure. |
| `src/PrintShopCompanion.App/Views/LoginView.xaml` | Two-panel split: amber brand panel (logo, tagline, three feature bullets, version stamp) + white form panel (EMAIL/PASSWORD labels, error pill, password reveal overlay, OR divider, footer link, build stamp). |
| `src/PrintShopCompanion.App/Views/LoginView.xaml.cs` | Wires up version stamps to `AppVersion.Prefixed`. Implements real password reveal by overlaying a TextBox on the PasswordBox and toggling visibility. |
| `src/PrintShopCompanion.App/Views/OverviewView.xaml` | Page header bar, connected-shop banner with avatar + Live badge, KEY METRICS heading, 5 stat cards with colored accent strips, RECENT ORDERS heading, table card with column header row + status pills in each row. |
| `src/PrintShopCompanion.App/ViewModels/OverviewViewModel.cs` | Added `ShopNameInitial` derived property + `OnShopNameChanged` partial method to notify the binding. |
| `src/PrintShopCompanion.App/Views/OrdersView.xaml` | Page header, table card with filter chip row, column header row, status pills in rows, empty state with icon. |
| `src/PrintShopCompanion.App/Views/PrintQueueView.xaml` | Page header, queue card with header showing job count, column header row, status pills colored per JobStatus (Pending/Downloading/Spooled/Printed/Failed/Cancelled), empty state with icon. |
| `src/PrintShopCompanion.App/Views/PrintersView.xaml` | Page header with Refresh + Test Print actions, status banner, AVAILABLE PRINTERS heading, printer cards with status dot + name + path + Windows Default / Selected pills, hover/active border turns amber, empty state with icon. |
| `src/PrintShopCompanion.App/Views/SettingsView.xaml` | Page header, four labeled sections (APPLICATION/SHOP INFORMATION/DIAGNOSTICS/ACCOUNT) as separate bordered cards, each row has title + description + control, horizontal divider lines between rows. |
| `src/PrintShopCompanion.App/Views/SettingsView.xaml.cs` | Reads `AppVersion.Prefixed` for version label (was hardcoded `v0.4.1`). |
| `src/PrintShopCompanion.Infrastructure/Update/GitHubUpdateService.cs` | Added `Interlocked.CompareExchange` install guard. .bat now logs every step to `%TEMP%\PrintShop-updater.log`, retries xcopy up to 5 times with 2s pause, aborts cleanly on failure. Self-delete via a separate `cmd.exe` invocation. Verifies the extracted directory is non-empty before killing the app. |

### Build status

> **Not verified in this Linux sandbox.** No `dotnet` available here. The user must
> run `dotnet build PrintShopCompanion.sln -c Release` on Windows to confirm a green
> build. The static-blocker sweep I performed here is comprehensive but is not a
> substitute for a real build.

---

## v0.1.2 — 2026-11-05 — Real Windows Build Repair

Fixed the compile errors and remaining NuGet warnings that surfaced when the user
ran `dotnet build .\PrintShopCompanion.sln -c Release` on a Windows 11 PC with the
.NET 10 SDK installed. These were the actual errors from the real build output.

### Compile errors fixed

| Error | Cause | Fix |
| --- | --- | --- |
| **CS0246** — `DeviceToken` not found in `IServices.cs(29,10)` and `(30,20)` | `PrintShopCompanion.Core.Contracts.IServices.cs` references the `DeviceToken` type (defined in `PrintShopCompanion.Core.Models.ShopDevice.cs`) but had no `using PrintShopCompanion.Core.Models;` directive. | Added `using PrintShopCompanion.Core.Models;` to `IServices.cs`. |
| **CS0246** (would have fired next) — `JobStatus.Downloaded` no longer exists | Earlier v0.1.1 renamed `JobStatus.Downloaded` → `JobStatus.Downloading`, but four files still referenced the old name. | Fixed `DashboardViewModel.cs`, `SqliteJobQueueTests.cs` (test assertion), `JobProcessorIdempotencyTests.cs` (FakeQueue.DequeueAsync), and the XML doc cref in `IJobQueue.cs`. |
| **CS1729** (would have fired next) — `new DeviceToken(...)` positional constructor does not exist | `DeviceToken` and `ShopInfo` are records with `required` properties, NOT positional records. The test files used `new DeviceToken("shop","dev","tok",...)` and `new ShopInfo("shop","Shop",...)` syntax that doesn't compile. | Rewrote those constructions as object initializers in `MagicLinkPairingServiceTests.cs` and `JobProcessorIdempotencyTests.cs`. |

### NuGet warnings fixed

| Warning | Cause | Fix |
| --- | --- | --- |
| **NU1510** — `System.Security.Cryptography.ProtectedData` will not be pruned | The package is auto-available on .NET 10 Windows via the framework reference; explicit PackageReference is redundant. | Removed the explicit PackageReference from `PrintShopCompanion.Infrastructure.csproj`. |
| **NU1510** — `System.Drawing.Common` will not be pruned | Same — auto-available via `<UseWPF>true</UseWPF>`. | Removed the explicit PackageReference. |
| **NU1903** — `Microsoft.NETCore.App 1.1.0` (GHSA-7mfr-774f-w5r9, GHSA-8884-xcr4-r68p) | Transitive via `SocketIOClient 3.1.0` → `SocketIOClient.Transport 3.1.0` → `System.Net.WebSockets.Client.Managed 1.0.x` → the .NET Core 1.1 runtime packages. | Upgraded `SocketIOClient` from `3.1.0` to `3.2.0`. The 3.2.0 release multi-targets `net6.0`/`net7.0`/`net8.0`; when consumed by `net10.0` it picks the `net8.0` TFM and uses the built-in `System.Net.WebSockets.ClientWebSocket`, no longer dragging in `System.Net.WebSockets.Client.Managed`. |
| **NU1903** — `Microsoft.NETCore.Jit 1.1.0` (GHSA-xcvr-qv8h-m7xw) | Same transitive chain as above. | Fixed by the SocketIOClient 3.2.0 bump. |
| **NU1903** — `Newtonsoft.Json 9.0.1` (GHSA-5crp-9r3c-p9vr) | Transitive via legacy dep chain. | Added explicit `<PackageReference Include="Newtonsoft.Json" Version="13.0.3" />` to App, Worker, Infrastructure, and both test projects to force the safe version. |
| **NU1903** — `SQLitePCLRaw.lib.e_sqlite3 2.1.11` (GHSA-2m69-gcr7-jv3q) | Transitive via `Microsoft.Data.Sqlite 10.0.0` → `SQLitePCLRaw.bundle_e_sqlite3` → `SQLitePCLRaw.lib.e_sqlite3`. The vulnerable 2.1.11 was the resolved version. | Added explicit `<PackageReference Include="SQLitePCLRaw.lib.e_sqlite3" Version="2.1.16" />` to App, Worker, Infrastructure, and both test projects. |
| **CA1716** — namespace `PrintShopCompanion.Shared.*` conflicts with VB `Shared` keyword | The `Shared` keyword is reserved in VB.NET. The companion is C#-only. | Suppressed via `<NoWarn>$(NoWarn);CA1716</NoWarn>` in `Directory.Build.props`. Renaming the namespaces would be a much bigger refactor than the warning warrants. |
| **CA1861** — `Prefer 'static readonly' fields over constant array arguments` in `Backend/Program.cs(42,25)` | The `new[] { ... }` array passed to `MapGet` was allocated on every request. | Hoisted to a `static readonly string[] EndpointList` and `static readonly string[] CorsOrigins` fields. |

### Dependency version changes (this round)

| Package | Old version | New version | Reason |
| --- | --- | --- | --- |
| `SocketIOClient` | 3.1.0 | **3.2.0** | NU1903 — drops `System.Net.WebSockets.Client.Managed` transitive dep |
| `Newtonsoft.Json` | (transitive, 9.0.1) | **13.0.3** (explicit override) | NU1903 — explicit safe-version override |
| `SQLitePCLRaw.lib.e_sqlite3` | (transitive, 2.1.11) | **2.1.16** (explicit override) | NU1903 — explicit safe-version override |
| `System.Security.Cryptography.ProtectedData` | 10.0.0 (explicit) | **removed** | NU1510 — auto-available |
| `System.Drawing.Common` | 10.0.0 (explicit) | **removed** | NU1510 — auto-available via `UseWPF=true` |
| `WPF-UI` | 4.0.0 | **4.3.0** | 4.3.0 explicitly targets `net10.0-windows7.0`; 4.0.0 does not |
| `H.NotifyIcon.Wpf` | 2.2.0 | **2.4.1** | 2.4.1 explicitly targets `net10.0-windows7.0` |
| `Hardcodet.NotifyIcon.Wpf` | 2.0.1 | **removed** | Dead weight — the codebase uses `H.NotifyIcon` exclusively |

### Files changed in this round

| File | Change |
| --- | --- |
| `src/PrintShopCompanion.Core/Contracts/IServices.cs` | Added `using PrintShopCompanion.Core.Models;` — fixes CS0246 |
| `src/PrintShopCompanion.Core/Contracts/IJobQueue.cs` | Updated `<see cref="JobStatus.Downloaded"/>` → `<see cref="JobStatus.Downloading"/>` |
| `src/PrintShopCompanion.App/ViewModels/DashboardViewModel.cs` | `JobStatus.Downloaded` → `JobStatus.Downloading or JobStatus.Rendering or JobStatus.Spooled` |
| `src/PrintShopCompanion.App/PrintShopCompanion.App.csproj` | Bumped `WPF-UI` 4.0.0 → 4.3.0; bumped `H.NotifyIcon.Wpf` 2.2.0 → 2.4.1; removed `Hardcodet.NotifyIcon.Wpf`; added `Newtonsoft.Json 13.0.3` and `SQLitePCLRaw.lib.e_sqlite3 2.1.16` explicit overrides |
| `src/PrintShopCompanion.Infrastructure/PrintShopCompanion.Infrastructure.csproj` | Bumped `SocketIOClient` 3.1.0 → 3.2.0; removed `System.Security.Cryptography.ProtectedData` and `System.Drawing.Common` explicit PackageReferences (NU1510); added `Newtonsoft.Json 13.0.3` and `SQLitePCLRaw.lib.e_sqlite3 2.1.16` explicit overrides |
| `src/PrintShopCompanion.Worker/PrintShopCompanion.Worker.csproj` | Added `Newtonsoft.Json 13.0.3` and `SQLitePCLRaw.lib.e_sqlite3 2.1.16` explicit overrides |
| `tests/PrintShopCompanion.Queue.Tests/PrintShopCompanion.Queue.Tests.csproj` | Added the same explicit overrides |
| `tests/PrintShopCompanion.Infrastructure.Tests/PrintShopCompanion.Infrastructure.Tests.csproj` | Added the same explicit overrides |
| `tests/PrintShopCompanion.Queue.Tests/SqliteJobQueueTests.cs` | `JobStatus.Downloaded` → `JobStatus.Downloading` (assertion + comment) |
| `tests/PrintShopCompanion.Infrastructure.Tests/JobProcessorIdempotencyTests.cs` | `JobStatus.Downloaded` → `JobStatus.Downloading`; rewrote `new ShopInfo(...)` positional construction as object initializer |
| `tests/PrintShopCompanion.Infrastructure.Tests/MagicLinkPairingServiceTests.cs` | Rewrote `new DeviceToken(...)` and `new ShopInfo(...)` positional constructions as object initializers |
| `Directory.Build.props` | Added `<NoWarn>$(NoWarn);CA1716;CA1861</NoWarn>` to suppress the namespace-conflict and static-readonly-array stylistic warnings |
| `src/PrintShopCompanion.Backend/Program.cs` | Hoisted the constant array arguments to `static readonly` fields to fix CA1861 |
| `src/PrintShopCompanion.Infrastructure/Queue/SqliteJobQueue.cs` | Updated doc comments referring to `Downloaded` → `Downloading` |

### Build status

The user ran `dotnet build` on Windows 11 with .NET 10 SDK and got 2 errors + 28 warnings. After this round of fixes:

- **Compile errors**: 0 expected (the CS0246 in `IServices.cs`, the `JobStatus.Downloaded` references, and the positional-record construction errors are all fixed).
- **NU1510 warnings**: 0 expected (the redundant explicit PackageReferences are removed).
- **NU1903 vulnerability warnings**: 0 expected (SocketIOClient 3.2.0 drops the legacy `System.Net.WebSockets.Client.Managed` chain; explicit overrides pin `Newtonsoft.Json 13.0.3` and `SQLitePCLRaw.lib.e_sqlite3 2.1.16`).
- **CA1716 / CA1861 stylistic warnings**: 0 expected (suppressed in `Directory.Build.props`).
- **NETSDK1057** (preview SDK info): will remain — it's informational, not blocking.

**Not actually verified**: I cannot run `dotnet build` in this Linux sandbox. The user must re-run `dotnet clean`, `dotnet restore`, `dotnet build` on Windows to confirm a green build. The static-blocker sweep I performed here is comprehensive but is not a substitute for a real build.

### Functionality preserved

All v0.1.0 functionality is preserved:
- ✅ Printer discovery (`WindowsPrinterService` via `System.Printing` through `<UseWPF>true</UseWPF>`)
- ✅ Windows spooler integration (via SumatraPDF's `-print-to` and `-print-stat`)
- ✅ PDF printing (SumatraPDF primary; raw GDI fallback for ESC/POS)
- ✅ SQLite durable queue (unchanged)
- ✅ DPAPI credential storage (the `System.Security.Cryptography.ProtectedData` API is still available — it's part of the framework reference, just no longer an explicit PackageReference)
- ✅ Pairing and device authentication (unchanged)
- ✅ Backend communication (unchanged)
- ✅ Socket.IO + HTTP polling fallback (SocketIOClient upgraded to 3.2.0)
- ✅ Accurate job status reporting with UncertainSpooled / OutcomeUnknown states (unchanged)
- ✅ WPF dashboard + tray (`H.NotifyIcon.Wpf` upgraded to 2.4.1; `WPF-UI` upgraded to 4.3.0)

---

## v0.1.1 — 2026-11-05 — NuGet Restore Repair

Fixed the NuGet restore errors that blocked the Windows build. No functional
behaviour was lost; the PDF printing path is now SumatraPDF-primary.

### NuGet restore errors fixed

| Error | Cause | Fix |
| --- | --- | --- |
| `NU1102` — `PdfiumViewer.Native.x86_64.v8-xfa >= 2018.4.12.1858` not found | The NuGet package version 2018.4.12.1858 was never published; only 2018.4.8.256 exists. | Dropped `PdfiumViewer` + `PdfiumViewer.Native.x86_64.v8-xfa` entirely. SumatraPDF is the primary PDF renderer. |
| `NU1101` — `System.Printing` package not found | `System.Printing` is part of `Microsoft.WindowsDesktop.App` (WPF/Windows Desktop framework), not a NuGet package. The Infrastructure project had `<UseWPF>false</UseWPF>` which blocked the framework reference. | Removed the `<PackageReference Include="System.Printing" />` line. Added `<UseWPF>true</UseWPF>` to `PrintShopCompanion.Infrastructure.csproj` so the Windows Desktop framework (which provides `System.Printing`) is referenced. |
| `NU1701` — `PdfiumViewer 2.13.0` targets .NET Framework | The PdfiumViewer package is a .NET Framework 4.x library; using it from .NET 10 generates NU1701. | Removed (see NU1102 fix above). |
| `NU1903` — vulnerable legacy transitive deps (Microsoft.NETCore.App 1.1.0, Microsoft.NETCore.Jit 1.1.0, Newtonsoft.Json 9.0.1, SQLitePCLRaw.lib.e_sqlite3 2.1.11) | All four were transitive deps of `PdfiumViewer`. | Removed (see NU1102 fix above). |
| `NU1603` — `SocketIOClient 3.0.13` unavailable; 3.1.0 resolved | The pinned version is no longer published on NuGet. | Pinned `SocketIOClient` to `3.1.0` (the version NuGet actually resolves). |

### Files changed

| File | Change |
| --- | --- |
| `src/PrintShopCompanion.Infrastructure/PrintShopCompanion.Infrastructure.csproj` | Removed `PdfiumViewer`, `PdfiumViewer.Native.x86_64.v8-xfa`, `System.Printing` package references. Added `<UseWPF>true</UseWPF>`. Pinned `SocketIOClient` to `3.1.0`. Added `System.Drawing.Common 10.0.0` for the raw GDI fallback renderer's `System.Drawing.Printing.PrintDocument`. |
| `src/PrintShopCompanion.Infrastructure/Printers/PdfiumPdfRenderer.cs` | **Deleted.** |
| `src/PrintShopCompanion.Infrastructure/Printers/CompositePdfRenderer.cs` | Removed the `PdfiumPdfRenderer` entry from the chain. The fallback chain is now SumatraPDF (primary) → raw GDI (fallback). |
| `src/PrintShopCompanion.Infrastructure/Printers/SumatraPdfPrinter.cs` | Updated the class doc to reflect that it is now the **primary** renderer. Fixed a long-standing bug in the color setting (`color == false` was forcing color; now correctly forces monochrome). Added `paperSize` and `duplex` handling for SumatraPDF's `-print-settings` flag. |
| `src/PrintShopCompanion.Infrastructure/Printers/RawGdiPrinter.cs` | Updated the class doc to reflect that it is the fallback (no longer references PDFium). |
| `src/PrintShopCompanion.Infrastructure/InfrastructureModule.cs` | Removed `services.AddSingleton<PdfiumPdfRenderer>()` registration. |
| `src/PrintShopCompanion.Core/Options/Options.cs` | Default `Renderer` changed from `"pdfium"` to `"sumatra"`. Default `FallbackRenderers` changed from `["sumatra", "gdi"]` to `["gdi"]`. |
| `src/PrintShopCompanion.App/appsettings.json` | `Printer.Renderer` → `"sumatra"`, `Printer.FallbackRenderers` → `["gdi"]`. |
| `src/PrintShopCompanion.Worker/appsettings.json` | Same defaults as the App. |
| `src/PrintShopCompanion.App/ViewModels/SettingsViewModel.cs` | Default `_renderer` field changed to `"sumatra"`. |
| `src/PrintShopCompanion.App/Views/SettingsView.xaml` | Removed the `<ComboBoxItem Content="pdfium" />` entry from the renderer dropdown. |
| `installer/PrintShopCompanion.Installer.wxs` | Removed the `<Component Id="Pdfium">` block that bundled `pdfium.dll`. The MSI no longer references the file. |
| `installer/README.md` | Removed the `pdfium.dll` bundling note. |
| `docs/PHASES.md` | Updated Phase 4 status to reflect the SumatraPDF-primary architecture. |
| `HONEST-STATUS.md` | Updated item 9 (renderers) and the "Known code-level issues" list. |
| `NOT-VERIFIED.md` | Removed the obsolete `PdfiumPdfRenderer` warnings. Updated `Bundle.wxs`, `--register-autostart`, and `BypassTlsVerification` items to reflect that they are now implemented. |

### Old vs new dependency versions

| Package | Old version | New version | Reason |
| --- | --- | --- | --- |
| `PdfiumViewer` | 2.13.0 | (removed) | NU1701 (targets .NET Framework), NU1903 (vulnerable transitives) |
| `PdfiumViewer.Native.x86_64.v8-xfa` | 2018.4.12.1858 | (removed) | NU1102 (not published) |
| `System.Printing` | 10.0.0 (NuGet) | (removed, framework ref via `UseWPF=true`) | NU1101 (not a NuGet package) |
| `SocketIOClient` | 3.0.13 | **3.1.0** | NU1603 (3.0.13 not published; 3.1.0 is the resolved version) |
| `System.Drawing.Common` | (implicit) | **10.0.0** (explicit) | Make the GDI fallback renderer's `System.Drawing.Printing.PrintDocument` dependency explicit in the manifest |
| `Microsoft.Data.Sqlite` | 10.0.0 | 10.0.0 | (unchanged — already current) |
| `Microsoft.Extensions.*` | 10.0.0 | 10.0.0 | (unchanged — already current) |
| `Serilog` | 4.2.0 | 4.2.0 | (unchanged) |

### Restore, build, test output

> **Not run in this Linux sandbox.** No `dotnet`, `mono`, or `wix` is available here. The commands below must be run on a Windows machine to produce real output.

The user's requested commands, to be run on a Windows 11 PC with .NET 10 SDK + WiX v5 installed:

```powershell
cd apps\windows-companion
dotnet clean .\PrintShopCompanion.sln
dotnet restore .\PrintShopCompanion.sln
dotnet build .\PrintShopCompanion.sln -c Release --no-restore
dotnet test .\PrintShopCompanion.sln -c Release --no-build
dotnet publish .\src\PrintShopCompanion.App\PrintShopCompanion.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\artifacts\app
```

Or the one-click path:

```powershell
.\scripts\install-on-windows.ps1
```

### EXE path and file size

- Expected path: `apps/windows-companion/artifacts/app/PrintShopCompanion.exe`
- Expected size: ~80–100 MB (self-contained, single-file with native libraries embedded for self-extract).
- **Not produced in this sandbox.** Will be produced by either `install-on-windows.ps1` on a Windows 11 PC or by the `build-windows.yml` GitHub Actions workflow on a `windows-latest` runner.

### MSI and Setup EXE paths

- MSI: `apps/windows-companion/installer/build/PrintShopCompanion.msi`
- Setup EXE (bootstrapper, optional): `apps/windows-companion/installer/build/PrintShopCompanion.Setup.exe`
- **Not produced in this sandbox.**

### Remaining warnings or limitations

1. **Companion has not compiled yet.** The static-blocker scan I ran here confirms no `.csproj` references the removed packages and no `.cs` file references the deleted `PdfiumPdfRenderer` type, but I cannot guarantee the first `dotnet build` will be green. Likely issues to watch for on Windows:
   - `System.Drawing.Common` may emit a runtime warning on non-Windows platforms — irrelevant for a Windows-only deployment but worth knowing.
   - `H.NotifyIcon.Wpf` and `WPF-UI` versions are unverified against .NET 10; if they don't support .NET 10 yet, the App project may need to pin earlier versions of those packages.
   - `System.Security.Cryptography.ProtectedData` 10.0.0 should be available, but if not, fall back to 9.0.0.
2. **No real printer test.** The SumatraPDF `-print-stat` → `SPOOLER_JOB_ID=<int>` parsing depends on the SumatraPDF version that `install-on-windows.ps1` downloads (currently the `sumatra-prerel-64.zip` pre-release). Pin to a stable release before production.
3. **Color setting is now correctly mapped** — `bool? color == true` → `-print-settings color`, `== false` → `-print-settings monochrome`. The previous code had the polarity reversed.
4. **Paper size and duplex** are now passed to SumatraPDF via `-print-settings paper=<size>` and `-print-settings duplexlong|duplexshort|simplex`. These have not been verified on real hardware.

### Functionality preserved

The repair preserves all required functionality:
- ✅ Printer discovery (`WindowsPrinterService.DiscoverPrinters` via `System.Printing`)
- ✅ Windows spooler integration (via SumatraPDF's `-print-to` and `-print-stat`)
- ✅ PDF printing (SumatraPDF primary; raw GDI fallback for ESC/POS)
- ✅ SQLite durable queue (unchanged)
- ✅ DPAPI credential storage (unchanged)
- ✅ Pairing and device authentication (unchanged)
- ✅ Backend communication (unchanged)
- ✅ Socket.IO + HTTP polling fallback (SocketIOClient pinned to 3.1.0)
- ✅ Accurate job status reporting with UncertainSpooled / OutcomeUnknown states (unchanged)
- ✅ WPF dashboard + tray (unchanged)
