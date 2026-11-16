# Offline Recovery — PrintEXE v0.5

The 20 deterministic recovery cases from the master prompt, with the implemented behavior for each.

| # | Scenario | Deterministic recovery |
|---|---|---|
| 1 | Customer upload interrupted | The website returns 4xx; the order stays in `awaiting_payment`. The customer retries. The companion never sees the job. |
| 2 | Agent offline during upload | Backend stores the PDF in Supabase, sets `deliveryStatus = cloud_staged`. Customer sees "Waiting for shop PC". When the agent reconnects, the polling fallback fetches the job. |
| 3 | Agent reconnects after several hours | The polling fallback (`GET /agents/jobs/pending`) returns up to 10 queued jobs. The companion downloads each, verifies SHA-256, calls `/received`, prints. |
| 4 | Direct transfer fails (v0.5.1+) | Backend falls back to `cloud_staged`, emits `job.dispatch` with presigned URL. Companion downloads via cloud. |
| 5 | TURN relay fails (v0.5.1+) | Same as #4 — the WebRTC stack falls back to cloud. |
| 6 | Cloud upload succeeds but notification fails | The job is in Supabase Storage but the `job.dispatch` event was lost. The companion's 2-second polling fallback picks it up on the next poll. |
| 7 | Download interrupted halfway | The `.tmp` file is incomplete. On the next attempt (or restart), the companion deletes the `.tmp` and re-downloads from scratch. (HTTP Range resume is v0.5.1+.) |
| 8 | Windows crashes during file write | The `.tmp` file may be truncated. On restart, `FileRetentionManager` deletes incomplete `.tmp` files in `Jobs/incoming/`. The job is re-dispatched via polling. |
| 9 | Windows crashes after local receipt | The PDF is in `Jobs/queued/` and the SQLite queue has `deliveryStatus = received`. On restart, `JobProcessor.ReplayInFlightAsync` picks it up and prints it normally. The cloud copy is scheduled for deletion (the `/received` call succeeded before the crash). |
| 10 | Windows crashes during printing | The SQLite queue has `printingStatus = printing` (or `claimed`). On restart, `ReplayInFlightAsync` resubmits to the spooler. The backend's atomic claim prevents double-print — if the spooler job already exists, the resubmit is a no-op. |
| 11 | Internet disconnects after spooler submission | The companion has submitted to the spooler but the `POST /status {status:"completed"}` call fails. The companion retries on the next sync cycle (15s). If still offline, the job stays in `printing` state locally; the backend shows `printing` (claimed) until the companion reconnects. |
| 12 | Status update fails | Same as #11 — the companion retries. If the job is already `completed` on the backend (the update was received but the response was lost), the idempotent update returns `skipped:true` and the companion moves on. |
| 13 | SQLite database is locked | The companion's `SqliteJobQueue` uses `Cache=Shared` + a `SemaphoreSlim` for serialization. If the lock persists, the JobProcessor backs off 5s and retries. If still locked after 5 min, the companion logs a critical error and surfaces in the UI. |
| 14 | Local disk becomes full | The companion checks disk space before each download. If insufficient, it cleans up `completed` files aggressively (regardless of `localDeleteAfter`). If still full, the job is marked `delivery_failed` with `lastError = "insufficient disk space"` and surfaced in the UI. |
| 15 | Local file is corrupted | SHA-256 verification on download catches this — the `.tmp` is deleted, the job is marked `delivery_failed`, and the cloud copy is NOT deleted (the companion never called `/received`). The shopkeeper can manually re-dispatch from the dashboard. |
| 16 | Cloud deletion fails | The cron worker logs the error and retries on the next tick (5 min later). The job stays in `cloud_delete_pending`. If the failure persists for > 1 hour, the cron worker surfaces an alert in the dashboard. |
| 17 | Local deletion fails | `FileRetentionManager` catches `IOException` (file locked by spooler), logs, and retries on the next tick (60s). If the failure persists for > 1 hour, the manager surfaces in the UI. |
| 18 | Device is revoked | The next companion → backend request returns 401. The companion's `HttpPrintShopBackend` catches 401, surfaces "Device revoked" in the UI, stops all background work, and shows a "Re-pair this PC" prompt. The Socket.IO client also receives `agent.revoked` event as a backup signal. |
| 19 | Duplicate job notification arrives | The SQLite queue is idempotent on `JobId` — `EnqueueAsync` returns the existing record. The atomic claim (`POST /status {status:"printing"}`) is also idempotent — the second claim returns `skipped:true`. The job is printed exactly once. |
| 20 | Two agents attempt to claim the same job | The backend's atomic claim is `UPDATE printJob WHERE id AND status='queued' SET status='printing', agentId=?` — only one UPDATE affects a row. The loser's claim returns `skipped:true` with `reason: "job is 'printing' — already claimed by another agent"`. The loser cancels locally and moves to the next job. |

## What's guaranteed

- **No silent job loss** — every job is either printed, failed (with `lastError`), or cancelled (with a reason). The SQLite queue preserves the record forever (until manual purge).
- **No premature deletion** — the cloud copy is only deleted after the companion confirms local receipt. The local copy is only deleted 60 min after a terminal printing state.
- **No infinite retry loops** — every retry has a `MaxAttempts` cap (default 5) with exponential backoff. After 5 attempts, the job is marked `failed` permanently.
- **No uncontrolled duplicate printing** — the atomic claim + idempotent `EnqueueAsync` + spooler job ID tracking prevent double-print even across crashes.

## What's NOT guaranteed (and documented)

- **Exactly-once physical printing** — a crash between spooler submission and server acknowledgement creates an unavoidable uncertainty window. The companion uses at-least-once delivery with idempotent processing (atomic claim prevents double-claim; spooler job ID prevents double-submit). In the rare case of a crash between submit and ack, the companion's `ReplayInFlightAsync` resubmits — but the spooler may have already printed, resulting in two physical copies. This is documented as "at-least-once" not "exactly-once" per the master prompt's explicit acknowledgment.
- **No lost jobs if the companion is uninstalled** — if the shopkeeper uninstalls PrintEXE while jobs are in the SQLite queue, those jobs are lost locally but still exist on the backend (in `cloud_staged` or `queued` state). The dashboard shows them as "Waiting for shop PC" and the shopkeeper can re-pair or print manually.

## Files

- `docs/OFFLINE_RECOVERY.md` (this document)
- `src/PrintShopCompanion.Infrastructure/Queue/JobProcessor.cs` — crash recovery
- `src/PrintShopCompanion.Infrastructure/Storage/FileRetentionManager.cs` — startup cleanup
- `tests/PrintShopCompanion.Infrastructure.Tests/OfflineRecoveryTests.cs` — tests for cases 8, 9, 10, 19, 20
