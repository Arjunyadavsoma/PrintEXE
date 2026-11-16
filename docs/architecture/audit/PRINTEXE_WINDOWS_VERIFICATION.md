# PrintEXE Windows Production Verification

**Phase:** 3
**Status:** Implemented (Windows runtime verification BLOCKED — no Windows machine or physical printer in the audit environment)
**PrintEXE version:** v0.5.5 (commit `199f04d`)
**Date:** 2026-11-16

## 1. Scope

The Phase 0 audit identified PrintEXE v0.5.5 as the existing Windows
desktop companion. The Phase 3 objective is to **verify** the existing
implementation against the audit's claims, **not** to redesign or
rewrite it. The prompt explicitly says:

> This phase must not become a duplicate redesign or a second
> independent PrintEXE prompt.

## 2. Verification environment

This audit is conducted in a Linux sandbox. The following are
**unavailable** in this environment:

- A Windows machine (any version).
- A .NET SDK (verified: `dotnet` is not on PATH; `find / -name dotnet`
  returns nothing; `apt list --installed | grep dotnet` is empty).
- A physical printer.

The following are **available**:

- The PrintEXE git repository (working copy at `/home/z/my-project`,
  remote at `github.com/Arjunyadavsoma/PrintEXE`).
- The GitHub Actions API (to confirm CI build results).
- The published v0.5.5 release artifact (62 MB portable ZIP).

## 3. Verified by source inspection

These items were verified by reading the actual source code:

| Item | Evidence |
|---|---|
| SQLCipher encryption wired | `SqliteJobQueue.cs` + `SqlitePendingOpsQueue.cs` both call `SqlCipherInitializer.Initialize()` in their constructors before checking `IsEncryptionAvailable`. Fail-closed: throw `InvalidOperationException` when unavailable. |
| DPAPI integration | `QueueKeyStore.cs` uses `DataProtectionScope.CurrentUser` for the SQLCipher key; `device.dat` for the device token. |
| Local DB initialization | `LocalStorageLayout.cs` resolves `%LocalAppData%\PrintShop\Companion\Database\queue.db` with `legacyRootOverride` for test isolation. |
| Queue persistence | `SqliteJobQueue.cs` (1035 lines) — idempotent `ON CONFLICT DO NOTHING` on `JobId`, transactional state transitions. |
| File storage atomic writes | `HttpPrintShopBackend.DownloadPdfAsync` — `.tmp` then rename to `.pdf` after SHA-256 verify. |
| File retention | `FileRetentionManager.cs` (186 lines) — 60-min timer, disk quota enforcement. |
| Receipt checksum | `MarkJobReceivedAsync` sends pure hex SHA-256 (no prefix). Matches SaaS zod schema `^[a-f0-9]{64}$`. |
| API authentication | Device token via `Authorization: Bearer`, HMAC-SHA256 hash lookup on server. |
| Device registration / pairing | `PairWithDeviceTokenAsync` + paste-token flow. |
| Job download | `DownloadPdfAsync` (atomic + checksum verify). |
| Job state transitions | `JobProcessor.cs` — Pending → Downloading → Rendering → Spooled → Printed → ReportedPrinted. |
| Acknowledgments | `MarkJobReceivedAsync` (durable receipt) + `ReportJobStatusAsync` (atomic claim). |
| Retry behavior | Max 5 retries with exponential backoff. |
| Duplicate-job prevention | `ON CONFLICT DO NOTHING` on `JobId` + atomic claim via `POST /agents/jobs/{id}/status`. |
| Offline operation | `SqlitePendingOpsQueue` (encrypted) + `SyncEngine` polling fallback. |
| Restart recovery | `ReplayInFlightAsync` returns Downloading/Rendering/Spooled jobs to Pending on startup. |
| Printer discovery | `WindowsPrintSpoolerAdapter` enumerates `System.Printing.PrintQueue`. |
| Logging | Serilog rolling daily, 14-day retention, `DeviceTokenRedactor` enricher. |
| Installer + portable | WiX v5 MSI + portable ZIP; both built on GitHub Actions. |
| Auto-updater | `GitHubUpdateService` — SHA-256 against asset digest, backup, rollback, sentinel file. |

## 4. Verified by CI

These items were verified via the GitHub Actions API:

| Item | Evidence |
|---|---|
| Windows release build | `v0.5.5` workflow run on `windows-latest` — `conclusion: success`, `head_sha: 199f04dc` |
| All 48 unit tests pass on Windows CI | The CI workflow runs `dotnet test` before packaging; the run succeeded. |
| Portable ZIP artifact published | `PrintShopCompanion-portable.zip` (62,053,885 bytes, 1 download) attached to the v0.5.5 release |
| MSI installer built | WiX v5 step in `build-windows.yml` runs before the release upload; the release succeeded. |

## 5. BLOCKED — Windows runtime verification

The following tests **cannot be run** in this Linux sandbox and have
not been verified against a real Windows machine. They are recorded
here honestly so a future Windows-equipped verifier knows what to
run.

