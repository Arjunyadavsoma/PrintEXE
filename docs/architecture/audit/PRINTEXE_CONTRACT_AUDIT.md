# PrintEXE Contract Audit — Cross-Repository Verification

## Methodology
Both repositories were inspected from source. Every shared endpoint was compared field-by-field: HTTP method, URL, authentication, request payload, response payload, status codes, idempotency behavior, and error handling.

## Contract Match Summary

| # | Endpoint | PrintEXE file | SaaS file | Match |
|---|---|---|---|---|
| 1 | POST /api/v1/agents/token | HttpPrintShopBackend.cs:166 | agents/token/route.ts | ✅ |
| 2 | POST /api/v1/agents/heartbeat | HttpPrintShopBackend.cs:187 | agents/heartbeat/route.ts | ✅ |
| 3 | GET /api/v1/agents/jobs/pending | HttpPrintShopBackend.cs:219 | agents/jobs/pending/route.ts | ✅ |
| 4 | POST /api/v1/agents/jobs/{id}/status | HttpPrintShopBackend.cs:242 | agents/jobs/[id]/status/route.ts | ✅ |
| 5 | POST /api/v1/agents/jobs/{id}/received | HttpPrintShopBackend.cs:444 | agents/jobs/[id]/received/route.ts | ✅ |
| 6 | GET /api/v1/sync/pull | HttpPrintShopBackend.cs:380 | sync/pull/route.ts | ✅ |
| 7 | POST /api/v1/sync/push | HttpPrintShopBackend.cs:402 | sync/push/route.ts | ✅ |
| 8 | GET /api/v1/sync/status | HttpPrintShopBackend.cs:421 | sync/status/route.ts | ✅ |

## Detailed Contract Analysis

### 1. POST /api/v1/agents/token

**Backend zod schema:** Empty body (no validation needed).
**Backend response:** `{ accessToken, expiresIn, shopId }` wrapped in `{ success, data }`.

**PrintEXE request:** Empty body POST with `Authorization: Bearer <deviceToken>`.
**PrintEXE response parsing:** Reads `ApiEnvelope<AgentTokenResponseDto>` with fields `accessToken`, `expiresIn`, `shopId`.

**Verdict:** ✅ Match. Both sides agree on empty request + envelope-wrapped response with accessToken + expiresIn.

### 2. POST /api/v1/agents/heartbeat

**Backend zod schema:** `{ printerName?, version?, os? }` — all optional.
**Backend response:** `{ agentId, online, ts }` wrapped in envelope.

**PrintEXE request:** `{ printerName, version, os }` (all strings).
**PrintEXE response parsing:** Reads `ApiEnvelope<AgentHeartbeatResponseDto>` with `agentId`, `online`, `ts`.

**Verdict:** ✅ Match.

### 3. GET /api/v1/agents/jobs/pending

**Backend response:** `{ jobs: [{ jobId, fileId, orderId, filename, downloadUrl, copies, colorMode, duplex, paperSize, pageRange }], agentId }` wrapped in envelope.

**PrintEXE parsing:** Reads `ApiEnvelope<PendingJobsEnvelopeDto>` with `jobs` list of `PrintJobDto` + `agentId`.

**Verdict:** ✅ Match. All field names and types align.

### 4. POST /api/v1/agents/jobs/{id}/status

**Backend zod schema:** `{ status: enum("printing"|"completed"|"failed"), lastError?: string, printerName?: string }`.
**Backend response:** `{ jobId, status, applied: true }` or `{ jobId, skipped: true, reason: "..." }`.

**PrintEXE request:** `AgentJobStatusRequestDto { status, printerName?, lastError? }`.
**PrintEXE response parsing:** `ApiEnvelope<AgentJobStatusResponseDto>` with `jobId`, `status`, `applied`, `skipped`, `reason`.

**Verdict:** ✅ Match. The status enum values (`printing`, `completed`, `failed`) match exactly.

### 5. POST /api/v1/agents/jobs/{id}/received

**Backend zod schema (CRITICAL — was previously mismatched):**
```typescript
const ReceiptSchema = z.object({
  deliveryAttemptId: z.string().uuid().optional(),
  transferId: z.string().uuid().optional(),
  checksum: z.string().regex(/^[a-f0-9]{64}$/i).optional(),  // PURE HEX, no prefix
  fileSize: z.number().int().positive().optional(),
  receivedAt: z.string().datetime().optional(),
});
```

**Backend response:** `{ jobId, received: true, cleanupScheduled: true, cleanupScheduledAt: "...", message: "..." }`.

**PrintEXE request (FIXED in v0.5.1):**
```csharp
var hexChecksum = checksum.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
    ? checksum.Substring("sha256:".Length)  // Strip prefix — backend expects pure hex
    : checksum;
body = new JobReceivedRequestDto { Checksum = hexChecksum, FileSize = fileSize, ReceivedAt = ... };
```

