# PrintEXE ↔ PrintShop SaaS — Contract Compatibility Report

**Date:** 2026-11-16
**Companion branch:** `feat/unified-print-exe-cloud-integration` (off `d65cb19`)
**Companion source:** `Arjunyadavsoma/PrintEXE` (this repo)
**Backend source:** `Arjunyadavsoma/printshop-saas` (TypeScript / Next.js + Bun Socket.IO mini-service — **NOT** .NET)
**Method:** both repos read in full; every endpoint, header, event name, and DTO field below was extracted from actual source, not from docs.

---

## 1. Verdict

The companion's **live production client code** (`HttpPrintShopBackend.cs`, `SocketIoRealtimeClient.cs`) **already matches the real SaaS backend contract** for: base URL, API path prefix, auth scheme, response envelope, all REST endpoints, all Socket.IO events, and all DTO field names. The contract drift is concentrated in:
- **the companion's own docs** (`docs/PROTOCOL.md`, `docs/BACKEND-MAPPING.md`) which describe a different, stale contract, and
- **dead constants** in `CompanionProtocol.cs` (`HubMethodDeliverJob="DeliverJob"` etc.) that the live client does not use.

There are **two genuine conflicts between the Agent-2 brief and the SaaS backend** that cannot be resolved on the companion side without inventing endpoints. Per the brief's own rule — *"Do not guess. Identify the exact mismatch. Produce a compatibility report. Continue only with work that does not depend on the unresolved contract."* — these are flagged in §3 and left for Agent 1 (SaaS owner).

---

## 2. The real SaaS contract (extracted from source)

### 2.1 Documentation in the SaaS repo

The SaaS repo publishes the authoritative contract under `docs/integration/PRINTEXE_*`:
- `PRINTEXE_API_CONTRACT.md` — 10 endpoints, envelope, base URL, version header
- `PRINTEXE_AUTHENTICATION.md` — three-token model (owner JWT / device token / Socket JWT), DPAPI rules, revocation
- `PRINTEXE_DEVICE_LIFECYCLE.md` — PrintAgent row lifecycle, 30-s staleness, 60-s heartbeat
- `PRINTEXE_REALTIME_EVENTS.md` — Socket.IO URL + `?XTransformPort=3003`, `auth` handshake, event catalogue, polling fallback
- `PRINTEXE_PRINT_JOB_LIFECYCLE.md` — job state machine, atomic claim, 15-min presigned-URL TTL, receipt, 5-min stale→`needs_review`
- `PRINTEXE_ERROR_CODES.md` — HTTP/code matrix + retry policy
- `PRINTEXE_COMPATIBILITY_MATRIX.md` — per-PrintEXE-version feature matrix (v0.4.7 / v0.5.x / v1.0.0 / v1.1.x / v2.0.0)

(No file named `COMPANION_API_CONTRACT.md` / `COMPANION_REALTIME_PROTOCOL.md` / `DEVICE_SECURITY.md` / `PRINT_JOB_LIFECYCLE.md` exists — the `PRINTEXE_*` files are the equivalents.)

### 2.2 HTTP endpoints (companion-facing)

Base URL (prod): `https://printshop-saas-three.vercel.app/api/v1`
Envelope: `{"success":true,"data":{...}}` | `{"success":false,"error":{"code","message","details"?}}`
Response header on every call: `x-request-id`.
Advisory request header: `X-PrintShop-Companion-Version` (logged, not enforced; planned `426 Upgrade Required` gate at v1.0.0).

