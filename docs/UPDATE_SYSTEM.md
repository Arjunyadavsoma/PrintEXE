# Update System — PrintShop Companion

This document describes the auto-update pipeline end-to-end.

## Source of truth

The companion checks `https://api.github.com/repos/Arjunyadavsoma/PrintEXE/releases/latest` for new versions. It does **not** consult a separate update server — GitHub Releases IS the update server.

## Update workflow state machine

```
                ┌─────────┐
                │  Idle   │←──────────────────────────────────┐
                └─────────┘                                    │
                     │                                         │
                     │ CheckForUpdatesAsync                    │
                     ▼                                         │
                ┌─────────┐                                    │
                │Checking │                                    │
                └─────────┘                                    │
                     │                                         │
            ┌────────┴────────┐                                │
            │                 │                                │
            ▼                 ▼                                │
       ┌─────────┐       ┌─────────┐                          │
       │Available│       │UpToDate │                          │
       └─────────┘       └─────────┘                          │
            │                                              │
            │ InstallUpdateAsync (user clicks "Update Now") │
            ▼                                              │
       ┌─────────┐                                          │
       │Downloading│                                        │
       └─────────┘                                          │
            │                                                │
            ▼                                                │
       ┌─────────┐                                          │
       │Verifying │──SHA-256 mismatch──→  ┌─────────┐       │
       └─────────┘                        │  Failed │───────┘
            │                            └─────────┘   user clicks Retry
            ▼
       ┌─────────┐
       │Extracting│
       └─────────┘
            │
            ▼
       ┌─────────┐
       │Installing│
       └─────────┘
            │
            ▼
       ┌─────────┐
       │Restarting│  → Application.Current.Shutdown()
       └─────────┘
```

The state is exposed via the `IUpdateService.State` property and the `StateChanged` event. The `MainWindow`'s update banner subscribes to `StateChanged` and updates its label, button text, and progress bar live as the install progresses.

## SHA-256 verification

The GitHub Releases API exposes a `digest` field on each asset, in the form `sha256:<hex>`. The companion:

1. Streams the downloaded bytes through a `CryptoStream` wrapping `SHA256.Create()` while writing them to disk — this avoids reading the file twice.
2. Compares the actual SHA-256 to the GitHub-provided digest.
3. If they don't match, throws `InvalidOperationException` with the expected and actual hashes; the install aborts and the user sees the error message in the MessageBox.
4. If GitHub didn't provide a digest (older release), the companion logs a warning and proceeds without verification (cannot verify what doesn't exist).

## Backup and rollback

