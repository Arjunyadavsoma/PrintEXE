# PrintEXE — Build and Release

This document describes how to build, publish, package, and release
the PrintEXE Windows companion locally and via GitHub Actions.

## 1. Repository layout

| Path | Purpose |
|---|---|
| `PrintShopCompanion.sln` | Visual Studio / `dotnet` solution (8 projects) |
| `src/PrintShopCompanion.App/` | WPF application (entry point: `PrintShopCompanion.exe`) |
| `src/PrintShopCompanion.Infrastructure/` | HTTP clients, SQLite/SQLCipher queue, printers, sync, update |
| `src/PrintShopCompanion.Core/` | Domain models + contracts (no dependencies) |
| `src/PrintShopCompanion.Shared/` | DTOs shared with the mock backend |
| `src/PrintShopCompanion.Worker/` | Headless background service (future use) |
| `src/PrintShopCompanion.Backend/` | Mock backend for local development |
| `tests/PrintShopCompanion.Infrastructure.Tests/` | xUnit tests (41 tests) |
| `tests/PrintShopCompanion.Queue.Tests/` | xUnit tests (7 tests) |
| `Directory.Build.props` | Shared build settings; `BUILD_VERSION` env var overrides `<Version>` |
| `.github/workflows/build-windows.yml` | CI/CD pipeline |

## 2. Prerequisites