**PrintEXE response parsing (FIXED in v0.5.1):** Reads `ApiEnvelope<JobReceivedResponseDto>` with `jobId`, `received`, `cleanupScheduled`, `cleanupScheduledAt`, `message`.

**Verdict:** ✅ Match (after v0.5.1 fix). The original v0.5.0 code sent `sha256:`-prefixed checksum which was REJECTED by the backend zod schema. Fixed by stripping the prefix before sending.

### 6. GET /api/v1/sync/pull

**Backend:** `GET /api/v1/sync/pull?cursor=ISO8601&limit=100` — cursor defaults to epoch, limit capped at 500.
**Backend response:** `{ changes: [{ entity, id, action, data, revision, updatedAt }], nextCursor, hasMore }`.

**PrintEXE:** `SyncPullAsync(string? cursor, int limit)` — sends cursor + limit as query params.
**PrintEXE response:** `ApiEnvelope<SyncPullResponseDto>` with `changes`, `nextCursor`, `hasMore`.

**Verdict:** ✅ Match.

### 7. POST /api/v1/sync/push

**Backend zod schema:** `{ changes: [{ entity, id, action: "upsert"|"delete", data, clientRevision, baseRevision }] }` — max 100 changes.
**Backend response:** `{ results: [{ id, accepted, serverRevision, conflict }] }`.

**PrintEXE:** `SyncPushAsync(object changes)` — sends the changes object.
**PrintEXE response:** `ApiEnvelope<SyncPushResponseDto>` with `results`.

**Verdict:** ✅ Match. Only `Customer` entity is pushable in v1.

### 8. GET /api/v1/sync/status

**Backend response:** `{ deviceId, shopId, syncCursor, lastSyncAt, lastPushAt, agentName, isOnline }`.

**PrintEXE response:** `ApiEnvelope<SyncStatusResponseDto>` with all matching fields.

**Verdict:** ✅ Match.

## Authentication Contract

| Aspect | Backend | PrintEXE | Match |
|---|---|---|---|
| Auth header | `Authorization: Bearer <deviceToken>` | Sets `Authorization: Bearer <deviceToken>` via `AuthenticationHeaderValue` | ✅ |
| Token verification | HMAC-SHA256("print-agent-salt", token) → lookup PrintAgent.tokenHash | Sends raw token in Bearer header | ✅ |
| Token expiry | No explicit expiry on device tokens (revoked via DELETE /agents/{id}) | Handles 401 responses → shows "Device revoked" | ✅ |
| JWT for Socket.IO | POST /agents/token returns JWT (HS256, 15-min TTL) | Calls ExchangeDeviceTokenForJwtAsync, uses JWT for Socket.IO auth only | ✅ |
| Socket.IO auth | Verifies JWT via JWT_ACCESS_SECRET | Emits `auth { token: jwt }` after connect | ✅ |

## Findings

### F-001: Receipt checksum format (FIXED)
- **Severity:** P0 (was) → Fixed in v0.5.1
- **Was:** PrintEXE sent `"sha256:abc123..."` but backend expected pure hex `/^[a-f0-9]{64}$/i`
- **Fix:** PrintEXE now strips the `sha256:` prefix before sending
- **Verification:** Code inspection confirms `hexChecksum` logic in `HttpPrintShopBackend.cs:454`

### F-002: Receipt response DTO shape (FIXED)
- **Severity:** P1 (was) → Fixed in v0.5.1
- **Was:** PrintEXE expected `{ deliveryStatus, storageStatus, cloudDeleteAfter, skipped, reason }` but backend returns `{ received, cleanupScheduled, cleanupScheduledAt, message }`
- **Fix:** DTO updated to match backend response
- **Verification:** `JobReceivedResponseDto` in `PrintShopDto.cs` has `received`, `cleanupScheduled`, `cleanupScheduledAt`, `message`

### F-003: Socket.IO URL parameter
- **Severity:** P1
- **Finding:** PrintEXE must include `?XTransformPort=3003` in the Socket.IO URL. Without it, Caddy routes to the Next.js app (port 3000) which doesn't serve Socket.IO.
- **Status:** ✅ Correctly implemented — `Backend:SocketIoUrl` in appsettings.json includes the query parameter

### F-004: Device token never logged
- **Severity:** P1
- **Finding:** Device token must not appear in logs, UI, or diagnostics.
- **Status:** ✅ `DeviceTokenRedactor` Serilog enricher redacts any property containing "Token", "Secret", or "Password". The token is never displayed in the UI after pairing.

### F-005: No contract mismatch found
- **Finding:** All 8 shared endpoints have matching contracts after the v0.5.1 fixes. No additional mismatches were found in this audit.
