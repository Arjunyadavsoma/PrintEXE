# Upgrade from v0.4.7 — PrintEXE v0.5

The companion handles its own upgrade automatically. The shopkeeper does NOT need to re-pair, lose pending jobs, or change any settings.

## What the upgrade does (automatic, on first launch of v0.5)

### 1. Local storage restructure

| Old location (v0.4.7) | New location (v0.5) | Action |
|---|---|---|
| `%AppData%\PrintShop\Companion\device.dat` | `%LocalAppData%\PrintShop\Companion\Security\device.dat` | Moved (DPAPI encryption unchanged) |
| `%AppData%\PrintShop\Companion\queue.db` | `%LocalAppData%\PrintShop\Companion\Database\queue.db` | Read (plain) → re-encrypted with SQLCipher → written to new location. Old file renamed to `queue.db.v047.bak` |
| `%AppData%\PrintShop\Companion\logs\` | `%LocalAppData%\PrintShop\Companion\Logs\` | New logs go to the new location; old logs stay in the old location for reference |
| `%Temp%\PrintShop\Companion\pdfs\` | `%LocalAppData%\PrintShop\Companion\Jobs\{incoming,queued,printing,completed}\` | Old PDFs are deleted (they're cached copies; the cloud originals still exist) |

### 2. SQLite schema migration

The new columns (`deliveryStatus`, `storageStatus`, `checksum`, `fileSize`, `receivedAt`, `cloudDeleteAfter`, `localDeleteAfter`, `deliveryAttemptCount`, `lastDeliveryError`) are added via `ALTER TABLE ADD COLUMN` on first open. Existing rows get the defaults:
- `deliveryStatus = "cloud_staged"` (existing jobs are already in the cloud)
- `storageStatus = "cloud_available"`
- `printingStatus` = the existing `status` value (queued/printing/completed/failed)
- All other new columns = `NULL` or `0`

### 3. SQLCipher key generation

A new 32-byte random key is generated via `RandomNumberGenerator.GetBytes(32)`, DPAPI-encrypted, and stored at `Security/queue.key`. The SQLite connection string includes `Password=<key>`.

### 4. Backward-compat for v0.4.7 backend

The v0.5 companion still works against the v0.4.7 backend (which doesn't have the new `/received` route or the new state columns). The companion's `MarkJobReceivedAsync` is wrapped in a try/catch that logs a warning and continues if the backend returns 404 (route not found). The cloud cleanup won't happen (no `cloudDeleteAfter`), but the local retention still works.

**Recommendation:** upgrade the backend FIRST, then the companion. If you upgrade the companion first, the cloud copies won't be cleaned up until the backend is upgraded.

## What the shopkeeper needs to do

**Nothing.** The upgrade is automatic on first launch of v0.5. The shopkeeper will see:
1. The new sidebar item "Delivery" (between "Print Queue" and "Printers")
2. The "Application" section in Settings now has new options (Local retention, Max local storage, Cloud recovery grace, Max concurrent downloads)
3. The version label in the sidebar shows `v0.5.0`

## What the shopkeeper needs to do on the website

If the shopkeeper wants the cloud cleanup to work (Supabase objects auto-deleted 15 min after the shop PC receives them), the website backend must be upgraded to v0.5 too. See `docs/DATABASE_MIGRATIONS.md` for the Prisma schema + new routes to apply to the printshop-saas repo.

If the website is NOT upgraded, the companion still works — but the cloud copies stay in Supabase Storage forever (until the shopkeeper manually deletes them or the Supabase lifecycle policy expires them).

## Rollback

If v0.5 has a critical bug, the shopkeeper can roll back to v0.4.7:

1. Close PrintEXE v0.5.
2. Delete (or rename) `%LocalAppData%\PrintShop\Companion\` (the v0.5 directory).
3. Restore the old `%AppData%\PrintShop\Companion\queue.db` from `queue.db.v047.bak`.
4. Reinstall v0.4.7 (download from https://github.com/Arjunyadavsoma/PrintEXE/releases/tag/v0.4.7).
5. Launch v0.4.7 — it will read the restored `queue.db` and continue normally.

The v0.4.7 companion ignores the new `Security/` directory (it reads `device.dat` from the old `%AppData%` location). The device token is preserved.

## What's NOT preserved

- **In-flight PDF downloads** — if the v0.4.7 companion was mid-download when v0.5 launched, the `.tmp` file is in the old `%Temp%` location and v0.5 doesn't know about it. The job is re-downloaded from the cloud.
- **Log history** — the v0.5 logs go to the new `Logs/` directory. The old logs stay in the old location for reference but aren't continued.

## Files

- `docs/UPGRADE_FROM_V047.md` (this document)
- `src/PrintShopCompanion.Infrastructure/Storage/LocalStorageLayout.cs` — path resolution + migration
- `src/PrintShopCompanion.Infrastructure/Storage/QueueKeyStore.cs` — SQLCipher key generation
- `src/PrintShopCompanion.Infrastructure/Queue/SqliteJobQueue.cs` — schema migration on first open
