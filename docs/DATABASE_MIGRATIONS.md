# Database Migrations — PrintEXE v0.5

This document describes the Prisma schema additions needed on the printshop-saas backend to support v0.5 features. The user must apply these to the printshop-saas repo and run `bun run db:push` (or `prisma migrate deploy`).

## Schema additions (additive — no breaking changes)

```prisma
// ─── v0.5: Independent state machines + delivery tracking ───────────

model PrintJob {
  // ... existing fields kept for backward compat ...

  // Three independent state machines (v0.5):
  deliveryStatus        String    @default("created")
  //   created → direct_attempt → cloud_staged → transferring → received → delivery_failed
  storageStatus         String    @default("uploading")
  //   uploading → cloud_available → local_verified → cloud_delete_pending → cloud_deleted → local_deleted
  // (existing `status` field kept; mirrors printingStatus for backward compat with v0.4.7 agents)

  // File integrity + receipt:
  checksum              String?   // SHA-256 of the PDF (set when the customer uploads)
  fileSize              Int?      // bytes
  receivedAt           DateTime? // when the companion confirmed local receipt

  // Cleanup timers:
  cloudDeleteAfter      DateTime? // 15 min after receivedAt
  localDeleteAfter      DateTime? // 60 min after terminal print state (companion-side)

  // Delivery attempt tracking:
  deliveryAttemptCount  Int       @default(0)
  lastDeliveryError     String?

  // Relation to delivery attempts:
  deliveryAttempts      PrintDeliveryAttempt[]

  @@index([deliveryStatus])
  @@index([storageStatus])
  @@index([cloudDeleteAfter])
}

model PrintDeliveryAttempt {
  id              String    @id @default(uuid())
  printJobId      String
  agentId         String?
  attemptId       Int       // 1, 2, 3... per job (incremented server-side)
  deliveryMode    String    // 'direct' | 'cloud'
  status          String    @default("initiated")
  //   initiated → signaling → transferring → completed | failed | cancelled
  startedAt       DateTime  @default(now())
  completedAt     DateTime?
  errorCode       String?
  bytesTransferred Int     @default(0)
  printJob        PrintJob  @relation(fields: [printJobId], references: [id])

  @@unique([printJobId, attemptId])  // idempotent on (jobId, attemptId)
  @@index([printJobId])
  @@index([agentId])
}

model PrintAgent {
  // ... existing fields kept ...

  // v0.5 additions:
  connectionStatus        String    @default("offline")
  //   offline → connecting → connected → disconnected → revoked
  supportedTransferModes   String    @default("cloud")
  //   comma-separated: "cloud" (v0.5.0), "cloud,direct" (v0.5.1+ with WebRTC)
  availableStorageBytes    Int?
  appVersion               String?

  @@index([connectionStatus])
}
```

## Migration steps

