# Delivery Protocol — PrintEXE v0.5

The transfer protocol for getting a customer's PDF from the website to the shopkeeper's Windows PC.

## Two delivery modes

### Mode 1: Cloud fallback (default, shipped in v0.5.0)

The customer's PDF is uploaded to Supabase Storage. The companion downloads it via a presigned URL. This is the existing v0.4.7 flow, with three new additions:

1. **Receipt acknowledgement:** after the companion verifies the SHA-256 of the downloaded PDF, it sends `POST /api/v1/agents/jobs/{id}/received` so the backend knows the file is safely on the shop PC.
2. **Cloud cleanup timer:** the backend sets `cloudDeleteAfter = receivedAt + 15 min`. A cron worker deletes the Supabase object when the timer fires.
3. **Local retention timer:** the companion sets `localDeleteAfter = printedAt + 60 min`. A local cleanup manager deletes the PDF when the timer fires.

### Mode 2: WebRTC direct delivery (v0.5.1+, not shipped)

The customer's PDF is streamed directly from the Next.js backend to the companion over a WebRTC DataChannel, bypassing Supabase Storage entirely. Socket.IO is used only for signaling. This requires STUN/TURN infrastructure and is documented in `docs/WEBRTC_IMPLEMENTATION.md` (future).

## End-to-end flow (cloud fallback)

```
1. Customer uploads PDF on the website
   → POST /api/v1/orders/{id}/files (multipart)
   → Next.js stores in Supabase Storage
   → creates PrintFile row (storageStatus = cloud_available)
   → creates PrintJob row (deliveryStatus = created, printingStatus = queued)
   → emits 'job.dispatch' via Socket.IO with presigned downloadUrl

2. PrintEXE (running on the shopkeeper's PC) receives 'job.dispatch'
   → enqueues in local SQLite (deliveryStatus = cloud_staged)
   → logs "Job <id> dispatched via Socket.IO"

3. JobProcessor dequeues the job
   → HTTP GET <downloadUrl> (presigned, 15-min TTL)
   → streams to %LocalAppData%\PrintShop\Companion\Jobs\incoming\<jobId>.pdf.tmp
   → computes SHA-256 of the .tmp file
   → if checksum matches expected → atomic rename to Jobs\queued\<jobId>.pdf
   → if checksum mismatch → delete .tmp, log error, mark delivery_failed, retry
   → POST /api/v1/agents/jobs/{id}/received {checksum, fileSize, receivedAt}
     (idempotent — the backend sets deliveryStatus = received, storageStatus =
     local_verified, cloudDeleteAfter = now + 15 min)
   → marks local deliveryStatus = received

4. JobProcessor submits to spooler
   → POST /api/v1/agents/jobs/{id}/status {status:"printing", printerName}
     (atomic claim — if skipped, another agent got it; cancel locally)
   → marks local printingStatus = printing
   → SubmitAsync to Windows spooler (SumatraPDF)
   → waits up to PrintConfirmTimeout (default 5 min) for spooler confirmation

5. Spooler confirms physical print
   → POST /api/v1/agents/jobs/{id}/status {status:"completed"}
   → marks local printingStatus = completed, localDeleteAfter = now + 60 min
   → logs "Job <id> completed"

6. (15 min later) Cloud cleanup worker
   → backend cron route finds job where cloudDeleteAfter < now AND storageStatus = cloud_delete_pending
   → deletes Supabase Storage object
   → sets storageStatus = cloud_deleted
   → keeps PrintJob + PrintFile rows (business history)

7. (60 min after completed) Local cleanup
   → FileRetentionManager finds job where localDeleteAfter < now
   → deletes %LocalAppData%\PrintShop\Companion\Jobs\completed\<jobId>.pdf
   → sets local storageStatus = local_deleted
   → keeps PrintJobRecord in SQLite (business history)
```

## Receipt acknowledgement (Phase 4)

The `POST /api/v1/agents/jobs/{id}/received` route is the **durable receipt acknowledgement**. It's the gate for cloud deletion — the backend NEVER deletes the Supabase object before this is called.

### Request

```http
POST /api/v1/agents/jobs/{id}/received HTTP/1.1
Authorization: Bearer <deviceToken>
Content-Type: application/json

{
  "checksum": "sha256:<hex>",
  "fileSize": 1234567,
  "receivedAt": "2026-11-08T12:34:56.789Z"
}
```

### Response

```json
{
  "success": true,
  "data": {
    "jobId": "...",
    "deliveryStatus": "received",
    "storageStatus": "local_verified",
    "cloudDeleteAfter": "2026-11-08T12:49:56.789Z"
  }
}
```