| # | Method | Path | Request body | Response `data` | Auth |
|---|---|---|---|---|---|
| 1 | POST | `/agents/register` | `{name:string(1..120,req), os?:string(≤40), version?:string(≤40), printerName?:string(≤200)}` | `{agentId, deviceToken(73-char), shopId, shopName, warning}` | Owner JWT (web dashboard only — NOT called by PrintEXE) |
| 2 | POST | `/agents/token` | (empty) | `{accessToken, expiresIn, shopId}` | Device token |
| 3 | POST | `/agents/heartbeat` | `{printerName?:≤200, version?:≤40, os?:≤40}` — all optional, `{}` OK | `{agentId, online:true, ts:ISO8601}` | Device token |
| 4 | GET | `/agents/jobs/pending` | — | `{jobs: PendingJob[], agentId}` | Device token |
| 5 | POST | `/agents/jobs/{jobId}/status` | `{status:'printing'\|'completed'\|'failed', lastError?:≤500, printerName?:≤200}` | success `{jobId, status, applied:true}` / skip `{jobId, skipped:true, reason}` (HTTP 200) | Device token |
| 6 | POST | `/agents/jobs/{jobId}/received` | `{deliveryAttemptId?:uuid, transferId?:uuid, checksum?:/^[a-f0-9]{64}$/i, fileSize?:int>0, receivedAt?:ISO8601}` — all optional | `{jobId, received:true, cleanupScheduled:boolean, message}` (idempotent) | Device token |
| 7 | DELETE | `/agents/{agentId}` | — | `{agentId, agentName, revoked:true, cancelledJobs, message}` | Owner JWT (web only — NOT called by PrintEXE) |
| 8 | GET | `/sync/pull?cursor={ISO8601}&limit={1-500}` | — | `{changes: SyncChange[], nextCursor, hasMore}` | Device token |
| 9 | POST | `/sync/push` | `{changes: Array<{entity, id, action:'upsert'\|'delete', data, clientRevision, baseRevision}>.max(100)}` — **Customer only in v1** | `{results: Array<{id, accepted, conflict, serverRevision?, serverData?, reason?, error?}>}` | Device token |
| 10 | GET | `/sync/status` | — | `{deviceId, shopId, syncCursor, lastSyncAt, lastPushAt, agentName, isOnline}` | Device token |

`PendingJob = {jobId, fileId, orderId, filename, downloadUrl, copies, colorMode, duplex, paperSize, pageRange}` — **matches the companion's `PrintJobDto` field-for-field.**

### 2.3 Device auth header

**Exact header:** `Authorization`. Value: `Bearer <deviceToken>`. There is **no `X-PrintShop-Device-Token` header** anywhere in the SaaS repo (zero grep hits).

Validation flow (`src/lib/api/with-auth.ts`, `withDeviceAuth`):
1. Strip `Bearer ` prefix.
2. `HMAC-SHA256('print-agent-salt', token).digest('hex')` — the salt is the literal string `"print-agent-salt"`.
3. `SELECT * FROM "PrintAgent" WHERE "tokenHash" = $1`.
4. No row → HTTP 401.
5. On success, **every** authenticated call refreshes presence: `UPDATE "PrintAgent" SET "isOnline"=true, "lastSeenAt"=NOW()`. So heartbeat/poll/status/sync all double as heartbeats.
6. `shopId` comes exclusively from the agent row (tenant isolation) — never from the request.

Token format: `crypto.randomUUID() + '-' + crypto.randomUUID()` → 73-char ASCII (two lowercase UUIDs joined by `-`). Local regex: `/^[0-9a-f-]{73}$/`. **Long-lived, no TTL, no expiry** — until revoked.

### 2.4 Pairing flow (the actual model)

**There is no PairingTicket / consume / redeem mechanism in the SaaS repo.** Searches for `PairingTicket`, `consume`, `redeem` (in the pairing sense) return nothing. Pairing is a **paste-the-token** flow:

1. Shopkeeper logs into the web dashboard (owner JWT issued by `POST /api/v1/auth/login`).
2. Shopkeeper opens Printer Setup → "Register new agent".
3. Web calls `POST /api/v1/agents/register` with `Authorization: Bearer <ownerJWT>` + `{name, os?, version?, printerName?}`.
4. Server generates `token = uuid() + '-' + uuid()`, hashes it with HMAC-SHA256 + salt `"print-agent-salt"`, inserts a `PrintAgent` row (only the hash is stored).
5. Server returns the plaintext `deviceToken` **exactly once**: `{agentId, deviceToken, shopId, shopName, warning:"Save this token — it is shown only once."}`.
6. Web shows the token in a modal; shopkeeper copy-pastes it into PrintEXE's pairing field.
7. PrintEXE validates locally `/^[0-9a-f-]{73}$/`, DPAPI-encrypts, persists, then sends `POST /agents/heartbeat` once. 200 = valid token; 401 = wrong paste / already revoked.