1. **Apply the schema additions** to `prisma/schema.prisma` in the printshop-saas repo.
2. **Run `bun run db:push`** (this adds the new columns with their default values; existing rows get the defaults — `deliveryStatus = "created"`, `storageStatus = "uploading"`, etc.).
3. **Verify the migration** with `SELECT deliveryStatus, storageStatus, COUNT(*) FROM "PrintJob" GROUP BY 1, 2;` — all existing jobs should show `created` / `uploading`.
4. **Backfill existing jobs:** existing `queued` jobs should be updated to `deliveryStatus = "cloud_staged"` (they're already in Supabase Storage, waiting for an agent):
   ```sql
   UPDATE "PrintJob" SET "deliveryStatus" = 'cloud_staged', "storageStatus" = 'cloud_available'
    WHERE status = 'queued';
   ```
5. **Deploy the new API routes** (see below).

## New API routes to add to printshop-saas

### `src/app/api/v1/agents/jobs/[id]/received/route.ts`

```typescript
import { z } from 'zod';
import { withDeviceAuth } from '@/lib/api/with-auth';
import { repos } from '@/lib/repositories';
import { ok, fail, toAppError } from '@/lib/api/envelope';

const ReceivedSchema = z.object({
  checksum: z.string().regex(/^sha256:[a-f0-9]{64}$/),
  fileSize: z.number().int().positive(),
  receivedAt: z.string().datetime(),
});

export const POST = withDeviceAuth<{ id: string }>(async (req, ctx, { agent, shopId }) => {
  try {
    const { id } = await ctx.params;
    const body = ReceivedSchema.parse(await req.json());
    const job = await repos.printJobs.findByIdAndShop(id, shopId);
    if (!job) return fail({ code: 'NOT_FOUND', message: 'Job not found' });

    // Idempotent: if already received, return existing state.
    if (job.deliveryStatus === 'received') {
      return ok({
        jobId: id,
        deliveryStatus: 'received',
        storageStatus: job.storageStatus,
        cloudDeleteAfter: job.cloudDeleteAfter,
        skipped: true,
      });
    }

    // Verify the checksum matches what the server stored (if set).
    if (job.checksum && job.checksum !== body.checksum) {
      return fail({ code: 'CONFLICT', message: `Checksum mismatch: expected ${job.checksum}, got ${body.checksum}` }, 409);
    }

    const now = new Date(body.receivedAt);
    const cloudRetentionMin = parseInt(process.env.CLOUD_RETENTION_MINUTES ?? '15', 10);
    const cloudDeleteAfter = new Date(now.getTime() + cloudRetentionMin * 60_000);

    await repos.printJobs.update(id, {
      deliveryStatus: 'received',
      storageStatus: 'local_verified',
      receivedAt: now,
      cloudDeleteAfter,
    });

    return ok({
      jobId: id,
      deliveryStatus: 'received',
      storageStatus: 'local_verified',
      cloudDeleteAfter: cloudDeleteAfter.toISOString(),
    });
  } catch (cause) {
    return fail(toAppError(cause));
  }
});
```

### `src/app/api/cron/cleanup-cloud-storage/route.ts`

```typescript
import { NextRequest, NextResponse } from 'next/server';
import { getContainer } from '@/lib/container';
import { repos } from '@/lib/repositories';
import { ok, fail } from '@/lib/api/envelope';

// Vercel cron — protected by CRON_SECRET env var.
// Add to vercel.json:
//   "crons": [{ "path": "/api/cron/cleanup-cloud-storage", "schedule": "*/5 * * * *" }]
export async function GET(req: NextRequest) {
  const authHeader = req.headers.get('authorization');
  if (authHeader !== `Bearer ${process.env.CRON_SECRET}`) {
    return NextResponse.json({ error: 'unauthorized' }, { status: 401 });
  }
  const container = getContainer();
  const now = new Date();
  let deletedCount = 0;
  let failedCount = 0;

  // Find jobs where cloud cleanup is due.
  // NOTE: requires a new repository method `findCloudCleanupDue` — add it.
  const due = await repos.printJobs.findCloudCleanupDue(now, 100);
  for (const job of due) {
    try {
      const file = await container.db.findOne<{ storageKey: string }>('printFile', { id: job.fileId });
      if (!file) continue;
      // Check no other active job references the same storageKey.
      const siblings = await repos.printJobs.findByStorageKey(file.storageKey);
      const active = siblings.filter((s) => s.id !== job.id && s.storageStatus !== 'cloud_deleted');
      if (active.length > 0) {
        // Skip — another job still needs this object.
        continue;
      }
      await container.storage.deleteObject(file.storageKey);
      await repos.printJobs.update(job.id, { storageStatus: 'cloud_deleted' });
      deletedCount++;
    } catch (err) {
      console.error(`cleanup failed for job ${job.id}:`, err);
      failedCount++;
    }
  }
  return ok({ deleted: deletedCount, failed: failedCount, checked: due.length });
}
```

### Repository additions (`src/lib/repositories/print-jobs.ts`)

```typescript
async findCloudCleanupDue(now: Date, limit: number): Promise<PrintJob[]> {
  return prisma.printJob.findMany({
    where: {
      cloudDeleteAfter: { lt: now },
      storageStatus: 'cloud_delete_pending',
    },
    take: limit,
    orderBy: { cloudDeleteAfter: 'asc' },
  });
}

async findByStorageKey(storageKey: string): Promise<PrintJob[]> {
  const files = await prisma.printFile.findMany({ where: { storageKey } });
  if (files.length === 0) return [];
  return prisma.printJob.findMany({
    where: { fileId: { in: files.map((f) => f.id) } },
  });
}
```

## Companion-side migration (v0.4.7 → v0.5.0)

The companion handles its own migration automatically on first launch:

1. **Old SQLite at `%AppData%\PrintShop\Companion\queue.db`** is detected.
2. The companion reads it (no password), copies the rows to the new SQLCipher-encrypted SQLite at `%LocalAppData%\PrintShop\Companion\Database\queue.db`, adds the new columns with defaults.
3. The old file is renamed to `queue.db.v047.bak` (not deleted — the user can roll back).
4. **Old `device.dat`** is moved from `%AppData%\PrintShop\Companion\device.dat` to `%LocalAppData%\PrintShop\Companion\Security\device.dat`.
5. The companion starts normally.

No user intervention required. The shopkeeper does NOT need to re-pair.

## Verification

After applying the backend migration:
```bash
# Verify the new columns exist:
psql $DATABASE_URL -c '\d "PrintJob"' | grep -E 'delivery|storage|receivedAt|cloudDeleteAfter'

# Verify existing jobs were backfilled:
psql $DATABASE_URL -c "SELECT deliveryStatus, storageStatus, COUNT(*) FROM \"PrintJob\" GROUP BY 1, 2;"
# Expected: all queued jobs show cloud_staged / cloud_available
```

After the companion upgrade:
- The companion log should show: `Migrated v0.4.7 queue.db to v0.5 SQLCipher-encrypted queue.db (N jobs)`
- The new `Database/queue.db` should be unreadable without the key (test by trying to open with `sqlite3` — should fail with "file is not a database").
