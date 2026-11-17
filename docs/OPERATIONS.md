# Operations Guide — PrintEXE (PrintShop Companion)

Internal reference for building, releasing, and operating the Windows client.
Secrets are never stored in this repository.

## Build

- Full solution: `dotnet build PrintShopCompanion.sln -c Release`
  (works on Windows and, via `EnableWindowsTargeting` in
  Directory.Build.props, also cross-compiles on Linux — but tests only RUN
  on Windows: `net10.0-windows` test hosts need the Windows desktop runtime).
- Tests: `dotnet test PrintShopCompanion.sln -c Release` on Windows.

## CI / Release

- Workflow: `.github/workflows/build-windows.yml` (windows-latest).
  Triggers: push/PR to `main`, version tags `v*`, manual dispatch.
  Stages: restore → build → **dotnet test (no continue-on-error)** →
  self-contained publish + native-DLL validation → ZIP + SHA-256SUMS +
  BUILD-INFO → artifact upload; release job (tag only) verifies the checksum
  round-trip and refuses to overwrite an existing release.
- Releasing a new version: commit on the release branch → tag `vX.Y.Z` →
  push the tag. `BUILD_VERSION` is taken from the tag into
  Directory.Build.props (assembly version matches the release).

## Configuration the app/Worker actually load

1. `appsettings.json` next to the exe (base).
2. `appsettings.Local.json` next to the exe (legacy per-install overrides).
3. `%AppData%\PrintShop\Companion\appsettings.Local.json` — per-user
   overrides written by Settings and printer selection
   (`LocalSettingsStore`; loaded with a RELATIVE path because
   `AddJsonFile` silently ignores absolute paths).

Key settings: `Backend:BaseUrl`, `Backend:ApiBasePath`,
`Printer:SelectedPrinter`, `Printer:Renderer`, `Storage:DeviceTokenPath`,
`App:*` (theme, autostart, tray).

## Device credential lifecycle

- Pairing: `printshop://pair?ticket=…` deep link (both schemes registered in
  HKCU by the installer) or manual ticket paste. The URI only PRE-FILLS the
  pairing form — an explicit user click pairs the device.
- Credential: DPAPI (CurrentUser) at `%AppData%\PrintEXE\device-token.dat`
  (or `Storage:DeviceTokenPath`), written atomically (.tmp/.bak/verify).
- Rotation: startup policy rotates credentials older than 30 days
  (POST /api/v1/companion/token/rotate; persist-before-transition; the old
  token stays valid for the server's 10-minute overlap, restricted to
  heartbeat/read-only/rotation-retry).
- Revocation (website disable/revoke) clears the in-memory credential
  immediately and returns the app to the Login screen.

## URI protocol

Installer (WiX, HKCU) registers BOTH `printshopcompanion://` (legacy) and
`printshop://` (emitted by the website's Add Device dialog) with
`shell\open\command "%1"`. NOTE: if the app is already running, a second
launch exits via the single-instance mutex and the URI is NOT forwarded —
the deep link must be clicked when the app is not running (documented
limitation; single-instance IPC is a planned fix).

## Troubleshooting

- `Not paired with a shop` → pair first (Step 2 of the setup guide); the
  lazy credential restore only helps when a credential exists on disk.
- Heartbeat online but no jobs → check the website's device row shows the
  same shop; jobs arrive by 2–3 s polling + realtime push.
- SQLITE_BUSY → mitigated by WAL + 5 s busy_timeout pragmas; if it recurs,
  check disk health and antivirus file locks on queue.db.
- Update installed but version unchanged → ensure the release was built from
  a proper `vX.Y.Z` tag (see BUILD-INFO.txt inside the ZIP).