The .bat updater creates a full backup of the current app directory BEFORE xcopying the new files. The backup goes to `%TEMP%\PrintShop-backup-<guid>\`.

If xcopy fails after 5 retries, OR if the new EXE doesn't exist after xcopy succeeds, the .bat:

1. Writes a sentinel file `_printshop_update_failed` in the app directory with the failure reason (`BACKUP_FAILED`, `XCOPY_FAILED`, or `EXE_MISSING`).
2. Restores the previous version from the backup via `xcopy /E /Y /I /Q /R`.
3. Starts the (restored) old EXE.
4. Exits with error code 1.

On the next app startup, `App.OnStartup` calls `GitHubUpdateService.CheckForFailedUpdateSentinel()`. If the sentinel exists, the app:
1. Shows a MessageBox telling the user the previous update failed and the app rolled back.
2. Deletes the sentinel so the warning doesn't keep appearing.

## Progress reporting

`InstallUpdateAsync` accepts an `IProgress<int>` that the caller uses to update a progress bar:

| Percentage | Stage |
| --- | --- |
| 5 | Started download |
| 40 | Download complete, starting SHA-256 verify |
| 50 | SHA-256 verified, starting extraction |
| 65 | Extraction complete, writing .bat updater |
| 85 | .bat written, launching it |
| 95 | .bat launched, about to shut down |
| 100 | Shutdown initiated |

The MainWindow's banner also displays a textual status: "Connecting to GitHub...", "Downloading update...", "Verifying SHA-256...", "Extracting files...", "Preparing updater...", "Restarting...".

## Click guard

The `MainWindow.Update_Click` method has a `_updateInProgress` flag that disables the button on entry. Additionally, `GitHubUpdateService.InstallUpdateAsync` uses `Interlocked.CompareExchange` to enforce a contract-level single-flight guard — even if two callers reach it simultaneously, only one passes the gate.

The original symptom (in `companion-20261107.log`) was 6 download attempts in 13 seconds — the user clicked "Update Now" 6 times. Both guards together ensure this cannot happen again.

## Failure modes handled

| Failure | Behavior |
| --- | --- |
| Network error during check | State → `Failed`. Banner shows "Update failed — click Update Now to retry". |
| SHA-256 mismatch | Throws, banner shows MessageBox with expected/actual hashes. User can retry. |
| Extraction produced empty directory | Throws, .bat is not launched. User sees error MessageBox. |
| xcopy fails 5x in .bat | .bat restores from backup, starts old EXE, writes sentinel. Next startup shows rollback warning. |
| New EXE missing after xcopy | Same as above. |
| Application already running | The `SingleInstance` mutex prevents this — second instance exits with "Another instance is already running". |
| Update while offline | `CheckForUpdatesAsync` catches the network exception, sets state to `Failed`. User can retry when back online. |
| User dismisses banner | Banner hidden but state remains `Available`. Next app restart re-checks and re-shows if still available. |

## What is NOT verified

- **The .bat updater itself is not signed.** A malicious actor with write access to the app directory could replace the .bat. This is mitigated by the MSI installer path (signed via `PRINTSHOP_CODE_SIGN_PFX` secret), but the portable ZIP distribution does not have this protection. Recommendation for production: prefer the signed MSI installer over the portable ZIP.
- **The GitHub API response is not signed.** We trust GitHub's TLS to prevent MITM. If GitHub's TLS is compromised, an attacker could swap the release JSON, the asset digest, and the asset bytes in coordinated fashion. This is a fundamental limitation of the GitHub Releases approach — the only way to mitigate it is to sign the asset itself (which requires a code-signing certificate the project does not currently have).

## Testing checklist (must be performed on a Windows machine)

- [ ] No update available — banner does not appear.
- [ ] New version available — banner appears with current → latest version text.
- [ ] Click Update Now — progress bar shows Downloading → Verifying → Extracting → Installing → Restarting.
- [ ] Update completes — app restarts, version label shows new version, banner does NOT reappear.
- [ ] Download interruption (kill network mid-download) — `Failed` state, MessageBox, user can retry.
- [ ] Invalid SHA-256 (manually corrupt the ZIP in temp dir before verify completes — hard to test) — `Failed` state, MessageBox shows expected/actual hashes.
- [ ] Sentinel file present on startup — MessageBox "previous update failed" appears, then disappears after dismissal.
- [ ] xcopy failure (set the new files to read-only) — .bat restores from backup, app starts old version, sentinel is written.
- [ ] Existing user data preserved — `%AppData%\PrintShop\Companion\queue.db` and `device.dat` are not in the app dir, so they survive the file swap.
- [ ] User clicks Update Now 6 times in 13 seconds — only one download occurs (Interlocked guard).
- [ ] User clicks Dismiss — banner hides; restart re-shows it (if still available).

## File locations

- Implementation: `src/PrintShopCompanion.Infrastructure/Update/GitHubUpdateService.cs`
- Contract: `src/PrintShopCompanion.Core/Contracts/IUpdateService.cs`
- State enum: `src/PrintShopCompanion.Core/Contracts/IUpdateService.cs` (`UpdateState`)
- UI banner: `src/PrintShopCompanion.App/Views/MainWindow.xaml` (search for `UpdateBanner`)
- UI logic: `src/PrintShopCompanion.App/Views/MainWindow.xaml.cs` (search for `Update_Click`)
- Sentinel check on startup: `src/PrintShopCompanion.App/App.xaml.cs` (search for `CheckForFailedUpdateSentinel`)
- Build version source: `Directory.Build.props` (reads `$(BUILD_VERSION)`)
- Workflow version setter: `.github/workflows/build-windows.yml` (search for `Set BUILD_VERSION from git tag`)