**No TTL on the pairing.** The device token is long-lived until the shopkeeper revokes it via `DELETE /agents/{agentId}` (owner JWT, web only).

### 2.5 Realtime protocol

**Socket.IO** (Node `socket.io`), NOT ASP.NET SignalR. No `Hub`/`MapHub` references anywhere in the SaaS repo.

Server: `mini-services/realtime-service/index.ts` (Bun runtime).
- TCP ports: `3003` (Socket.IO), `3004` (HTTP `/emit` relay for server→server pushes).
- Options: `path: '/'`, `cors: {origin: CORS_ALLOWED_ORIGINS, methods: ['GET','POST']}`, `pingTimeout: 60000`, `pingInterval: 25000`.
- Public URL: `wss://printshop-saas-three.vercel.app/?XTransformPort=3003`. The `?XTransformPort=3003` query is **required** — without it Caddy proxies to the Next.js app on port 3000 which doesn't serve Socket.IO → HTTP 400.
- Single default namespace `/` — no named hub.

**Client→server (companion invokes):** exactly one inbound handler — `auth` with `{token: <jwt>}`. The server verifies the HS256 JWT (sub=`agent:<agentId>`, shopId, role=`staff`, type=`access`, TTL=900s), rejects if `!claims || !claims.shopId`, then `socket.join('shop:<shopId>')` and emits `auth-ok {shopId, role}`. **No other client→server method has a listener.** The brief's hypothesized `AckJob`/`ReportStatus`/`Heartbeat`/`DeliverJob`/`claim`/`complete`/`fail` are silently dropped.

**Server→client events (companion receives), exhaustively enumerated from `publish(...)` calls:**

| Event | Payload | Source |
|---|---|---|
| `job.dispatch` | `{jobId, fileId, orderId?, filename, downloadUrl, copies, colorMode, duplex, paperSize, pageRange, deliveryAttemptId}` | `delivery-coordinator.ts` |
| `job.printing` | `{jobId, fileId}` | `status/route.ts` |
| `job.completed` | `{jobId, fileId}` | `status/route.ts` |
| `job.failed` | `{jobId, fileId, error}` | `status/route.ts` |
| `job.needs_review` | `{jobId, fileId}` | `durable-queue.ts` (stale recovery) |
| `order.ready` | `{orderId}` | `status/route.ts` |
| `order.paid` | `{orderId, amountPaise}` | payment webhook |
| `order.collected` | `{orderId}` | order-completion path |
| `agent.revoked` | `{agentId, agentName, cancelledJobs}` | `[id]/route.ts` |
| `agent.heartbeat` | `{agentId}` | `jobs/pending/route.ts` (poll side-effect) |
| `delivery.received` | `{jobId, agentId, agentName, receivedAt, checksum?}` | `received/route.ts` |
| `sync.pushed` | `{count, deviceId}` | `push/route.ts` |
| `auth-ok` / `auth-error` | `{shopId, role}` / `{message}` | Socket.IO server (not via /emit) |

**Connection lifecycle:** `Reconnection=true`, attempts ∞ (browser uses 10), delay 2000ms, transports `[websocket, polling]`. On every reconnect, re-emit `auth` with cached/refreshed JWT. On `auth-error`: refresh JWT via `POST /agents/token` and re-emit `auth` once; if refresh returns 401, treat as revocation. Socket.IO does **not** refresh `lastSeenAt` — companion must keep making REST calls (heartbeat/poll) or the dashboard marks it offline after 30s. Polling fallback: `GET /agents/jobs/pending` every 2s while Socket.IO is down; optional 10-15s even while up.

### 2.6 Job status enum (exact)

