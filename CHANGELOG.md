# PrintShop Companion — Changelog

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
