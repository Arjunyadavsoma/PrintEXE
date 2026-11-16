# Local-First Architecture — PrintEXE v0.5

This document describes the **target architecture** for PrintEXE v0.5 — a local-first intelligent document delivery system. It is the canonical reference for all other v0.5 docs.

## The shift from v0.4.7 to v0.5

| Aspect | v0.4.7 (current) | v0.5 (target) |
|---|---|---|
| Document delivery | Cloud-only (presigned URL download) | Direct (WebRTC) when possible, cloud fallback always |
| Local SQLite | Plain file at `%AppData%\PrintShop\Companion\queue.db` | SQLCipher-encrypted at `%LocalAppData%\PrintShop\Companion\Database\queue.db` |
| Local PDF cache | `%Temp%\PrintShop\Companion\pdfs\` — deleted on success/failure | `%LocalAppData%\PrintShop\Companion\Jobs\{incoming,queued,printing,completed}\` — 60-min retention after terminal state |
| State machine | Single `PrintJob.status` (queued/printing/completed/failed) | Three independent states: `deliveryStatus`, `printingStatus`, `storageStatus` |
| Receipt acknowledgement | None — `completed` is reported but no separate receipt | `POST /agents/jobs/{id}/received` after SHA-256 verified — gates cloud deletion |
| Cloud cleanup | None — presigned URLs expire but the object stays in Supabase | Server-side cron worker deletes 15 min after verified local receipt |
| WebRTC | Not implemented | Transport abstraction; WebRTC stub for v0.5.1+ |

## System diagram

```
┌─────────────────────────────────────────────────────────────────────┐
│  Customer (browser)                                                │
│  - Uploads PDF                                                      │
│  - Sees truthful delivery state (uploading → waiting_for_shop_pc →  │
│    downloading → received → printing → completed)                   │
└──────────────────────────┬──────────────────────────────────────────┘
                           │ HTTPS (multipart upload)
                           ▼
┌─────────────────────────────────────────────────────────────────────┐
│  Next.js backend (Vercel)                                           │
│                                                                     │
│  POST /api/v1/orders/{id}/files  ──── customer upload endpoint      │
│  ┌─────────────────────────────────────────────────────────────┐  │
│  │  Delivery Coordinator (NEW in v0.5)                          │  │
│  │  - Checks if any paired agent for this shop is online        │  │
│  │  - If yes → emits delivery.attempt via Socket.IO             │  │
│  │  - If no  → stores in Supabase, sets deliveryStatus=cloud_staged│
│  └─────────────────────────────────────────────────────────────┘  │
│                                                                     │
│  Storage adapter: Supabase Storage (presigned URLs, 15-min TTL)     │
│  Database: PostgreSQL (Supabase) — PrintJob, PrintFile,            │
│            PrintDeliveryAttempt, PrintAgent                         │
│                                                                     │
│  Socket.IO mini-service (port 3003 via Caddy gateway):              │
│  - emits job.dispatch, delivery.attempt, agent.revoked              │
│  - listens for auth {token: jwt}                                    │
│                                                                     │
│  Cron route (NEW in v0.5):                                          │
│  - GET /api/cron/cleanup-cloud-storage (every 5 min)                │
│  - Finds jobs where cloudDeleteAfter < now AND storageStatus =     │
│    cloud_delete_pending → deletes Supabase object → sets            │
│    storageStatus = cloud_deleted                                    │
└──────────────────────────┬──────────────────────────────────────────┘
                           │
                           │ Direct delivery (WebRTC DataChannel) — v0.5.1+
                           │ OR cloud fallback (presigned URL download) — v0.5.0
                           ▼
┌─────────────────────────────────────────────────────────────────────┐
│  Windows companion (PrintEXE)                                       │
│                                                                     │
│  %LocalAppData%\PrintShop\Companion\                                │
│  ├─ Security\                                                       │
│  │   ├─ device.dat     (DPAPI-encrypted device token)               │
│  │   └─ queue.key      (DPAPI-encrypted SQLCipher key)              │
│  ├─ Database\                                                       │
│  │   └─ queue.db       (SQLCipher-encrypted SQLite)                 │
│  ├─ Jobs\                                                           │
│  │   ├─ incoming\       (transfers in progress, .pdf.tmp)            │
│  │   ├─ queued\         (downloaded, awaiting print)                │
│  │   ├─ printing\       (submitted to spooler)                       │
│  │   └─ completed\      (terminal, awaiting cleanup)                │
│  ├─ Temp\transfers\     (chunked transfer temp files)               │
│  └─ Logs\companion.log                                              │
│                                                                     │
│  Components:                                                        │
│  - SocketIoRealtimeClient — receives job.dispatch + delivery.attempt│
│  - IPdfTransferTransport  — abstraction (cloud or direct)            │
│  - JobProcessor          — atomic claim + spooler + report          │
│  - FileRetentionManager   — 60-min local cleanup                    │
│  - SyncEngine             — 15s sync poll (existing)                │
└─────────────────────────────────────────────────────────────────────┘
```

## The 3 independent state machines

### Delivery state machine

```
                    ┌──────────────┐
                    │   created    │  (job created on the server)
                    └──────┬───────┘
                           │
              ┌────────────┼─────────────┐
              │ online agent             │ offline agent
              ▼                          ▼
       ┌──────────────┐           ┌──────────────┐
       │ direct_attempt│           │ cloud_staged │
       └──────┬───────┘           └──────┬───────┘
              │ WebRTC signaling           │ presigned URL ready
              ▼                          │
       ┌──────────────┐                  │
       │  transferring│◄─────────────────┘
       └──────┬───────┘
              │ local receipt confirmed
              ▼
       ┌──────────────┐
       │   received   │  ←── triggers cloud cleanup timer (15 min)
       └──────────────┘

       (any state can transition to delivery_failed on error)