`packages/core/src/domain/enums.ts`:
```ts
export type PrintJobStatus = 'queued' | 'assigned' | 'printing' | 'completed' | 'failed' | 'needs_review';
```
Persisted as free-text `String @default("queued")` (`prisma/schema.prisma:131`).

**Companion-facing transitions** (`POST /agents/jobs/{id}/status` accepts only three):
- `printing` — atomic conditional claim (`UPDATE ... WHERE id AND shopId AND status='queued'`). Loser gets `{skipped:true, reason}` HTTP 200. Side-effects: `PrintFile.status→'printing'`, publishes `job.printing`.
- `completed` — conditional `UPDATE ... WHERE status='printing'`. Side-effects: `PrintFile.status→'completed'`, `printedAt=NOW()`, publishes `job.completed`; then `maybeMarkOrderReady` flips order to `ready` when ALL its files are `completed`.
- `failed` — unconditional `UPDATE PrintJob SET status='failed', lastError=$1`. Side-effects: `PrintFile.status→'failed'`, publishes `job.failed`.

**NOT accepted by the companion:** `queued`, `assigned`, `needs_review` are server-internal. `needs_review` is reached exclusively by the **stale-recovery worker**: any job in `printing` with `startedAt < now - 5min` is moved to `needs_review` on worker startup. Once in `needs_review`, further status calls return `{skipped:true, reason}`. Recovery out of `needs_review` is **shopkeeper-only** (`requeueJob` → `queued`, or `markCompleted` → `completed`).

**The brief's hypothesized enum values `Pending`, `Processing`, `Printed`, `Cancelled`, `UncertainSpooled`, `OutcomeUnknown` do NOT exist in the SaaS repo.** Closest semantic mappings:
- local `Pending` → backend `queued` (file) / `pending` (job)
- local `Downloading`/`Rendering`/`Spooled` → backend `printing` (companion reports `printing` at claim time)
- local `Printed`/`ReportedPrinted` → backend `completed`
- local `Failed` → backend `failed`
- local `UncertainSpooled`/`OutcomeUnknown` → backend `needs_review` (companion cannot drive this transition — only the stale-recovery worker does)
- local `Cancelled` → **no backend equivalent**; cancellation is modelled at the **order** level (`OrderStatus = awaiting_payment | paid | printing | ready | collected | cancelled | refunded`), not the job level.

### 2.7 Remote commands — NOT FOUND

There is **no backend→device command channel** in the SaaS repo. No `CommandType`, `RefreshPrinters`, `Pause`, `Resume`, `Cancel`, `TestPrint`, `Diagnostics` identifiers anywhere in `src/`, `packages/`, or `mini-services/`. No `/agents/{id}/command` endpoint. The only way the backend signals a device is the `agent.revoked` broadcast (and the implicit 401 once the row is deleted). The entire companion→server control surface is the 10 REST endpoints in §2.2.

**If the PrintEXE companion documents or implements a `RefreshPrinters`/`Pause`/`Resume`/`Cancel`/`TestPrint`/`Diagnostics` remote-command channel, that channel has no server counterpart.**

---

## 3. Conflicts with the Agent-2 brief (cannot be resolved on the companion side)

### Conflict #1 — Pairing model

| | Brief (§4) | SaaS backend (actual) |
|---|---|---|
| Mechanism | Short-lived **one-time pairing ticket**; website creates ticket; companion opens `printshop://pair?...` URI; companion **exchanges the ticket** with backend; backend **validates and consumes the ticket atomically**; returns `{deviceId, shop, deviceToken}` | **Paste-token**: shopkeeper copy-pastes a **long-lived opaque device token** generated by `POST /agents/register` (owner JWT, web only); no ticket, no TTL, no consume step |
| URI scheme | `printshop://` | `printshopcompanion://` (installer-registered; backend has no URI handler at all) |
| TTL | "short-lived", "expiry" checked by companion | none — token is long-lived until revoked |
| Single-use | "single-use ticket" | the plaintext token is shown once but never "consumed" — the row stores only the hash |
| Device token in URL | "Never place the device token in a URL" — agreed | the brief's ticket-in-URL is fine; but the **actual backend never uses a URL-based flow** |

