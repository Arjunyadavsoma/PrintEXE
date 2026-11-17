# PrintEXE ↔ PrintShop SaaS — Contract Compatibility Matrix (Phase 3)

**Date:** 2026-11-17
**Investigation method:** cloned `printshop-saas` (shallow, depth 30), read every route's actual TypeScript + zod schema + middleware + Prisma schema + Socket.IO server. The actual route implementation is authoritative (brief §2); the published contract is updated where it drifts.
**SaaS `main` HEAD:** `9f0e8ff1a190288a05651a3005387a20539f53d1` (newer than the `ffd0bb3` reported in Phase 2 — includes the rate-limit + build-script fixes).
**PrintEXE `main` HEAD:** `815d11c` (Phase 2 v1 contract implementation).

This matrix is the basis for the Phase 3 implementation work. Every discrepancy marked ❌ has been fixed in Phase 3 (see the commits that follow this doc).

---

## Critical findings — the 4 contract-vs-implementation mismatches that BREAK the Phase 2 client

These four were NOT visible from the published `COMPANION_V1_CONTRACT.md` alone — they only surface by reading the actual SaaS route code. My Phase 2 `HttpPrintShopBackend` + `CommandExecutor` were written against the contract document, not the implementation, so they desync on these four points. **Phase 3 fixes all four.**

### Mismatch 1 — `GET /companion/commands` response field names + payload type ❌→✅

| Aspect | Published contract §6 | Actual SaaS `companion/commands/route.ts` | Phase 2 C# | Phase 3 fix |
|---|---|---|---|---|
| Command id field | `id` | **`commandId`** | `id` | rename to `commandId` |
| Payload field | `payload` (JSON string) | `payload` (parsed `Record<string,unknown>`, `{}` on null/garbage) | `string` | accept `JsonElement` (object OR null) |
| `issuedAt` | not documented | **present** (ISO8601 of `createdAt`) | not parsed | add `IssuedAt` |
| Envelope | `{commands: [...]}` | `{agentId, commands: [...]}` | `commands` only | also parse `agentId` |

**Impact before fix:** every poll deserializes `commands` to an empty list (the `id` field is never populated → commands are silently dropped → no remote command ever executes).

### Mismatch 2 — `POST /companion/commands` request `result` field type ❌→✅

