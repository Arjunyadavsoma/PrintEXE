# COMPANION_V1_CONTRACT — PrintEXE C# Implementation Review

**Date:** 2026-11-17
**Contract reviewed:** `Arjunyadavsoma/printshop-saas` `docs/integration/COMPANION_V1_CONTRACT.md` (v1.0, Frozen, committed `ffd0bb3` on `main`).
**PrintEXE branch:** `feat/unified-print-exe-cloud-integration` @ `cfa0a89`.
**Mandate:** the brief requires Agent 2 to "review this document and report any mismatch with the current C# implementation before coding."

This report is the Phase-1 review deliverable. Per-contract sections, it lists what already matches, what must change, and what is new. Implementation commits will reference this report.

---

## Verdict

The companion's **existing `/agents/*` REST + Socket.IO `auth`/`job.dispatch` paths already match the contract** (this was verified in Phase 0 / `docs/CONTRACT_COMPATIBILITY.md`). The contract **adds** three new surfaces that the companion does NOT yet implement:

1. **Pairing-ticket exchange** (`POST /companion/pairing/begin` is shopkeeper-side on the website; `POST /companion/pair` is the PrintEXE side) — replaces the current paste-token `PairWithDeviceTokenAsync` as the recommended pairing flow. **Conflict #1 from Phase 0 is now resolved by the contract.**
2. **Remote command channel** (`GET /companion/commands` + `POST /companion/commands` on the device side) — the companion has NO command client today. **Conflict #2 from Phase 0 is now resolved by the contract.**
3. **Two new Socket.IO events** (`device.command.issued`, `device.revoked`) plus the existing `agent.revoked`.

It also **tightens** two parameters the companion currently has wrong:
4. **Heartbeat interval 15s** (contract §4) — companion default is **60s** (`BackendOptions.HeartbeatInterval = TimeSpan.FromMinutes(1)`). Must change to 15s.
5. **Token storage path `%AppData%\PrintEXE\device-token.dat`** (contract Appendix C) — companion uses `%LocalAppData%\PrintShop\Companion\Security\device.dat`. Must reconcile (support the contract path; migrate from the old path; do not silently drop existing credentials).

And it specifies a **config section shape** (`PrintShop:BaseUrl`/`PollingInterval`/`HeartbeatInterval`/`SocketIoUrl`) that the companion does not yet read — currently the companion reads `Backend:BaseUrl` etc. Must add a `PrintShop` section alias (keep `Backend` for backward compat).

---

## Per-section review

### §1 Pairing-Ticket Creation — `POST /api/v1/companion/pairing/begin`

| Aspect | Contract | Current C# | Match? |
|---|---|---|---|
| Endpoint | `POST /companion/pairing/begin` | Not implemented | **NEW** |
| Auth | Bearer shopkeeper JWT | (n/a — shopkeeper-side, called by the website, not PrintEXE) | n/a |
| PrintEXE role | None — PrintEXE never calls this endpoint | — | n/a |

**Action:** none on the PrintEXE side. This is a website-side endpoint. PrintEXE only consumes the resulting `ticket` via §2.

### §2 Ticket Exchange (Pairing) — `POST /api/v1/companion/pair`

| Aspect | Contract | Current C# | Match? |
|---|---|---|---|
| Endpoint | `POST /companion/pair` | Not implemented (current `PairWithDeviceTokenAsync` hits `POST /agents/register` legacy, paste-token) | **NEW** |
| Auth | None (ticket IS the auth) | n/a | **NEW** |
| Request body | `{ticket: 64hex, deviceName: 1-255, version?, os?, printerName?}` | n/a | **NEW** |
| Response `data` | `{agentId, deviceToken: 64hex, shopId, shopName, protocolVersion: 1}` | n/a | **NEW** |
| Atomic consume | backend does `WHERE status='active'` | n/a | n/a |
| Error codes | 400 VALIDATION_ERROR, 401 UNAUTHORIZED (no ticket), 401 TICKET_EXPIRED, 409 TICKET_CONSUMED, 429 RATE_LIMITED | n/a | **NEW** — companion must map these to user-facing states |
| `protocolVersion` | returned `1` | not stored | **NEW** — store on the device record for future compatibility gates |

**Action:** add `PairWithTicketAsync(string ticket, string deviceName, ...)` to `IPrintShopBackend` + `HttpPrintShopBackend`. Keep `PairWithDeviceTokenAsync` for the legacy paste-token path (Appendix A `/agents/register`). The Pair Computer screen should support BOTH: ticket-first (recommended), paste-token-fallback.

### §3 Device-Token Authentication — `Authorization: Bearer <deviceToken>`