**Resolution:** the companion **must not invent a `/pair` endpoint or a ticket-consume flow** — the backend does not have one. The brief's §4 experience (website presents an Open PrintShop Companion link; Windows launches PrintEXE through the URI scheme; PrintEXE exchanges a one-time ticket) describes a **future contract** that the SaaS backend does not currently implement. This is a **change request for Agent 1**, not companion work.

What the companion **can** do today (and already does): accept the pasted device token, validate `/^[0-9a-f-]{73}$/`, DPAPI-encrypt, persist, send a heartbeat to confirm. The existing `MagicLinkPairingService` shim (which forwards a URL `token` to `PairWithDeviceTokenAsync`) is a vestige; the live pairing path is `HttpPrintShopBackend.PairWithDeviceTokenAsync` driven from the Login view's paste field.

**Action for Agent 1:** if a ticket flow is required, the SaaS backend must add: (a) `POST /agents/pairing/begin` (owner JWT) → returns `{pairingTicket, expiresAt}`; (b) `POST /agents/pairing/redeem` (device-auth or unauthenticated with ticket) → atomically consumes the ticket, returns `{agentId, deviceToken, shopId, shopName}`; (c) a documented TTL (e.g. 5 min, single-use). Until then the companion cannot implement the brief's §4 flow truthfully.

### Conflict #2 — Remote command channel

| | Brief (§8) | SaaS backend (actual) |
|---|---|---|
| Commands | RefreshPrinters, RefreshStatus, SelectDefaultPrinter, Pause, Resume, Cancel, Retry, TestPrint, Diagnostics, CheckForUpdates | **none** — no command channel of any kind |
| Transport | "authorized print commands from the website through the cloud" | only `job.dispatch` (a job, not a command) and `agent.revoked` (a notification, not a command) |
| Endpoint | (implied) | no `/agents/{id}/command` or similar |

**Resolution:** the companion **must not invent a command channel** — the backend has none. The brief's §8 allowlist describes a **future contract**. This is a **change request for Agent 1**.

What the companion **can** do today: implement the command handlers locally (RefreshPrinters, Pause/Resume queue, Cancel/Retry, TestPrint, Diagnostics, CheckForUpdates) as **local UI actions** triggered from the tray/dashboard — these are useful and contract-independent. Exposing them as remote-receivable commands requires Agent 1 to add a server→device command event (e.g. `command.dispatch` with `{commandId, type, payload, expiresAt}`) and an ack endpoint (e.g. `POST /agents/commands/{commandId}/result`). Until then the companion's remote surface is limited to: receiving `job.dispatch`, reporting `printing`/`completed`/`failed`/`received`, and the sync endpoints.

**Action for Agent 1:** if remote commands are required, the SaaS backend must add: (a) a server→client Socket.IO event (e.g. `command.dispatch` with `{commandId, type, payload, expiresAt, protocolVersion}`); (b) a result-reporting endpoint (e.g. `POST /agents/commands/{commandId}/result`); (c) a documented allowlist + authorization model. Until then the companion can only implement these as local actions.

---

## 4. Companion doc reconciliation (what to fix in this repo)

The following claims in the companion's own docs are **wrong** against the real SaaS contract and must be corrected (tracked in `docs/PROTOCOL-DRIFT.md` and the §5 work):

