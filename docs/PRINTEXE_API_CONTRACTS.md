# PrintEXE — API Contracts (companion ⇄ Printelyx cloud)

**Date:** 2026-11-25 · **Rule:** the companion implements the production SaaS contract exactly as verified against the live backend (23 contract points, `docs/AUDIT.md` §15). No endpoint, field, or event in this document is invented; where the backend does not expose something the brief asked for, that is stated explicitly and the honest alternative is documented.

Base URL: `https://printshop-saas-three.vercel.app` (configurable; `PrintShop:BaseUrl` overrides `Backend:BaseUrl`).
API prefix: `/api/v1`. Envelope: `{ success, data, error: { code, message }, pagination? }`.
Socket.IO URL: `https://printshop-saas-three.vercel.app/?XTransformPort=3003` (the query parameter is REQUIRED — without it the gateway routes to the Next.js app, which does not serve Socket.IO). Path `/`, WebSocket transport.

## 1. Authentication model

1. **Shopkeeper session** (UI): `POST /api/v1/auth/login` (email+password) → `{accessToken, refreshToken, expiresIn, user}`; short-lived access (~15 min) + long-lived refresh (~7 days). Used by `IShopBackend` (shops/me, orders, analytics).
2. **Device pairing**: `POST /api/v1/companion/pair` (v1 contract §2, pairing ticket) or paste-token (`POST /api/v1/agents/register` with owner JWT). Result: `{deviceToken, agentId, shopId, shopName, protocolVersion=1}`. Stored DPAPI-encrypted (`ProtectedData`, CurrentUser) at the contract path `%AppData%\PrintEXE\device-token.dat` (with legacy-path migration).
3. **Agent calls** use `Authorization: Bearer <deviceToken>`.
4. **Socket.IO auth**: on connect the companion exchanges the device token for a short-lived JWT via `POST /api/v1/agents/token` → `{accessToken, expiresIn, shopId}` and emits `auth {token: <jwt>}`. `auth-ok` joins the shop room; `auth-error` triggers refresh+reconnect.

## 2. REST endpoints (agent/companion)

| Method & path | Request | Response `data` | Notes |
|---|---|---|---|
| POST `/agents/heartbeat` | `{printerName?, version?, os?}` | `{agentId?, online, ts?, serverTime, tokenStillValid?}` | 15 s cadence (v1 contract §4; server marks device offline after 30 s). The production response does **not** carry `tokenStillValid` — revocation is detected via 401 / Socket.IO revocation events (documented limitation). |
| POST `/agents/token` | (empty, Bearer device token) | `{accessToken, expiresIn, shopId}` | JWT for Socket.IO auth only. |
| GET `/agents/jobs/pending` | — | `{jobs: PrintJobDto[], agentId}` | Poll fallback every 2 s while the socket is down (v1: 3 s contract default). |
| POST `/agents/jobs/{jobId}/status` | `{status: "printing"\|"completed"\|"failed"\|"needs_review", printerName?, lastError?}` | `{jobId, status, applied, skipped, reason?}` | **Atomic claim**: `printing` applies only when backend state is `queued`. `skipped:true` + reason `"...claimed by this device in a previous session"` = crash leftover → companion parks `OutcomeUnknown` and reports `needs_review`. Reason `"already claimed by another agent"` = race loss → cancel locally. |
| POST `/agents/jobs/{jobId}/received` | `{checksum (64-hex, NO "sha256:" prefix), fileSize, receivedAt (ISO), deliveryAttemptId?, transferId?}` | `{jobId, received, cleanupScheduled, cleanupScheduledAt?, message}` | Durable receipt — gates cloud deletion (cloudDeleteAfter = receipt + 15 min, server-side cron). 404 = foreign-shop job; 409 = terminal. |
| GET `{pdfUrl}` | — | `application/pdf` bytes | Presigned S3 URL (no auth header). Client caps at 100 MB and verifies SHA-256 when the backend supplies it. |
| GET `/sync/pull?cursor=&limit=` | — | `{changes: SyncChange[], nextCursor, hasMore}` | Shop snapshot sync. |
| POST `/sync/push` | `{changes: [...]}` | `{results: [...]}` | Pending-op outbox. |
| GET `/sync/status` | — | `{deviceId, shopId, syncCursor, lastSyncAt, lastPushAt, agentName, isOnline}` | |
| POST `/companion/pair` | pairing-ticket exchange (v1 §2) | `{deviceToken, agentId, shopId, shopName, protocolVersion}` | |
| GET/POST `/companion/commands` | command payload / report | pending commands / ack | v1 §12 remote commands (7 types: refresh_printers, pause, resume, cancel_job, test_print, diagnostics, set_printer). Dedup via `commandId` (LRU + durable store). |
| GET/POST `/companion/devices/*` (token rotate, revoke) | — | rotation result / ack | Token rotation returns the NEW token once (server stores HMAC verifier). |

**Not provided by the backend (explicitly NOT invented on the client):**
- No rush/priority field on `PrintJobDto` — the companion supports operator-set local priority (v0.6 schema) ready for the field.
- No due-time field — `due_at` column exists, stays null.
- No customer-approval field — the atomic claim (backend state machine: only paid/approved orders dispatch) is the authorization gate; the companion additionally refuses any job whose delivery state is `delivery_failed` (D-11). When the contract grows an explicit approval marker, `JobProcessor` will honor it before spooling.
- No live printer-status push — the companion reports printer name on heartbeat/status per current contract.

## 3. PrintJobDto (wire shape, verbatim field names)

`jobId, fileId, orderId, filename, downloadUrl, copies, colorMode ("bw"|"color"), duplex (bool), paperSize ("a4"|…), pageRange ("1-3,5" | null)`.

## 4. Socket.IO events

| Direction | Event | Payload | Companion behavior |
|---|---|---|---|
| emit | `auth` | `{token: <jwt>}` | the ONLY emit |
| listen | `auth-ok` | — | joined shop room |
| listen | `auth-error` | — | refresh JWT, reconnect |
| listen | `job.dispatch` | `PrintJobDto` | stamp shop id (D-12) → ownership check → idempotent enqueue → `JobDelivered`. **No wire-level ACK** — claiming happens via REST `status:"printing"`. |
| listen | `agent.revoked` / `device.revoked` | `{agentId, agentName}` | clear credentials, stop, surface "Device revoked" |
| listen | `device.command.issued` | `{commandId, agentId, commandType}` | immediate command poll (authoritative fetch via GET /companion/commands) |
| listen | `sync.pushed` | — | logged only (no aggressive refetch) |

Reconnection: SocketIOClient built-in reconnection (2 s fixed delay). Documented gap: no jitter and no socket-level ping — liveness currently relies on server close + heartbeat HTTP calls. Improvement tracked (backoff wrapper) in ENGINEERING_DECISIONS follow-ups; deliberately not changed in this pass because it is the only live-integration transport that is verified working end-to-end.

## 5. Mock/fake backend for tests

`tests/PrintShopCompanion.Integration.Tests/FakePrintelyxBackend.cs` implements the contract above over real HTTP (Kestrel): heartbeat (401 on wrong Bearer), token exchange, pending, atomic claim with the state machine queued→printing→completed/failed + crash-leftover skip semantics, received (cleanup scheduling), and PDF bytes at `/files/{jobId}.pdf`. This is the executable specification of this document; the legacy `PrintShopCompanion.Backend` project does NOT match this contract and is slated for removal.