| Aspect | Contract | Current C# | Match? |
|---|---|---|---|
| Header | `Authorization: Bearer <deviceToken>` | `HttpPrintShopBackend.AddAuth` already sends this | ✅ MATCH |
| Verification | `HMAC-SHA256("print-agent-salt", token)` server-side | n/a (server concern) | n/a |
| `isEnabled = false` → 403 | contract: disabled devices get 403 on ALL companion routes | companion must treat 403 as "disabled" (distinct from 401 "revoked") | **NEW** — add 403 handling in the HTTP client (clear credential + show "Device disabled, contact your shop admin") |
| `isOnline`/`lastSeenAt` refresh on every auth call | contract: yes | companion's calls already trigger this server-side | ✅ (server side) |

**Action:** add explicit 403 → "device disabled" handling in `HttpPrintShopBackend` (don't conflate with 401 revoked). The companion should NOT auto-retry 403 (per §14 retry rules).

### §4 Device Heartbeat — `POST /api/v1/agents/heartbeat`

| Aspect | Contract | Current C# | Match? |
|---|---|---|---|
| Endpoint | `POST /agents/heartbeat` | ✅ already implemented | ✅ |
| Auth | Bearer device token | ✅ | ✅ |
| Request body | `{printerName?, version?, os?}` all optional | ✅ (`{printerName, version, os}`) | ✅ |
| Response `data` | `{agentId, online: true, ts}` | ✅ (`HeartbeatResult`) | ✅ |
| **Interval** | **15 seconds** | **60 seconds** (`BackendOptions.HeartbeatInterval`) | ❌ **MISMATCH — change to 15s** |
| Offline threshold | 30s | n/a (server side) | n/a |

**Action:** change `BackendOptions.HeartbeatInterval` default to `TimeSpan.FromSeconds(15)` and `HeartbeatIntervalSeconds` to `15`. Update `appsettings.json` (App + Worker) accordingly. (The WPF App must also START a heartbeat timer — see §3 of this report — currently no timer drives `HeartbeatAsync`.)

### §5 Device Metadata & Printer Inventory

| Aspect | Contract | Current C# | Match? |
|---|---|---|---|
| `printerName` reported via heartbeat | contract: yes (single selected printer) | ✅ `HeartbeatAsync` sends `printerName` | ✅ |
| Dedicated inventory endpoint | contract: "future versions" | not needed for v1 | ✅ (deferred) |
| `GET /shops/me/devices` (shopkeeper view) | shopkeeper-side, not called by PrintEXE | n/a | n/a |

**Action:** none new. Keep sending `printerName` on heartbeat. The `refresh_printers` command (§6) should trigger a local `WindowsPrinterService.DiscoverPrinters()` + a heartbeat to push the new `printerName` up.

### §6 Command Retrieval — `GET /api/v1/companion/commands`

| Aspect | Contract | Current C# | Match? |
|---|---|---|---|
| Endpoint | `GET /companion/commands` | Not implemented | **NEW** |
| Auth | Bearer device token | n/a | **NEW** |
| Response `data` | `{commands: [{id, commandType, payload (JSON string), expiresAt}]}` | n/a | **NEW** |
| Filter | only `status='requested'` AND `expiresAt > now` (server side) | n/a | n/a |
| Expired commands | server auto-marks `expired` | n/a | n/a |

**Action:** add `FetchPendingCommandsAsync()` to `IPrintShopBackend` returning `IReadOnlyList<PendingCommand>` with `{Id, CommandType, PayloadJson, ExpiresAt}`. The companion should still validate `expiresAt > now` locally before executing (defense in depth, and for the case where the command was fetched just before expiry).

### §7 Command Acknowledgement & Result Reporting — `POST /api/v1/companion/commands`

| Aspect | Contract | Current C# | Match? |
|---|---|---|---|
| Endpoint | `POST /companion/commands` | Not implemented | **NEW** |
| Auth | Bearer device token | n/a | **NEW** |
| Request body | `{commandId, status, result (JSON string)}` | n/a | **NEW** |
| `status` values | `delivered`, `accepted`, `executing`, `succeeded`, `failed` | n/a | **NEW** |
| Response `data` | `{applied: true}` | n/a | **NEW** |
| Idempotency | same `commandId` + same `status` is a no-op | n/a | **NEW** — companion must dedup locally too (don't re-execute on retry) |

**Action:** add `ReportCommandStatusAsync(string commandId, string status, string? resultJson)`. Add a local `CommandState` table (or reuse the SQLite queue DB) to track `command_id → status` so duplicate fetches don't re-execute. Report `delivered` on receipt, `executing` when starting, `succeeded`/`failed` on completion.

### §8 Sending Commands (Shopkeeper) — `POST /shops/me/devices/:deviceId/commands`

Shopkeeper-side, not called by PrintEXE. The companion only consumes the resulting commands via §6. **No PrintEXE action.**

### §9 Print-Job Dispatch — `GET /agents/jobs/pending`

| Aspect | Contract | Current C# | Match? |
|---|---|---|---|
| Endpoint | `GET /agents/jobs/pending` | ✅ already implemented | ✅ |
| Auth | Bearer device token | ✅ | ✅ |
| Response `data` | `{jobs: [PrintJobDto], agentId}` | ✅ | ✅ |
| Max 10 jobs, oldest first | contract: yes | companion already handles | ✅ |
| `downloadUrl` 15-min TTL | contract: 900s | companion already enforces 5-min download timeout (tighter; ok) | ✅ |
| Shop-scoped | contract: yes | n/a (server side) | n/a |

✅ No change.

### §10 Print-Job Status Updates — `POST /agents/jobs/:jobId/status`

| Aspect | Contract | Current C# | Match? |
|---|---|---|---|
| Endpoint | `POST /agents/jobs/:jobId/status` | ✅ already implemented | ✅ |
| `status` values | `printing`, `completed`, `failed` | ✅ (guarded by `JobStatusMappingTests`) | ✅ |
| Atomic claim on `printing` | contract: `WHERE status='queued'` | ✅ companion handles `{skipped:true}` | ✅ |
| Completion side-effects | server-side (order→ready) | n/a | n/a |

✅ No change. (The `MarkJobReceivedAsync` receipt endpoint is also already implemented, though the contract doesn't list it under §10 — it's in Appendix A as `/agents/jobs/:id/received`. ✅ match.)

### §11 Socket.IO Authentication

| Aspect | Contract | Current C# | Match? |
|---|---|---|---|
| URL | `https://<domain>/?XTransformPort=3003` | ✅ `SocketIoRealtimeClient.BuildUri` | ✅ |
| JWT acquisition | `POST /agents/token` | ✅ `ExchangeDeviceTokenForJwtAsync` | ✅ |
| JWT shape | HS256, sub=`agent:<agentId>`, shopId, role=`staff`, 15-min TTL | n/a (server signs) | n/a |
| Emit `auth {token}` | contract: yes | ✅ `EmitAsync("auth", new {token = _currentJwt})` | ✅ |
| Listen `auth-ok`/`auth-error` | contract: yes | ✅ already listened | ✅ |

✅ No change.

### §12 Socket.IO Event Names & Payloads

| Event | Direction | Contract payload | Current C# | Match? |
|---|---|---|---|---|
| `auth` | client→server | `{token}` | ✅ emitted | ✅ |
| `auth-ok` | server→client | `{shopId, role}` | ✅ listened | ✅ |
| `auth-error` | server→client | `{error}` | ✅ listened (refreshes JWT) | ✅ |
| `job.dispatch` | server→client | `PrintJobDto + deliveryAttemptId?` | ✅ listened → enqueues | ✅ |
| `job.printing` | server→client | `{jobId, fileId}` | NOT listened (informational) | ✅ ok to skip |
| `job.completed` | server→client | `{jobId, fileId}` | NOT listened (informational) | ✅ ok to skip |
| `job.failed` | server→client | `{jobId, fileId, error?}` | NOT listened (informational) | ✅ ok to skip |
| `order.paid`/`order.ready` | server→client | `{orderId, ...}` | NOT listened (informational) | ✅ ok to skip |
| `agent.heartbeat` | server→client | `{agentId}` | NOT listened (informational) | ✅ ok to skip |
| `agent.revoked` | server→client | `{agentId, agentName, cancelledJobs}` | ✅ listened → raises `Revoked` | ✅ |
| `delivery.received` | server→client | `{jobId, agentId, ...}` | NOT listened (informational) | ✅ ok to skip |
| **`device.command.issued`** | server→client | `{commandId, agentId, commandType}` | **NOT listened** | **NEW** — listen, trigger immediate `FetchPendingCommandsAsync` |
| **`device.revoked`** | server→client | `{agentId}` | **NOT listened** (only `agent.revoked` is) | **NEW** — listen, treat same as `agent.revoked` (clear credential + stop) |

**Action:** add `device.command.issued` + `device.revoked` listeners to `SocketIoRealtimeClient`. Both are additive (the contract §16 says new events are additive; existing names/payloads stable).

### §13 Reconnection & Polling Fallback

| Aspect | Contract | Current C# | Match? |
|---|---|---|---|
| Reconnection | `reconnection:true`, attempts ∞, delay 1s, max 30s, exp backoff+jitter | ✅ `Reconnection=true`, `ReconnectionAttempts=int.MaxValue`, `ReconnectionDelay=2000ms` | ⚠ contract says initial 1s, companion uses 2s — minor; align to 1s |
| Poll fallback (jobs) | `GET /agents/jobs/pending` every 2-5s | ✅ `HttpFallbackPoller` every 2s | ✅ |
| Poll fallback (commands) | `GET /companion/commands` every 2-5s | **NOT implemented** | **NEW** — add command polling alongside job polling |
| Heartbeat during poll | every 15s | n/a (no heartbeat timer today) | **NEW** — drive heartbeat at 15s always |
| Reconnect reconciliation | re-auth + poll jobs + poll commands + resume heartbeat | partial: re-auth ✅, poll jobs ✅, **poll commands ❌**, resume heartbeat ❌ | **NEW** — extend reconciliation to poll commands + restart heartbeat |

**Action:** align `ReconnectionDelay` to 1000ms; add command polling to the fallback poller; add a reconciliation hook on reconnect that polls both jobs and commands and restarts the heartbeat timer.

### §14 Error Codes & Retry Behavior

| Code | HTTP | Contract retry | Current C# | Match? |
|---|---|---|---|---|
| `UNAUTHORIZED` | 401 | do NOT retry; re-pair | companion treats 401 as revoked → clears credential | ✅ |
| `FORBIDDEN` | 403 | do NOT retry; device disabled | **companion conflates 403 with 401** | ❌ **MISMATCH** — distinguish 403 (disabled, keep credential, show "contact admin") from 401 (revoked, clear credential) |
| `NOT_FOUND` | 404 | (job gone) | companion marks job Cancelled | ✅ |
| `VALIDATION_ERROR` | 400 | do NOT retry | companion surfaces as permanent failure | ✅ |
| `RATE_LIMITED` | 429 | wait `Retry-After` | companion does not parse `Retry-After` | ⚠ minor — add `Retry-After` parsing |
| `TICKET_EXPIRED` | 401 | re-pair (new ticket) | n/a (no ticket flow yet) | **NEW** — surface as "Pairing code expired, generate a new one" |
| `TICKET_CONSUMED` | 409 | re-pair (new ticket) | n/a | **NEW** — surface as "Pairing code already used, generate a new one" |
| `DEVICE_DISABLED` | 403 | same as FORBIDDEN | n/a | **NEW** — same handling as 403 |
| 5xx | | exp backoff 1,2,4,8,max30; max 5 retries | companion uses 3^n*5 cap 600 — different curve | ⚠ minor — align to 1,2,4,8,30 cap |

**Action:** add 403/`DEVICE_DISABLED` distinct from 401; add `Retry-After` parsing for 429; map ticket-specific errors to user-facing strings. The 5xx curve mismatch is minor — keep the existing curve (it's stricter, which is safe).

### §15 Token Revocation & Device Disablement

| Aspect | Contract | Current C# | Match? |
|---|---|---|---|
| `DELETE /shops/me/devices/:deviceId` (shopkeeper) | shopkeeper-side | n/a | n/a |
| `PATCH /shops/me/devices/:deviceId {isEnabled}` (shopkeeper) | shopkeeper-side | n/a | n/a |
| Effect on companion | revoked → 401 on next call OR `agent.revoked`/`device.revoked` event; disabled → 403 on next call | companion handles 401 + `agent.revoked` today; must add 403 + `device.revoked` | **NEW** (covered in §3 + §12) |
| Token rotation | contract: "future, not implemented" | n/a | n/a |

**Action:** covered by §3 (403 handling) and §12 (`device.revoked` listener).

### §16 API Versioning & Backward Compatibility

| Aspect | Contract | Current C# | Match? |
|---|---|---|---|
| `protocolVersion: 1` returned in pairing | companion should store + validate | not stored today | **NEW** — store on device record; refuse to operate if a future backend returns a higher `protocolVersion` the companion doesn't support (forward-compat gate) |
| `/agents/*` unchanged | contract: yes | ✅ companion uses them as-is | ✅ |
| `/companion/*` additive | contract: yes | companion adds new client methods | ✅ plan |
| `print-agent-salt` fixed | n/a (server) | n/a | n/a |
| Socket.IO event names stable; new additive | contract: yes | companion adds listeners for new events, keeps existing | ✅ plan |

**Action:** store `ProtocolVersion` on the device record; add a startup check that refuses to connect if the stored version is > the companion's supported version (1).

### Appendix C — PrintEXE Configuration

| Aspect | Contract | Current C# | Match? |
|---|---|---|---|
| Config section | `PrintShop:BaseUrl`, `PrintShop:PollingInterval`, `PrintShop:HeartbeatInterval`, `PrintShop:SocketIoUrl` | companion reads `Backend:BaseUrl` etc. | ❌ **MISMATCH** — add a `PrintShop` options section as the contract-compatible alias; keep `Backend` for backward compat (prefer `PrintShop` if both set) |
| `PollingInterval` | `00:00:03` (3s) | companion `PollFallbackInterval` = 2s | ⚠ minor — align default to 3s OR keep 2s (both within the contract's 2-5s range) |
| `HeartbeatInterval` | `00:00:15` (15s) | companion = 60s | ❌ **MISMATCH** (already in §4) |
| Token storage path | `%AppData%\PrintEXE\device-token.dat` | `%LocalAppData%\PrintShop\Companion\Security\device.dat` | ❌ **MISMATCH** — must support the contract path; migrate from the old path; do not silently drop existing credentials |

**Action on the token path:** this is the most delicate change. The contract says `%AppData%\PrintEXE\device-token.dat`. The companion currently uses `%LocalAppData%\PrintShop\Companion\Security\device.dat` (with a legacy migration from `%AppData%\PrintShop\Companion\device.dat`). The safe approach:
1. Add `PrintShop:DeviceTokenPath` option (default = the contract path `%AppData%\PrintEXE\device-token.dat`).
2. On startup, `LocalStorageLayout` checks: contract path → old `PrintShop\Companion\Security\device.dat` → legacy `PrintShop\Companion\device.dat`, and migrates the FIRST one that exists to the contract path (atomic move, with a `.bak` backup of the source).
3. Keep the `PrintShop\Companion` queue DB + queue key where they are (the contract doesn't specify those paths; moving the encrypted DB is risky and unnecessary). Only the device-token path moves.

This keeps the contract happy (token at the documented path) without invalidating existing installs (migration) or disturbing the SQLCipher queue.

---

## Summary of required changes (implementation order)

**Phase 2 (pairing):**
1. Add `PairWithTicketAsync(ticket, deviceName, version?, os?, printerName?)` → `POST /companion/pair`. Map `TICKET_EXPIRED`/`TICKET_CONSUMED`/`VALIDATION_ERROR` to user states. Store `protocolVersion`.
2. Pair Computer screen: ticket input first, paste-token fallback.
3. Token storage path migration to `%AppData%\PrintEXE\device-token.dat` (with backward-compat migration).

**Phase 3 (connection):**
4. Start `SocketIoRealtimeClient` in `App.OnStartup` (not just Worker).
5. 15s heartbeat timer (always running, not just on events).
6. Reconnect reconciliation: re-auth + poll jobs + poll commands + restart heartbeat.

**Phase 4 (commands):**
7. `FetchPendingCommandsAsync()` → `GET /companion/commands`.
8. `ReportCommandStatusAsync(commandId, status, resultJson)` → `POST /companion/commands`.
9. `CommandExecutor` for the 7 allowlisted types with payload validation + expiry check + local dedup (SQLite `commands` table).
10. Listen `device.command.issued` → immediate poll; `device.revoked` → treat as revoked.

**Phase 8 (config + security):**
11. `PrintShop` config section alias (`BaseUrl`, `HeartbeatInterval=15s`, `PollingInterval=3s`, `SocketIoUrl`, `DeviceTokenPath`).
12. 403/`DEVICE_DISABLED` distinct from 401 in `HttpPrintShopBackend`.
13. `Retry-After` parsing for 429.

**Phase 9 (tests):**
14. Unit tests: ticket exchange (success + each error code), command validation/dedup/expiry, heartbeat interval config, command executor dispatch, `device.command.issued`/`device.revoked` handling.

**Phase 2 docs:**
15. Reconcile `docs/PROTOCOL.md` + `docs/CONTRACT_COMPATIBILITY.md` + `HONEST-STATUS.md` + `NOT-VERIFIED.md` with the frozen v1 contract (the two "conflicts" are now resolved; the contract is the source of truth).

## What still requires a Windows machine + staging backend + real printer (NOT claimable from this sandbox)

- End-to-end pairing via ticket (steps 1-6 of the brief's Phase 10 sequence).
- Reconnect/re-pair behavior on a real Windows restart.
- Remote command execution on a real PC.
- Physical test print + spooler evidence.
- Revocation propagation on a real device.

These will be listed as "remaining blockers / manual validation steps" in the Phase 2 final report, not claimed as done.