### Idempotency

The route is idempotent on `(jobId, checksum)`. If called twice with the same checksum, the second call is a no-op (returns the existing `cloudDeleteAfter`). If called with a different checksum, the backend rejects it (409 Conflict) — the file has been tampered with.

### Atomic local persistence

The companion does NOT call `/received` until the local file is atomically renamed from `.tmp` to `.pdf`. This guarantees:
- If the companion crashes during download → no `/received` call → backend keeps the cloud copy → next start retries.
- If the companion crashes after `/received` but before printing → the cloud copy is scheduled for deletion (15 min) but the local copy is in the encrypted SQLite queue → JobProcessor picks it up on next start.

## Transfer protocol fields (for v0.5.1+ WebRTC)

Each transfer will have:

| Field | Purpose |
|---|---|
| `jobId` | The PrintJob ID |
| `transferId` | Unique per transfer attempt (UUID) |
| `deliveryAttemptId` | Incremental per job (1, 2, 3...) — idempotent on this |
| `agentId` | The PrintAgent ID |
| `fileSize` | Total bytes |
| `checksum` | SHA-256 of the complete file |
| `chunkSize` | Bytes per chunk (default 64 KB) |
| `chunkSequence` | 0, 1, 2, ... |
| `transferOffset` | Byte offset for resume |
| `transferExpiry` | ISO timestamp — transfer must complete before this |
| `authContext` | Short-lived JWT signed by the backend, scoped to this transfer |

The protocol supports:
1. Transfer initialization (backend → agent: `delivery.attempt` event)
2. Short-lived authorization (JWT with 5-min TTL, scoped to one transfer)
3. Chunk delivery (agent ↔ backend: chunked DataChannel messages)
4. Chunk validation (per-chunk SHA-256)
5. Resume after interruption (transfer offset)
6. Final checksum verification (full-file SHA-256)
7. Atomic local file persistence (`.tmp` → `.pdf` rename)
8. Durable receipt acknowledgement (`POST /received`)
9. Transfer completion (backend marks `deliveryStatus = received`)

## What's NOT assumed

- **Sending a transfer notification ≠ file received.** The companion must confirm the entire file is present, validated, and persisted before the backend considers it delivered.
- **WebSocket connect ≠ agent ready.** The backend uses server-observed heartbeat freshness, not socket connection state, to decide whether to attempt direct delivery.
- **Presigned URL generated ≠ file delivered.** The companion must call `/received` to gate cloud deletion — generating a URL is not enough.

## Error handling

| Failure | Recovery |
|---|---|
| Network error during download | Retry up to 5 times with exponential backoff. If still failing, mark `delivery_failed` and surface to the shopkeeper in the UI. |
| SHA-256 mismatch | Delete the `.tmp` file, log the mismatch, mark `delivery_failed`. Do NOT retry automatically (the cloud copy might be corrupt). |
| `/received` call fails | The cloud copy is NOT deleted (15-min timer never starts). The companion retries `/received` on the next sync cycle. |
| Spooler submit fails | Mark `printing_failed`, retry up to `MaxAttempts` (5). If still failing, mark `failed` permanently and surface in UI. |
| Spooler doesn't confirm within 5 min | Mark `UncertainSpooled` — operator must visually verify and reconcile via the UI. Never auto-reprint. |
| Companion crashes during download | On restart, the `.tmp` file is detected + deleted (incomplete). The job is re-dispatched via the polling fallback. |
| Companion crashes after `/received` | On restart, the local file is in `Jobs/queued/` and the SQLite queue has `deliveryStatus = received`. JobProcessor prints it normally. |
| Companion crashes during printing | On restart, the queue's `ReplayInFlightAsync` finds jobs in `printing` state and resubmits to the spooler. The backend's atomic claim prevents double-print (the spooler job already exists). |

## Files

- `src/PrintShopCompanion.Core/Contracts/IPrintShopBackend.cs` — `MarkJobReceivedAsync` declaration
- `src/PrintShopCompanion.Infrastructure/Backend/HttpPrintShopBackend.cs` — implementation
- `src/PrintShopCompanion.Infrastructure/Queue/JobProcessor.cs` — calls `/received` after download
- `tests/PrintShopCompanion.Infrastructure.Tests/ReceiptAcknowledgementTests.cs` — 3 tests
- `docs/DATABASE_MIGRATIONS.md` — backend route file (patch for printshop-saas)