- **.NET 10 SDK** (`10.0.x`) — [download](https://dotnet.microsoft.com/download/dotnet/10.0)
- **Windows 10/11** for local publish (the app targets `net10.0-windows` + WPF)
- **7-Zip** (optional, for zipping the publish folder — Windows ships with
  `Compress-Archive` as a fallback)

The solution has **no** code-signing requirement for local builds. CI
signing is opt-in via the `PRINTSHOP_CODE_SIGN_PFX` /
`PRINTSHOP_CODE_SIGN_PWD` repository secrets.

## 3. Local build

```powershell
# From the repository root:
dotnet restore PrintShopCompanion.sln
dotnet build   PrintShopCompanion.sln -c Release
dotnet test    PrintShopCompanion.sln -c Release --no-build
```

`Directory.Build.props` sets `<Version>` to `0.4.7` by default. To
build with a specific version (e.g. to test the auto-updater), set
the `BUILD_VERSION` environment variable before building:

```powershell
$env:BUILD_VERSION = "0.6.0"
dotnet build PrintShopCompanion.sln -c Release
```

The assembly version is derived as `<BUILD_VERSION>.0` (e.g.
`0.6.0.0`), which is what `GitHubUpdateService.IsNewer()` compares
against.

## 4. Local publish

Publish a self-contained, folder distribution for `win-x64`:

```powershell
dotnet publish src/PrintShopCompanion.App/PrintShopCompanion.App.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=false `
  -o ./artifacts/app
```

**Why folder distribution (not single-file)?**

PrintEXE uses WPF + SQLCipher + native SQLite. The
`SQLitePCLRaw.bundle_e_sqlcipher` package ships native DLLs
(`e_sqlcipher.dll`, `e_sqlite3.dll`) that must load alongside the
managed assemblies. Single-file publishing with
`IncludeNativeLibrariesForSelfExtract=true` extracts these to a
temp folder at startup, which has caused SxS (side-by-side)
configuration errors on some Windows machines. Folder distribution
keeps the native DLLs next to the EXE and avoids the issue.

**Do not enable `PublishSingleFile`, trimming, or Native AOT** for
this project. The native dependencies and WPF resource loading do
not survive those transforms reliably.

## 5. Package the publish folder

```powershell
$ver = "0.6.0"   # match the tag
Compress-Archive `
  -Path artifacts/app/* `
  -DestinationPath "artifacts/PrintEXE-v$ver-win-x64.zip" `
  -Force

# Generate the checksum:
$hash = (Get-FileHash -Algorithm SHA256 "artifacts/PrintEXE-v$ver-win-x64.zip").Hash.ToLower()
"$hash  PrintEXE-v$ver-win-x64.zip" | Out-File -Encoding ascii artifacts/SHA256SUMS.txt
```

The published ZIP contains:

- `PrintShopCompanion.exe` — the application entry point
- `PrintShopCompanion.dll`, `PrintShopCompanion.Infrastructure.dll`, `PrintShopCompanion.Core.dll`, `PrintShopCompanion.Shared.dll`
- `Microsoft.Data.Sqlite.dll`, `SQLitePCLRaw.batteries_v2.dll`
- Native: `e_sqlcipher.dll` / `e_sqlite3.dll` (SQLCipher + SQLite runtime)
- `appsettings.json` (runtime config — no secrets)
- All .NET 10 runtime DLLs (self-contained)
- WPF UI assets + themes

## 6. GitHub Actions workflow behavior

`.github/workflows/build-windows.yml` runs on every push to `main`,
every pull request targeting `main`, every tag matching `v*`, and on
manual dispatch.

### Triggers

| Event | Behavior |
|---|---|
| `push` to `main` | Build + test + package; upload artifact; **no release** |
| `pull_request` to `main` | Build + test + package; upload artifact; **no release** |
| `push` tag `v*` | Build + test + package + **create GitHub Release** |
| `workflow_dispatch` (with `release-tag` input) | Build + test + package + **create release for the given tag** |
| `workflow_dispatch` (no input) | Build + test + package; **no release** |

### Pipeline stages

1. **Checkout** — full history (`fetch-depth: 0`) for tag lookup.
2. **Setup .NET 10 SDK** — `actions/setup-dotnet@v4`, version `10.0.x`.
3. **Cache NuGet** — keyed on `*.csproj` + `*.props` hashes.
4. **Set `BUILD_VERSION`** — derived from the git tag (e.g. `v0.6.0` → `0.6.0`); fallback `0.0.<run_number>` for non-tag builds.
5. **Restore** — `dotnet restore` (no `continue-on-error`).
6. **Build** — `dotnet build -c Release --no-restore` (no `continue-on-error`).
7. **Test** — `dotnet test --no-build` (no `continue-on-error`); uploads `tests.trx`.
8. **Publish** — folder distribution, self-contained, `win-x64`, no single-file.
9. **Validate published output** — verifies the EXE, required DLLs, native `e_sql*.dll`, `appsettings.json`, and the absence of secrets/databases.
10. **Package** — 7-Zip the publish folder into `PrintEXE-vX.Y.Z-win-x64.zip`.
11. **Write metadata** — `BUILD-INFO.txt` + `README.txt` with install instructions.
12. **Generate SHA-256** — `SHA256SUMS.txt` covering the ZIP + `BUILD-INFO.txt`.
13. **Verify checksum round-trip** — re-reads the checksum and confirms it matches the ZIP.
14. **Upload artifact** — `PrintEXE-win-x64` artifact (ZIP + checksums + metadata).
15. **Release job** (tag only) — checks the release does not already exist, downloads the artifact, re-verifies the checksum on Linux, and creates the GitHub Release with `softprops/action-gh-release@v2`.

### Concurrency

```yaml
concurrency:
  group: windows-build-${{ github.ref }}
  cancel-in-progress: ${{ !startsWith(github.ref, 'refs/tags/v') }}
```

Branch/PR pushes cancel in-progress runs on the same ref. Tag pushes
are **not** cancelled — every tag deserves a complete release.

### Permissions

The workflow-level permission is `contents: read` (least privilege).
The `release` job declares `contents: write` explicitly so it can
create the release. The `build` job is read-only — a failed build
cannot produce a release because the release job `needs: build`.

## 7. Artifact download

After a successful run:

1. Open the run on GitHub Actions.
2. Download the **`PrintEXE-win-x64`** artifact (a ZIP containing the
   distribution ZIP + `SHA256SUMS.txt` + `BUILD-INFO.txt` + `README.txt`).
3. Unzip locally; the distribution is `PrintEXE-vX.Y.Z-win-x64.zip`.
4. Verify: `sha256sum PrintEXE-vX.Y.Z-win-x64.zip` and compare with
   `SHA256SUMS.txt`.

## 8. Version-tag release procedure

To cut a new release:

```bash
# 1. Ensure main is green (the latest CI run passed).
# 2. Tag the commit:
git tag v0.6.0
git push origin v0.6.0

# 3. The tag push triggers the workflow. The release job:
#    - builds + tests the tagged commit
#    - packages PrintEXE-v0.6.0-win-x64.zip
#    - generates SHA256SUMS.txt
#    - creates the GitHub Release at https://github.com/Arjunyadavsoma/PrintEXE/releases/tag/v0.6.0
#    - attaches the ZIP + checksums + metadata
```

**Do not** re-tag an existing version. The release job checks
`gh release view <tag>` and refuses to overwrite an existing release.
To re-release, delete the release (and tag) first, then re-push.

### Versioning strategy

- **SemVer-ish**: `vMAJOR.MINOR.PATCH` (e.g. `v0.6.0`).
- The git tag is the source of truth — `BUILD_VERSION` is derived
  from the tag name (stripping the `v` prefix).
- `Directory.Build.props` falls back to `0.4.7` for local builds
  without `BUILD_VERSION` set, so the assembly version is always
  deterministic.
- The auto-updater (`GitHubUpdateService`) compares the running
  assembly version against the latest GitHub Release tag, so a new
  tag is what makes the updater fire.

## 9. Installation and upgrade

### Fresh install

1. Download `PrintEXE-vX.Y.Z-win-x64.zip` from the latest release.
2. Unblock the file (right-click → Properties → Unblock).
3. Unzip to a folder, e.g. `C:\PrintEXE\`.
4. Run `PrintShopCompanion.exe`.
5. On first launch: pair with your shop (paste the device token from
   the web dashboard), then select a printer in the Printers tab.

### Upgrade

- **Auto-update**: PrintEXE checks GitHub Releases on startup. When
  a new version is available, a banner appears. Click "Update Now"
  to download, verify SHA-256, back up the current folder, and
  swap in the new files. If the update fails, it rolls back.
- **Manual**: download the new ZIP, unzip over the existing folder
  (or a new folder), and run. Settings are stored in
  `%LocalAppData%\PrintShop\Companion\` and are preserved across
  upgrades.

## 10. Troubleshooting

### `dotnet restore` fails with NU1605 (downgrade)

A package reference pins a transitive dependency lower than a
referenced package. Check `Directory.Build.props` and the
`PackageReference` overrides in
`PrintShopCompanion.Infrastructure.csproj` — they pin
`Newtonsoft.Json` 13.0.4 and `SQLitePCLRaw.lib.e_sqlite3` 3.50.3 to
fix vulnerable transitive deps.

### Published EXE crashes on first SQLCipher call

The native `e_sqlcipher.dll` is missing from the publish folder.
Check that `SQLitePCLRaw.bundle_e_sqlcipher` 2.1.11 is in
`PrintShopCompanion.Infrastructure.csproj` and that you published
with `-r win-x64 --self-contained true`. Do not use
`PublishSingleFile=true` for this project.

### `GitHubUpdateService` keeps reporting "update available" after
### updating

The assembly version did not change. The CI workflow sets
`BUILD_VERSION` from the git tag; ensure the tag matches
`vX.Y.Z` (e.g. `v0.6.0`, not `v0.6`). `Directory.Build.props` writes
`<AssemblyVersion>$(Version).0</AssemblyVersion>`.

### MSI / Setup.exe build fails

The MSI / Setup.exe steps were removed from the workflow in favor of
the portable ZIP distribution (which is more reliable on machines
without the exact VC++ runtime). The WiX sources remain in
`installer/` for users who want to build an MSI locally.

### Test results show failures

Tests run on `windows-latest` with no `continue-on-error`. A failing
test fails the build. Download the `test-results` artifact to inspect
the `tests.trx` file. The 48 tests cover: queue idempotency, migration
safety, GitHubUpdateService version comparison, MagicLink pairing,
and JobProcessor idempotency.

## 11. Required GitHub repository permissions

The workflow uses the built-in `GITHUB_TOKEN` (no PAT required for
build/test/release). The repository must allow:

- **Actions → Read + write** (for the workflow to upload artifacts
  and create releases).
- **Contents → Read** at the workflow level (default), **Write** for
  the release job (declared explicitly on the job).
- **Metadata → Read** (default, for checkout).

No external secrets are required for a non-signed release. To enable
code signing, add the repository secrets
`PRINTSHOP_CODE_SIGN_PFX` (base64-encoded PFX) and
`PRINTSHOP_CODE_SIGN_PWD` (the PFX password). The workflow signs the
release artifacts only when both secrets are present.

## 12. Security notes

- The workflow never prints secrets in logs. The `PFX_B64` / `PFX_PWD`
  env vars are hoisted at the job level only so step `if:` conditions
  can test for their presence — the actual values are never echoed.
- `.env` is in `.gitignore` and is NOT committed. The publish
  verification step rejects any `.env`, `*.db`, `*.pfx`, `*.key` files
  in the publish output.
- The workflow does not hardcode any GitHub tokens. It uses the
  built-in `GITHUB_TOKEN` with `contents: write` only on the release
  job.
- If you discover previously committed credentials, **rotate them
  immediately** — removing a file from the latest commit does NOT
  remove it from git history. Use `git filter-repo` or BFG Repo
  Cleaner to purge history, then force-push (coordinate with
  collaborators first).