| Doc | Wrong claim | Reality |
|---|---|---|
| `docs/PROTOCOL.md` §2.3 | `POST /pair` with `{pairingToken, deviceId, hostname, operatingSystem, appVersion}` | No `/pair` endpoint; pairing is paste-token via `POST /agents/register` (owner JWT, web only) |
| `docs/PROTOCOL.md` §4.1 | Socket.IO hub methods `registerDevice`, `DeliverJob`, `AckJob`, `ReportStatus` | Only one inbound handler: `auth`. Events: `job.dispatch`, `job.printing/completed/failed/needs_review`, `order.*`, `agent.revoked`, `delivery.received`, `sync.pushed`, `auth-ok/error` |
| `docs/PROTOCOL.md` §3 | 60-s heartbeat driven by companion | No heartbeat timer exists in companion code; backend treats every authenticated call as a heartbeat; 30-s online threshold |
| `docs/BACKEND-MAPPING.md` §3.2 | Same stale `/pair` + `printJob`/`ack` event names | Same fixes as above |
| `docs/BACKEND-MAPPING.md` §3 | "real prefix may be `/api/print-agent`" | Real prefix is `/api/v1` (confirmed) |
| `CompanionProtocol.cs` | `HubMethodDeliverJob="DeliverJob"`, `HubMethodAckJob="AckJob"`, `HubMethodReportStatus="ReportStatus"`, `HubMethodHeartbeat="Heartbeat"`, `DeviceTokenHeader="X-PrintShop-Device-Token"`, `DeviceIdHeader`, `AppVersionHeader` | All dead. Real event names are string literals in `SocketIoRealtimeClient`; real auth is `Authorization: Bearer` |
| `README.md` | "local HTTP listener binds to `127.0.0.1` only and validates the `Origin` header" | No local HTTP listener exists in code |
| `docs/SECURITY.md` | "Local API binds to 127.0.0.1 only" row; "MSI is per-user" | Local API does not exist; MSI is per-machine (`Scope="perMachine"`) |
| `docs/PHASES.md` | "UncertainSpooled never auto-reprints" | `ReplayInFlightAsync` resets it to `Pending` on startup (code bug, not a contract issue) |
| `NOT-VERIFIED.md` | "Socket.IO hub method names are best-guesses" | They are confirmed correct against the real backend (`job.dispatch` etc.) |
| `NOT-VERIFIED.md` | "`PrintAgent`/`PrintJob` Prisma models not seen" | They exist (`prisma/schema.prisma`) — `PrintAgent {id, shopId, name, tokenHash, isOnline, lastSeenAt, printerName, version, os}`, `PrintJob {id, shopId, fileId, status, lastError, startedAt, printedAt}` |
| Mock backend `PrintShopCompanion.Backend` | `/api/companion/v1/*`, `X-PrintShop-Device-Token`+`X-PrintShop-Device-Id` headers, `LegacyPrintJobDto`, `JobStatusReportDto` | Implements a **different contract** from the production client. Cannot interoperate. Either align the mock to the real contract or document it as a local-dev-only fixture (not a contract reference) |

---

## 5. What the companion can build WITHOUT resolving the two conflicts

These are contract-independent, cross-platform-verifiable (unit tests), or already-contract-aligned (no invention):

1. **Reconcile docs** (`PROTOCOL.md`, `BACKEND-MAPPING.md`, `HONEST-STATUS.md`, `NOT-VERIFIED.md`, `README.md`, `SECURITY.md`, `PHASES.md`) to the real contract — remove stale `/pair`, `DeliverJob`/`AckJob`/`ReportStatus`, `printJob`/`ack`, "best-guess" claims.
2. **Fix `CompanionProtocol.cs` constants** to reflect the real event names (or remove the dead ones + add the real ones as the single source of truth) — no behavior change, just truth.
3. **Fix `App.OnStartup` credential-path check** to use `ICredentialStore`/`DeviceTokenStore` (the real `%LocalAppData%\...\Security\device.dat` path) instead of the legacy `%AppData%\PrintShop\Companion\device.dat`.
4. **Fix `SqliteJobQueue.ReadRecord`/`WriteStatusAsync`** to round-trip the v0.5 columns (data-loss bug, contract-independent).
5. **Fix `ReplayInFlightAsync`** to NOT reset `UncertainSpooled`/`OutcomeUnknown` to `Pending` (queue-safety invariant, contract-independent).
6. **Start the realtime client (or at minimum the poller) in `App.OnStartup`** so the WPF app can actually receive jobs — uses the already-aligned `SocketIoRealtimeClient`.
7. **Drive the heartbeat with a timer** (60s) — uses the already-aligned `HeartbeatAsync`.
8. **Implement local command handlers** (RefreshPrinters, Pause/Resume queue, Cancel/Retry, TestPrint, Diagnostics, CheckForUpdates) as **local UI actions** — useful regardless of the remote-command conflict. Wire the tray "Pause queue" menu item that is currently empty.
9. **Add a job-status-mapping unit test** asserting local `PrintJobRecord` states map to the backend's `printing`/`completed`/`failed` strings — contract-aligned, Linux-buildable test body.
10. **Wire `DeviceTokenRedactor`** into the Serilog config (security hardening, contract-independent).
11. **Load `appsettings.Local.json` in the App** (consistency with Worker, contract-independent).
12. **Fix `OptionsWriter.Update`** to actually persist `PrinterOptions.SelectedPrinter` (contract-independent).
13. **Document the mock backend as local-dev-only**, not a contract reference, OR align it to the real contract — a decision for a follow-up.

