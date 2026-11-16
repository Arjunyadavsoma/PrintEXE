# File Retention Policy — PrintEXE v0.5

Theautomatic lifecycle management of customer PDFs at the local (companion) and cloud (Supabase Storage) layers.

## Two retention clocks, independent

### Local PDF retention (companion-side)

**Default:** 60 minutes after the printing state reaches a terminal value (Completed, Failed, or Cancelled).

**Where the deadline is stored:** the `LocalDeleteAfter` column in the encrypted SQLite queue. Set to `printedAt + 60 min` (configurable via `Storage:LocalRetentionMinutes`) when the printing state transitions to terminal.

**Who enforces it:** the `FileRetentionManager` in the companion. Runs:
1. Once on application startup (cleans up files left over from a previous crash).
2. Every 60 seconds while the app is running.

**Rules:**
- Never delete a file referenced by a job in `printing` state (the spooler may still be reading it).
- Never delete a file referenced by a job in `queued` state (the JobProcessor may still need to print it).
- For `completed`, `failed`, `cancelled` states: delete if `LocalDeleteAfter < now`.
- If the file is locked by the spooler (Windows holds a handle), retry on the next tick (60s later).
- After deletion, set `storageStatus = local_deleted` in the SQLite queue.
- The PrintJobRecord row in SQLite is NOT deleted — only the PDF file is. Business history is preserved.

**Configuration:** `appsettings.json` → `Storage:LocalRetentionMinutes` (default 60, range 5-240).

### Cloud PDF retention (server-side)

**Default:** 15 minutes after the companion confirms local receipt via `POST /agents/jobs/{id}/received`.

**Where the deadline is stored:** the `cloudDeleteAfter` column on the `PrintJob` table (PostgreSQL). Set to `receivedAt + 15 min` by the `/received` route.

**Who enforces it:** a server-side cron worker. Two implementation options (the user picks based on their Vercel plan):
1. **Vercel Cron** — add to `vercel.json`:
   ```json
   {
     "crons": [
       { "path": "/api/cron/cleanup-cloud-storage", "schedule": "*/5 * * * *" }
     ]
   }
   ```
2. **External cron** — a GitHub Actions workflow that hits the endpoint every 5 min (free for public repos).

**Rules:**
- No cloud deletion before verified local receipt (the `/received` route is the gate).
- No cloud deletion merely because a presigned URL was generated (URLs expire but the object stays).
- No cloud deletion merely because a `job.dispatch` event was emitted (the companion may not have received it).
- Before deleting, check that no other active job references the same Supabase object (rare, but possible if a job was re-queued).
- After deletion, set `storageStatus = cloud_deleted` on the PrintJob row.
- The PrintJob + PrintFile rows are NOT deleted — only the Storage object is. Business history is preserved.
- Failed deletions are retried on the next cron tick (5 min later). The cron route is idempotent.

**Configuration:** the cron route reads `CLOUD_RETENTION_MINUTES` from env (default 15).

## Separation of storage and business lifecycle

PDF deletion (local OR cloud) NEVER automatically deletes:
- PrintJob history (the `printJob` table row)
- Order history (the `order` table row)
- PrintFile metadata (the `printFile` table row — stores `originalFilename`, `copies`, `colorMode`, etc.)
- Payment records (`paymentAttempt`, `paymentEvent`)
- Agent audit records (`auditLog`)
- Delivery attempt history (`printDeliveryAttempt` — new in v0.5)

This is enforced by:
- The companion's `FileRetentionManager` only deletes the file at `Jobs/completed/<jobId>.pdf`, not the SQLite row.
- The backend's cleanup cron only calls `container.storage.deleteObject(storageKey)`, not `repos.printJobs.delete(id)`.

## Cleanup deadlines in SQLite (companion-side)

```sql
-- Schema extension (additive, runs on first open via ALTER TABLE ADD COLUMN):
ALTER TABLE print_jobs ADD COLUMN local_delete_after TEXT;
ALTER TABLE print_jobs ADD COLUMN cloud_delete_after TEXT;
ALTER TABLE print_jobs ADD COLUMN storage_status TEXT DEFAULT 'cloud_available';
```

The `FileRetentionManager` queries:
```sql
SELECT job_id, local_pdf_path FROM print_jobs
 WHERE printing_status IN ('completed', 'failed', 'cancelled')
   AND local_delete_after IS NOT NULL
   AND local_delete_after <= @now
   AND storage_status != 'local_deleted';
```

## Disk-space monitoring

The companion monitors `Jobs/` directory size every 60s. If it exceeds `Storage:MaxLocalStorageBytes` (default 1 GB):
1. Log a warning.
2. Aggressively clean up the oldest `completed` files (regardless of `LocalDeleteAfter`).
3. Surface a "Disk space low" notification in the UI.

This prevents a flood of large PDFs from filling the shop PC's disk.

## Recovery

- **App crash during cleanup:** the `FileRetentionManager` runs on startup, so any files that should have been deleted are caught up.
- **Spooler holds a file lock:** the deletion fails with `UnauthorizedAccessException` or `IOException`. The manager logs the error and retries on the next tick. The file is eventually deleted when the spooler releases the handle.
- **SQLite is locked:** the manager catches `SqliteException` and retries on the next tick (60s). If the lock persists for > 5 min, the manager logs a critical error and surfaces in the UI.
- **Disk full:** the manager catches `IOException` and surfaces "Disk full" in the UI.

## What's NOT enforced (intentional)

- The companion does NOT delete the cloud copy. Only the server-side cron worker does that. The companion has no Supabase credentials and no way to delete cloud objects.
- The server does NOT delete the local copy. Only the companion's `FileRetentionManager` does that. The server has no way to reach the shop PC's filesystem.
- The two retention clocks are independent. A job can be `cloud_deleted` while the local copy is still in `local_verified` (the cloud cleanup ran first). A job can be `local_deleted` while the cloud copy is still in `cloud_delete_pending` (the local cleanup ran first). Both are valid terminal states.

## Files

- `src/PrintShopCompanion.Infrastructure/Storage/FileRetentionManager.cs` — local cleanup
- `src/PrintShopCompanion.Core/Options/Options.cs` — `StorageOptions.LocalRetentionMinutes`, `MaxLocalStorageBytes`, `CloudRetentionMinutes`
- `tests/PrintShopCompanion.Infrastructure.Tests/FileRetentionTests.cs` — 4 tests
- `docs/DATABASE_MIGRATIONS.md` — backend cron route (patch for printshop-saas)