```

### Printing state machine (companion-side)

```
       ┌────────┐
       │ queued │  (in local SQLite, awaiting JobProcessor dequeue)
       └───┬────┘
           │ atomic claim via POST /agents/jobs/{id}/status {status:"printing"}
           ▼
       ┌────────┐
       │claimed │  (server marked as 'printing', spooler submit pending)
       └───┬────┘
           │ SubmitAsync to Windows spooler
           ▼
       ┌─────────┐
       │printing │  (waiting for spooler confirmation, up to PrintConfirmTimeout)
       └────┬────┘
            │
       ┌────┴────┐
       ▼         ▼
   ┌──────────┐ ┌───────┐
   │completed │ │ failed│
   └──────────┘ └───────┘
       │              │
       ▼              ▼
   [localDeleteAfter = now + 60 min]
```

### Storage state machine

```
   ┌──────────────┐
   │  uploading   │  (customer's browser is uploading to the server)
   └──────┬───────┘
          │ upload complete, object in Supabase Storage
          ▼
   ┌──────────────┐
   │cloud_available│
   └──────┬───────┘
          │ companion confirms local receipt (POST /agents/jobs/{id}/received)
          ▼
   ┌──────────────┐
   │ local_verified│
   └──────┬───────┘
          │ cloud cleanup timer fires (15 min after receipt)
          ▼
   ┌────────────────────┐
   │ cloud_delete_pending│
   └──────┬─────────────┘
          │ cron worker deletes Supabase object
          ▼
   ┌──────────────┐
   │ cloud_deleted│
   └──────┬───────┘
          │ local retention timer fires (60 min after terminal print state)
          ▼
   ┌──────────────┐
   │ local_deleted│
   └──────────────┘
```

## Why three state machines instead of one

The v0.4.7 single `PrintJob.status` conflated three independent lifecycles:

- A job can be **delivered but not yet printed** (e.g. agent received the PDF but hasn't claimed it for printing yet).
- A job can be **printed but the cloud copy still exists** (e.g. the cleanup worker hasn't run yet).
- A job can be **delivery_failed but still printed** (e.g. WebRTC failed, cloud fallback succeeded — the original `direct_attempt` is failed, but `printing` can still succeed).

Splitting into three states lets the customer see the truth:
- "Waiting for shop PC" — `deliveryStatus = cloud_staged`, `printingStatus = queued`
- "Received by shop PC" — `deliveryStatus = received`, `printingStatus = queued`
- "Printing" — `deliveryStatus = received`, `printingStatus = printing`
- "Completed" — `deliveryStatus = received`, `printingStatus = completed`, `storageStatus = local_verified`

## Files in this phase

| File | Purpose |
|---|---|
| `src/PrintShopCompanion.Core/Models/DeliveryStatus.cs` | New enum |
| `src/PrintShopCompanion.Core/Models/StorageStatus.cs` | New enum |
| `src/PrintShopCompanion.Core/Models/PrintJobRecord.cs` | Extended with new fields |
| `src/PrintShopCompanion.Infrastructure/Storage/FileRetentionManager.cs` | New — 60-min local cleanup |
| `src/PrintShopCompanion.Infrastructure/Storage/QueueKeyStore.cs` | New — DPAPI-protected SQLCipher key |
| `src/PrintShopCompanion.Infrastructure/Storage/LocalStorageLayout.cs` | New — path resolution for the new layout |
| `src/PrintShopCompanion.Core/Contracts/IPrintShopBackend.cs` | Extended with `MarkJobReceivedAsync` |
| `src/PrintShopCompanion.Infrastructure/Backend/HttpPrintShopBackend.cs` | Implements `MarkJobReceivedAsync` |
| `src/PrintShopCompanion.App/Views/DeliveryView.xaml` | New — delivery dashboard |
| `src/PrintShopCompanion.App/ViewModels/DeliveryViewModel.cs` | New — VM for delivery dashboard |

## What's intentionally deferred to v0.5.1+

- **WebRTC direct delivery** — requires STUN/TURN infrastructure + Windows WebRTC library + real-world testing.
- **Customer-side offline upload queue** — requires service-worker changes on the website.
- **Resumable downloads (HTTP Range)** — the current download is resumable across restarts but not across a single interrupted HTTP request.

These are documented as future work, not silently omitted.