## 6. What the companion CANNOT build until Agent 1 resolves the conflicts

1. **The brief's §4 ticket-based pairing flow** (website → `printshop://pair?...` → companion redeems ticket → backend consumes atomically). Requires `POST /agents/pairing/begin` + `POST /agents/pairing/redeem` + TTL on the SaaS side.
2. **The brief's §8 remote command channel** (RefreshPrinters/Pause/Resume/Cancel/TestPrint/Diagnostics from the cloud). Requires a server→client `command.dispatch` event + a result-reporting endpoint + an authorization model on the SaaS side.
3. **The brief's §4 "Windows launches PrintEXE through the registered URI scheme" single-instance URL-forwarding.** `SingleInstance` is just a mutex today; making it forward a URL to the running instance is contract-independent and worth doing, but the **pairing-via-URL** experience depends on conflict #1.

---

## 6.1 Companion → SaaS change requests (for Agent 1)

To unblock the brief's §4 and §8, the SaaS backend needs:

**For pairing (§4):**
- `POST /api/v1/agents/pairing/begin` (owner JWT) → `{pairingTicket, expiresAt}` (TTL e.g. 5 min)
- `POST /api/v1/agents/pairing/redeem` (unauthenticated, body `{pairingTicket, deviceName, os?, version?}`) → atomically consumes the ticket, returns `{agentId, deviceToken, shopId, shopName}` (single-use)
- Web dashboard "Add Computer" should create a ticket and present a `printshopcompanion://pair?ticket=...&expires=...` link instead of showing the raw token

**For remote commands (§8):**
- A server→client Socket.IO event `command.dispatch` with `{commandId, type ∈ {RefreshPrinters, RefreshStatus, SelectDefaultPrinter, Pause, Resume, Cancel, Retry, TestPrint, Diagnostics, CheckForUpdates}, payload, expiresAt, protocolVersion}`
- A result-reporting endpoint `POST /api/v1/agents/commands/{commandId}/result` with `{status: 'accepted'|'rejected'|'executed'|'failed', reason?, result?}`
- A documented authorization model (which shop users can issue which commands)

These are **not** companion-side tasks. The companion will implement the corresponding client code the moment the SaaS contract publishes them.

---

## 7. Summary

- The companion's **live client code is already contract-aligned** with the real SaaS backend for REST endpoints, auth, envelope, Socket.IO events, and DTO shapes.
- The companion's **docs and dead constants are stale** and must be reconciled.
- Two **brief-vs-backend conflicts** (pairing-ticket vs paste-token; remote-commands vs none) are real and **cannot be resolved by inventing on the companion side** — they require SaaS changes by Agent 1.
- The companion can proceed with **contract-independent fixes** (queue data-loss, credential path, doc reconciliation, local command handlers, heartbeat timer, starting the realtime client in the WPF app) without waiting on Agent 1.
- No claim of end-to-end pairing, remote command execution, or physical printing is made — those require a Windows machine + the real backend + a real printer, none available in this sandbox.