| # | Test | Why blocked |
|---|---|---|
| 1 | Clean installation | No Windows machine |
| 2 | Upgrade from supported previous version (v0.4.7 → v0.5.5) | No Windows machine |
| 3 | First-run configuration | No Windows machine |
| 4 | Secure SQLCipher key creation (DPAPI round-trip) | DPAPI is Windows-only |
| 5 | Database creation (encrypted) | No Windows machine |
| 6 | Restart recovery | No Windows machine |
| 7 | Device pairing against production backend | Requires real shop account |
| 8 | Cloud synchronization | Requires real backend + paired device |
| 9 | Offline queueing | No Windows machine |
| 10 | Network restoration | No Windows machine |
| 11 | Document download | Requires paired device + active shop |
| 12 | Physical printing | No Windows machine, no printer |
| 13 | Printer failure handling | No printer |
| 14 | Job retry | No Windows machine |
| 15 | Duplicate acknowledgment | Requires paired device |
| 16 | Application update (v0.5.4 → v0.5.5) | No Windows machine |
| 17 | Uninstall + data retention | No Windows machine |

## 6. Manual test plan (for a Windows-equipped verifier)

When a Windows machine + physical printer become available, run this
sequence. Each step has an explicit pass/fail signal.

### 6.1 Setup
1. Download `PrintShopCompanion-portable.zip` from
   https://github.com/Arjunyadavsoma/PrintEXE/releases/tag/v0.5.5
   to a clean Windows 10/11 machine.
2. Unblock the file (right-click → Properties → Unblock).
3. Extract to `C:\PrintShop\`.
4. Expected: extraction succeeds; `PrintShopCompanion.exe` is present.

### 6.2 First run + pairing
5. Double-click `PrintShopCompanion.exe`. Expected: window opens with
   the paste-token pairing form (no crash).
6. In a browser, sign in at
   `https://printshop-saas-three.vercel.app/shop/login`.
7. Go to Printer Setup → Register new agent → copy the device token.
8. Paste the token into PrintEXE → click Connect.
9. Expected: PrintEXE shows "Connected to <shop name>", the agent
   appears in the shop's agent list on the web, and `%LocalAppData%\
   PrintShop\Companion\Security\device.dat` exists (DPAPI-encrypted).

### 6.3 Printer selection
10. Go to Printers tab → select your printer → click Test Print.
11. Expected: a test page prints on the physical printer; the printer
    name is shown in the dashboard.

### 6.4 End-to-end job
12. From the customer web (or a test order in the shopkeeper
    dashboard), create a paid order with a small PDF.
13. Expected (within seconds):
    - PrintEXE shows the job in the Print Queue (Downloading → Spooled
      → Printed).
    - The physical printer prints the PDF.
    - `%LocalAppData%\PrintShop\Companion\Jobs\queued\<jobId>.pdf`
      exists.
    - The SaaS order status moves to `printing` then `ready`.
14. Wait 60 minutes. Expected: the local PDF is deleted by
    `FileRetentionManager`.

### 6.5 Restart recovery
15. While a job is in the `Downloading` state, kill PrintEXE (Task
    Manager → End Task).
16. Restart PrintEXE.
17. Expected: the job resumes from `Pending` (per
    `ReplayInFlightAsync`); no duplicate download.

### 6.6 Offline queue
18. Disconnect the network.
19. Create a paid order on the web (the SaaS queues the dispatch).
20. Reconnect the network.
21. Expected: PrintEXE picks up the queued job within 2s (polling
    fallback) and prints it.

### 6.7 SQLCipher verification
22. Open `%LocalAppData%\PrintShop\Companion\Database\queue.db` with
    the `sqlite3` CLI: `sqlite3 queue.db "SELECT * FROM jobs;"`.
23. Expected: error `file is not a database` or `file is encrypted`
    (SQLCipher is active). The file should NOT be readable without
    the key.

### 6.8 Upgrade from v0.4.7 (regression)
24. Install PrintEXE v0.4.7 on a clean Windows machine.
25. Add a few jobs (let them complete).
26. Upgrade to v0.5.5 (replace the portable folder, OR use the
    auto-updater from v0.5.4 if you have it).
27. Expected: existing jobs are preserved (the migration runs once,
    writes `.migrated` sentinel); no data loss.

## 7. Verdict

**PrintEXE v0.5.5 is CI-verified but NOT runtime-verified.**

- Source inspection confirms the architecture matches the audit.
- Windows CI confirms the build + 48 tests pass on `windows-latest`.
- The release artifact is published and downloadable.
- The 17-step runtime test plan is documented above for a
  Windows-equipped verifier.

This phase makes **no code changes** to PrintEXE — per the prompt's
restriction. PrintEXE remains at v0.5.5.

## 8. Compatibility with Phase 1 + Phase 2 SaaS changes

PrintEXE v0.5.5 talks to the SaaS backend over the 8 shared endpoints
(device token, heartbeat, jobs pending, status, received, sync
pull/push/status). Phase 1 (admin auth) and Phase 2 (entitlement)
do **not** change any of these endpoints' contracts:

- Phase 1 adds admin permissions to the JWT, but PrintEXE uses
  **device-token auth** (not JWT) for all its calls. No change.
- Phase 2 adds `withEntitlement` middleware but does not yet wire it
  into any device-auth route. The agent routes
  (`/api/v1/agents/*`) use `withDeviceAuth`, not `withAuth`, so they
  are not affected by entitlement enforcement either way. No change.

**PrintEXE v0.5.5 remains compatible with the SaaS backend after
Phase 1 + Phase 2.**

If a later phase wires `withEntitlement` into shopkeeper routes that
PrintEXE calls indirectly (e.g. via the agent's `/jobs/pending`), the
agent routes will continue to use `withDeviceAuth` and remain
exempt — the entitlement check is shopkeeper-JWT-scoped, not
device-token-scoped. This is the correct design: an agent that has
already paired with a shop should not be locked out by the
shopkeeper's billing state mid-print-job; the entitlement decision
is enforced at order-creation time on the shopkeeper side.
