# Implementation Roadmap — PrintEXE v0.5

The 12-phase plan with current status, what's done, and what's deferred. See `PHASE_STATUS.md` for the live status.

## Phase 0 — Audit ✅
- Both repos inspected (PrintEXE @ v0.4.7, printshop-saas cloned)
- Schema, API routes, Socket.IO events, storage adapters documented

## Phase 1 — Architecture docs ✅
- 9 docs created (LOCAL_FIRST_ARCHITECTURE, DELIVERY_PROTOCOL, FILE_RETENTION_POLICY, SECURITY_MODEL, DATABASE_MIGRATIONS, OFFLINE_RECOVERY, UPGRADE_FROM_V047, IMPLEMENTATION_ROADMAP, PHASE_STATUS)

## Phase 2 — SQLCipher + DPAPI ✅
- `QueueKeyStore.cs` — DPAPI-protected SQLCipher key
- `LocalStorageLayout.cs` — new `%LocalAppData%\PrintShop\Companion\{Security,Database,Jobs,Temp,Logs}` structure
- `SqliteJobQueue` + `SqlitePendingOpsQueue` open with `Password=<key>`
- v0.4.7 → v0.5 migration: old `queue.db` re-encrypted, old `device.dat` moved
- **Deferred:** Windows testing (SQLCipher native DLL bundling)

## Phase 3 — State machines ✅
- `DeliveryStatus`, `StorageStatus`, `PrintingStatus` enums
- `PrintJobRecord` extended
- SQLite schema extended (additive columns)
- **Deferred:** backend Prisma migration (user applies to printshop-saas)

## Phase 4 — Receipt acknowledgement ✅
- `MarkJobReceivedAsync` on `IPrintShopBackend`
- Atomic local persistence (`.tmp` → `.pdf` rename)
- 3 unit tests
- **Deferred:** backend route (user applies to printshop-saas)

## Phase 5 — Cloud fallback ⚠️
- Existing HTTP polling + presigned URL download works
- `IPdfTransferTransport` abstraction added (cloud + WebRTC stub)
- **Deferred:** `PrintDeliveryAttempt` model, `cloud_staged` backend state

## Phase 6 — File retention ✅ (local) / ⚠️ (cloud)
- `FileRetentionManager` — 60-min local cleanup
- `StorageOptions.LocalRetentionMinutes`, `MaxLocalStorageBytes`
- 4 unit tests
- **Deferred:** backend cloud cleanup cron route

## Phase 7 — Socket.IO ⚠️
- Existing flow works for cloud fallback
- **Deferred:** new `delivery.attempt`, `transfer.progress`, `receipt.confirmed` events

## Phase 8 — WebRTC ❌ (deferred to v0.5.1+)
- Transport abstraction in place
- WebRTC implementation deferred (requires STUN/TURN + Windows testing)

## Phase 9 — UI ✅ (companion) / ❌ (website)
- New `DeliveryView` + `DeliveryViewModel` with connection, downloads, disk usage, cleanup
- Settings extended with retention / max storage / concurrent downloads
- **Deferred:** website customer order tracking (requires backend changes)

## Phase 10 — Testing ⚠️
- 38 unit tests pass on Linux (31 existing + 7 new)
- **Deferred:** E2E tests on Windows

## Phase 11 — Installer ⚠️
- Build workflow unchanged
- **Deferred:** upgrade testing on Windows

## Phase 12 — Release docs ✅ (companion) / ⚠️ (backend)
- Companion deployment docs complete
- **Deferred:** backend deployment (user applies patches)

## What's ready for v0.5.0 release

The companion is ready for v0.5.0 with **cloud-fallback-only delivery** + SQLCipher encryption + local file retention + receipt acknowledgement. WebRTC direct delivery is the v0.5.1 enhancement.

## What the user needs to do for full v0.5 functionality

1. **Apply the backend patches** from `docs/DATABASE_MIGRATIONS.md` to the printshop-saas repo.
2. **Run `bun run db:push`** on the printshop-saas Prisma schema.
3. **Add the Vercel cron** for cloud cleanup (`/api/cron/cleanup-cloud-storage`).
4. **Deploy the printshop-saas** changes to Vercel.
5. **Push v0.5.0 tag** on the PrintEXE repo — the GitHub Actions build will produce the portable ZIP.
6. **Validate on a Windows machine** — see `docs/PRODUCTION_DEPLOYMENT.md`.