| Aspect | Published contract §7 | Actual SaaS zod | Phase 2 C# | Phase 3 fix |
|---|---|---|---|---|
| `result` field | JSON string | **`z.record(z.string(), z.unknown()).optional()`** — an OBJECT, not a string | `string?` (JSON-string) | send an `object` (deserialize the executor's `ResultJson` back to a `JsonElement` before sending) |

**Impact before fix:** every terminal result report (`succeeded`/`failed`) sends a string → zod rejects with 400 `VALIDATION_ERROR` → the backend never learns the command's outcome → the dashboard shows the command stuck in `executing` forever.

### Mismatch 3 — `POST /companion/commands` response shape ❌→✅

| Aspect | Published contract §7 | Actual SaaS | Phase 2 C# | Phase 3 fix |
|---|---|---|---|---|
| Success `data` | `{applied: true}` | **`{commandId, status, updatedAt}`** | `Applied` bool | replace with `{CommandId, Status, UpdatedAt}` |

**Impact before fix:** the poller's success check (`envelope.Data?.Applied`) always reads `false` → the poller logs "applied=false" on every successful report → confusing telemetry, but not a hard failure (the report still went through).

### Mismatch 4 — Pairing ticket validation + error codes ❌→✅

| Aspect | Published contract §2 | Actual SaaS zod + route | Phase 2 C# | Phase 3 fix |
|---|---|---|---|---|
| `deviceName` max length | 255 | **120** | 255 | lower to 120 |
| Expired ticket error code | `TICKET_EXPIRED` (401) | **`UNAUTHORIZED`** (401, message "Pairing ticket has expired...") | mapped `TICKET_EXPIRED` | map `UNAUTHORIZED` w/ "expired" message → "code expired" UI state |
| Consumed ticket error code | `TICKET_CONSUMED` (409) | **`PAIRING_TICKET_CONSUMED`** (409) | mapped `TICKET_CONSUMED` | map `PAIRING_TICKET_CONSUMED` |
| Non-active ticket | not documented | **`PAIRING_TICKET_UNAVAILABLE`** (409) | not mapped | add mapping |
| Unknown ticket | `UNAUTHORIZED` | `UNAUTHORIZED` (same) | ✅ | ✅ |
| `version`/`os` max | not documented | 40 chars | unbounded | add 40-char cap |
| `printerName` max | not documented | 200 chars | unbounded | add 200-char cap |

**Impact before fix:** a 200-char device name passes local validation but fails server-side (120 cap); the consumed-ticket UI message says "TICKET_CONSUMED" instead of "PAIRING_TICKET_CONSUMED"; expired tickets surface as a generic "Unauthorized" instead of "code expired".

---

## Other material findings (not breaking, but require action)

### F1 — `withDeviceAuth` does NOT enforce `isEnabled` ⚠

The SaaS `withDeviceAuth` middleware (the device-token verification used by ALL `/companion/*` + `/agents/*` routes) refreshes presence but does **NOT** check `agent.isEnabled`. A disabled device can still poll commands, claim jobs, post heartbeats. Disabling only stops NEW commands from being queued (`POST /shops/me/devices/[id]/commands` rejects with 400 `VALIDATION_ERROR` if `isEnabled=false`).

**Implication for PrintEXE:** the 403 `DEVICE_DISABLED` handling I built in Phase 2 is **defense in depth, not a contract guarantee**. The server may never send 403 for a disabled device; the device keeps operating until the shopkeeper explicitly revokes (DELETE → token hash gone → 401). I keep the 403 handling (it's correct for the contract's intent + future-proof) but document that the current SaaS doesn't enforce it.

### F2 — Two distinct revoke events (both must be handled) ✅ (already done in Phase 2)

- `device.revoked` `{agentId, deviceName, cancelledJobs}` — from `DELETE /shops/me/devices/[id]`
- `agent.revoked` `{agentId, agentName, cancelledJobs}` — from the legacy `DELETE /agents/[id]`

My Phase 2 `SocketIoRealtimeClient` already listens for both. ✅

### F3 — Realtime auth is an EMIT, not the `auth` option ⚠

The SaaS Socket.IO server expects `socket.emit('auth', { token: jwt })` AFTER `connect`. The socket.io `auth` option is ignored by the server. My Phase 2 client already emits `auth` on connect (verified in the `SocketIoRealtimeClient`). ✅ But the server also passes `auth: { token }` in the reference TS client — harmless, server ignores it.

### F4 — HMAC pepper is the literal `'print-agent-salt'` ⚠

The SaaS stores `printAgent.tokenHash = HMAC_SHA256(key="print-agent-salt", deviceToken)`. The pepper is **hard-coded in source**, NOT an env var. This is a Phase H security finding (brief §10): the pepper is not a secret — it's a constant. PrintEXE does NOT need to compute it (the server verifies the token); but the SaaS should move it to an env var for Phase H. That's an Agent 1 (SaaS) task — I document it but don't modify the SaaS repo.

### F5 — Rate limits degrade to global in production ⚠

With `CACHE_PROVIDER=memory` (Vercel default) and no `TRUSTED_PROXY`, the rate-limit key is `rl:<scope>:unknown` — a **global** limit shared across all clients, not per-IP. The `/companion/pair` limit of 10/60s becomes "10 pairings per minute across the entire internet". This is an SaaS-side production risk (brief §10 Phase H); PrintEXE just needs to back off on 429 (already does — `PairingTicketException.IsRateLimited`).

### F6 — `received` returns 200 (not 409) for terminal jobs ✅ (already handled)

The `/agents/jobs/[id]/received` route returns `{jobId, received: true, cleanupScheduled: false, message: "Job is already '<status>'. Receipt not needed."}` with HTTP 200 for terminal-state jobs. My Phase 2 `MarkJobReceivedAsync` already handles `cleanupScheduled: false` as a no-op. ✅

### F7 — `jobs/[id]/status` returns 200 with `skipped: true` ✅ (already handled)

The claim/status route returns `{jobId, skipped: true, reason: "..."}` with HTTP 200 when a claim race is lost. My Phase 2 `ReportJobStatusAsync` already surfaces `Skipped`. ✅

### F8 — `job.dispatch` payload has optional `deliveryAttemptId` ✅

Two emitters (legacy `print.service.ts` without `deliveryAttemptId`, newer `delivery-coordinator.ts` with it). My Phase 2 `SocketIoRealtimeClient` job.dispatch handler treats it as optional. ✅

---

## Full endpoint-by-endpoint matrix (the brief's §2 deliverable)

For each of the 14 routes: method, URL, auth, request, response, envelope, status codes, error codes, enum values, required/optional, timeout/retry. ✅ = matches; ⚠ = drift but works; ❌ = drift that breaks (fixed in Phase 3).

| # | Route | Auth | Req matches | Resp matches | Error codes | Status | Phase 3 action |
|---|---|---|---|---|---|---|---|
| 1 | `POST /companion/pairing/begin` | shopkeeper JWT | (none) | `{ticket, pairingUrl, expiresAt, shopId}` | UNAUTHORIZED/FORBIDDEN/RATE_LIMITED | ✅ | none (shopkeeper-side; PrintEXE doesn't call) |
| 2 | `POST /companion/pair` | none (ticket) | ❌ deviceName cap 120 vs 255 | `{agentId, deviceToken, shopId, shopName, protocolVersion:1, warning}` | ❌ PAIRING_TICKET_UNAVAILABLE/CONSUMED vs TICKET_CONSUMED; UNAUTHORIZED (expired) vs TICKET_EXPIRED | ✅ | **fix #4** |
| 3 | `GET /companion/commands` | device token | (none) | ❌ `{agentId, commands:[{commandId, commandType, payload(object), issuedAt, expiresAt}]}` vs my `{commands:[{id, commandType, payload(string), expiresAt}]}` | UNAUTHORIZED | ✅ | **fix #1** |
| 4 | `POST /companion/commands` | device token | ❌ `result` object vs my string | ❌ `{commandId, status, updatedAt}` vs my `{applied}` | VALIDATION_ERROR/UNAUTHORIZED/NOT_FOUND | ✅ | **fix #2 + #3** |
| 5 | `GET /shops/me/devices` | shopkeeper JWT | (none) | `{devices:[{id, name, isOnline, isEnabled, lastSeenAt, printerName, version, os, createdAt}]}` | UNAUTHORIZED/FORBIDDEN/NOT_FOUND | ✅ | none (shopkeeper-side) |
| 6 | `GET /shops/me/devices/[id]` | shopkeeper JWT | (path) | `{device:{...}, commands:[{...}]}` | UNAUTHORIZED/FORBIDDEN/NOT_FOUND | ✅ | none |
| 7 | `PATCH /shops/me/devices/[id]` | shopkeeper JWT (owner only) | `{name?, isEnabled?}` | `{id, name, isEnabled, updatedAt}` | VALIDATION_ERROR/FORBIDDEN/NOT_FOUND | ✅ | none |
| 8 | `DELETE /shops/me/devices/[id]` | shopkeeper JWT (owner only) | (path) | `{id, name, revoked:true, cancelledJobs, message}` | UNAUTHORIZED/FORBIDDEN/NOT_FOUND | ✅ | none |
| 9 | `POST /shops/me/devices/[id]/commands` | shopkeeper JWT | `{commandType, payload?, idempotencyKey?}` | `{commandId, commandType, status:'requested', payload, idempotencyKey, expiresAt, createdAt, idempotentReplay}` | VALIDATION_ERROR/FORBIDDEN/NOT_FOUND | ✅ | none |
| 10 | `POST /agents/token` | device token | (none) | `{accessToken, expiresIn, shopId}` | UNAUTHORIZED | ✅ | already matches |
| 11 | `POST /agents/heartbeat` | device token | `{printerName?, version?, os?}` (all max 40/200) | `{agentId, online:true, ts}` | VALIDATION_ERROR/UNAUTHORIZED | ✅ | already matches |
| 12 | `GET /agents/jobs/pending` | device token | (none) | `{jobs:[{jobId, fileId, orderId, filename, downloadUrl, copies, colorMode, duplex, paperSize, pageRange}], agentId}` | UNAUTHORIZED | ✅ | already matches |
| 13 | `POST /agents/jobs/[id]/status` | device token | `{status: 'printing'/'completed'/'failed', lastError?, printerName?}` | `{jobId, status, applied:true}` OR `{jobId, skipped:true, reason}` | VALIDATION_ERROR/UNAUTHORIZED/NOT_FOUND | ✅ | already matches |
| 14 | `POST /agents/jobs/[id]/received` | device token | `{deliveryAttemptId?, transferId?, checksum?, fileSize?, receivedAt?}` | `{jobId, received:true, cleanupScheduled, message}` | VALIDATION_ERROR/UNAUTHORIZED/NOT_FOUND | ✅ | already matches |

## Socket.IO events matrix

| Event | Direction | Payload | PrintEXE listens | Status |
|---|---|---|---|---|
| `auth` | client→server | `{token}` | ✅ emits on connect | ✅ |
| `auth-ok` | server→client | `{shopId, role}` | ✅ | ✅ |
| `auth-error` | server→client | `{message}` | ✅ (refreshes JWT) | ✅ |
| `job.dispatch` | server→client | `{jobId, fileId, orderId?, filename, downloadUrl, copies, colorMode, duplex, paperSize, pageRange, deliveryAttemptId?}` | ✅ → enqueues | ✅ |
| `job.printing` | server→client | `{jobId, fileId}` | not listened (informational) | ok |
| `job.completed` | server→client | `{jobId, fileId}` | not listened (informational) | ok |
| `job.failed` | server→client | `{jobId, fileId, error?}` | not listened (informational) | ok |
| `order.paid`/`order.ready` | server→client | `{orderId, ...}` | not listened (informational) | ok |
| `agent.heartbeat` | server→client | `{agentId}` | not listened | ok |
| `agent.revoked` | server→client | `{agentId, agentName, cancelledJobs}` | ✅ → Revoked | ✅ |
| `delivery.received` | server→client | `{jobId, agentId, agentName, receivedAt, checksum?}` | not listened | ok |
| `device.command.issued` | server→client | `{commandId, agentId, commandType}` | ✅ → immediate poll | ✅ |
| `device.revoked` | server→client | `{agentId, deviceName, cancelledJobs}` | ✅ → Revoked | ✅ |

## Prisma schema (the models PrintEXE touches, indirectly)

| Model | Key fields | PrintEXE interaction |
|---|---|---|
| `PrintAgent` | `id, shopId, name, tokenHash, isEnabled, isOnline, lastSeenAt, printerName, version, os` | the device PrintEXE pairs as; PrintEXE sends the token, server verifies via HMAC |
| `PairingTicket` | `id, shopId, agentId?, createdByUserId, ticketHash (SHA-256, unique), status (active/consumed/expired/revoked), expiresAt, consumedAt, deviceName?, deviceMetadata?` | the ticket PrintEXE exchanges |
| `DeviceCommand` | `id, shopId, agentId, commandType, payload, status, initiatedBy, result, idempotencyKey (unique), expiresAt, deliveredAt, executedAt` | the commands PrintEXE fetches + reports |
| `PrintJob` | `id, shopId, fileId, agentId?, status (queued/printing/completed/failed), attempts, lastError, queuedAt, startedAt, completedAt` | the jobs PrintEXE claims + reports |
| `PrintDeliveryAttempt` | `id, printJobId, agentId, shopId, attemptId, deliveryMode, status, checksum?, fileSize?, ...` | the delivery receipt PrintEXE sends |
| `DeviceSyncState` | `id, shopId, deviceId, syncCursor, lastSyncAt, lastPushAt` | not currently used by PrintEXE (future) |

## Verdict

The published v1 contract and the actual SaaS implementation are **99% aligned on the 14 routes + Socket.IO events + Prisma models**. The 4 critical mismatches (M1–M4) are field-name / type / error-code drifts that break the Phase 2 client at runtime. Phase 3 fixes all four. The other findings (F1–F8) are SaaS-side production risks (HMAC pepper, rate-limit degradation, isEnabled not enforced) that I document but do not modify (Agent 1's territory).
