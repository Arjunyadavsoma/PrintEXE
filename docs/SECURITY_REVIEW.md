# PrintEXE Phase 3 — Security Review

**Date:** 2026-11-17
**Branch:** `main` @ `6b7997c` (pushed to remote `main` per the "push to main only" directive)
**Contract:** `COMPANION_V1_CONTRACT.md` v1.0 (Frozen) + actual SaaS impl `9f0e8ff`
**Scope:** PrintEXE (the Windows companion). SaaS-side findings are documented for Agent 1 but NOT modified by this work.

This review follows the brief's §8 (Phase H) + §5 (Phase C) security requirements. Findings are split into **PrintEXE-side** (this repo, fixed/verified here) and **SaaS-side** (Agent 1's territory, documented for coordination).

---

## PrintEXE-side findings (this repo)

### P1 — Device-token storage (DPAPI) ✅ Fixed in Phase 3

**Requirement (brief §5):** atomic write, DPAPI, never overwrite valid with empty, preserve old until new is verified, restrict file permissions, never log.

**Before Phase 3:** `DeviceTokenStore.SaveAsync` used `File.WriteAllBytesAsync` directly — a crash mid-write could leave a truncated/corrupt file, losing the credential. No `.bak` recovery. No round-trip verification.

**After Phase 3:** `SaveAsync` is now a 6-step atomic-verify sequence:
1. Validate the credentials serialize + DPAPI-encrypt to memory BEFORE touching the existing file (fail fast on null/empty device token — refuses to overwrite a valid token with empty).
2. Write to `<path>.tmp` in the SAME directory (so the rename is atomic on Windows — same-volume moves are atomic).
3. Back up the existing file to `<path>.bak` (overwrites any stale `.bak`).
4. Atomic `File.Move(tmp → final, overwrite: true)` — same-volume, atomic on Windows. On failure, restores from `.bak`.
5. Round-trip verification: re-Load the new file (decrypt + deserialize). If it fails, restore from `.bak` + throw `InvalidOperationException`.
6. Delete the `.bak` only after the round-trip succeeds.

`LoadAsync` now falls back to the `.bak` if the primary file fails to load (graceful recovery), then promotes the `.bak` to primary.

**Token path (v1 contract Appendix C):** `%AppData%\PrintEXE\device-token.dat` (new default) with 3-tier backward-compat migration from `%LocalAppData%\PrintShop\Companion\Security\device.dat` (v0.5) and `%AppData%\PrintShop\Companion\device.dat` (v0.4.7). First-existing source wins; no overwrite of an existing contract-path token.

**DPAPI scope:** `DataProtectionScope.CurrentUser` — only the Windows user who encrypted can decrypt. No machine-scope, no roaming. The queue DB + key are at `%LocalAppData%` (non-roaming) so they stay together with the user.

**File permissions:** the file inherits the directory's ACL (`%AppData%\PrintEXE\` is user-owned by default; only the current user + Administrators can read). Phase 3 did NOT add an explicit ACL harden because the default ACL is already user-restricted; an explicit `File.SetAccessControl` restricting to the current user only is a documented future harden (it would break in unprivileged contexts).

**No logging of secrets:** `SaveAsync`/`LoadAsync` log only the operation ("Credentials persisted atomically", "Failed to load credentials"), never the token, never the cipher, never the JSON. The HTTP client redacts `Authorization` headers + any property containing "Token" in the Serilog setup (Phase 2, unchanged).

### P2 — Device-token authentication (HMAC) ⚠ Documented (SaaS-side)

**Requirement (brief §10 Phase H):** "Use a server-side secret/pepper stored in the deployment environment or secret manager, following a backward-compatible migration plan for existing device tokens."

**Actual SaaS (verified by reading `with-auth.ts` + `companion/pair/route.ts`):** the HMAC pepper is the **literal string `'print-agent-salt'`**, hard-coded in source at 3 sites, NOT an env var. The brief explicitly says this "must not be treated as a secret" — and indeed it isn't (it's a constant, not a secret).

**PrintEXE implication:** PrintEXE does NOT compute the HMAC (the server verifies the token; PrintEXE just sends `Authorization: Bearer <plaintext_device_token>`). So the literal pepper is not a PrintEXE vulnerability — it's an SaaS-side finding. The pepper being a constant means the security rests entirely on the 256-bit entropy of the random device token + the secrecy of the token at rest (DPAPI). This is acceptable as long as the token is high-entropy (it is — 32 bytes `crypto.randomBytes`) and never leaks.

**Recommendation for Agent 1:** move the pepper to an env var (`PRINT_AGENT_HMAC_PEPPER`) with a backward-compatible migration: hash each new token with the new pepper, but on auth failure, retry with the old pepper (so existing devices keep working until re-paired). This is a Phase H SaaS task.

### P3 — Pairing-ticket security ✅ Matches contract

**Requirement (brief §8 Phase 8):** high-entropy, only hashes stored, short expiry, atomic single-use, replay protection, rate limiting.

**Actual SaaS (verified):**
- Ticket = 32 bytes `crypto.randomBytes` → hex (256-bit entropy). ✅
- Stored as `SHA-256(ticket)` (NOT HMAC — the ticket IS the secret; SHA-256 is sufficient). ✅
- 5-minute TTL. ✅
- Atomic single-use: `UPDATE pairingTicket SET status='consumed' WHERE id AND status='active'` — 0 rows = 409 `PAIRING_TICKET_CONSUMED`. ✅
- Replay protection: a consumed ticket's status is `consumed`, not `active`, so a replay hits `PAIRING_TICKET_UNAVAILABLE`. ✅
- Rate limit: `pairing-consume` 10/60s/IP + `pairing-begin` 5/60s/IP. ⚠ (degrades to global with `CACHE_PROVIDER=memory` + no `TRUSTED_PROXY` — see P5).

**PrintEXE side:** validates the ticket format locally (64 hex chars, trimmed) before the network call; never logs the ticket; clears it from the ViewModel after successful pairing (single-use, now consumed). ✅

### P4 — Device revocation + disablement ✅ Implemented (with documented SaaS gap)

**Requirement (brief §5 Phase C):** 401 (revoked) vs 403 (disabled) distinct; clear credential on revoke, NOT on disable.

**PrintEXE side:** `HttpPrintShopBackend.EnsureNotDisabledAsync` throws `DeviceDisabledException` on 403 across all 7 companion routes. `App.StartCloudConnectionAsync` wires `realtime.Revoked` → navigate to Login. The 401 path throws `InvalidOperationException("Device token rejected")` which the credential-restore logic treats as "credential invalid → go to Login".

**SaaS gap (documented, not fixed by me):** `withDeviceAuth` does NOT check `isEnabled`. A disabled device can still poll commands + claim jobs + report status. Disabling only stops NEW commands from being queued. This means the 403 `DeviceDisabledException` path in PrintEXE is defense-in-depth for a contract the SaaS doesn't yet enforce. The shopkeeper's "Disable" button is a UI hint, not a hard gate. Agent 1 should add `if (!agent.isEnabled) throw ForbiddenError` to `withDeviceAuth`.

### P5 — Rate limiting ⚠ Documented (SaaS-side)

**Actual SaaS:** rate limit keys are `rl:<scope>:<ip>` where `ip` is from `X-Forwarded-For` ONLY if `TRUSTED_PROXY` is set in production. Without it, `ip = 'unknown'` → a GLOBAL limit shared across all clients. With `CACHE_PROVIDER=memory` (Vercel default), the cache is per-invocation, so the limit is effectively a no-op (every cold invocation sees count=1).

**PrintEXE side:** backs off on 429 (`PairingTicketException.IsRateLimited` → "Too many pairing attempts. Wait a minute."). ✅

**Recommendation for Agent 1:** set `TRUSTED_PROXY` to the Caddy gateway's IP, switch `CACHE_PROVIDER=redis` (Upstash), and the rate limits become per-IP as intended.

### P6 — Command security (no shell/file/URL execution) ✅ Verified

**Requirement (brief §7 Phase E):** remote commands must NEVER support arbitrary shell, PowerShell, process execution, file read/write, URL fetching, or dynamic code. Unknown commands fail closed.

**PrintEXE side:** `CommandExecutor` validates the command type against a 7-value allowlist (`CommandType.All`); unknown types return a structured failure (never executed). `cancel_job`/`retry_job` parse a `jobId` (UUID string) from the JSON payload and pass it to the queue — never to a shell, never to a file path, never to a URL. `test_print` enqueues a synthetic job (NOT a direct spooler call) so the durable-queue idempotency applies. `diagnostics` redacts: returns only app version, device id, OS, machine name, selected printer, queue summary (status→count). No file contents, no customer documents, no PDF paths, no PII. ✅

### P7 — Command idempotency (durable) ✅ Implemented

**Requirement (brief §7 Phase E):** "durable command execution tracking in the local SQLite database" with atomic claim + restart recovery + no-duplicate-execution-after-restart.

**Before Phase 3:** in-memory LRU (`CommandDedupStore`) only — insufficient across restarts.

**After Phase 3:** `DurableCommandStore` (SQLite `companion_commands` table, shares the SQLCipher-encrypted queue DB) with:
- Atomic claim: `INSERT ... ON CONFLICT(command_id) DO NOTHING` — first caller wins; subsequent callers get the stored terminal state.
- Restart recovery: a command left in `claimed`/`executing` (crash before terminal report) is visible to a fresh store instance; the poller re-executes ONLY if the command is still not expired, else reports `failed ("interrupted by restart, expired")`.
- Terminal commands are re-reported (idempotent on the backend) but NEVER re-executed.
- Bounded retention: `PurgeOldTerminalAsync` deletes only terminal rows older than the cutoff; non-terminal rows are NEVER deleted (brief §7).

The in-memory LRU is kept as a fast-path cache on top (avoids the SQLite round-trip on hot duplicates within one process).

### P8 — Cross-shop / cross-device authorization ✅ Server-enforced

**Requirement (brief §8 Phase H + §10 Phase I):** "Cross-shop access is impossible."

**PrintEXE side:** the companion authenticates with a single device token (its own). It cannot access another device's commands (the SaaS filters `GET /companion/commands` by `agentId` derived from the token hash) or another shop's jobs (the SaaS filters by `shopId` derived from the token). The companion does NOT have a way to request another shop's data — the token is the scope. ✅ (server-enforced)

### P9 — Secrets in logs / diagnostics / errors ✅ Verified

**PrintEXE side:** 
- The Serilog setup redacts `Authorization` headers + any property containing "Token".
- `PairWithTicketAsync` logs `agentId` + `shopName` + `protocolVersion` on success — NEVER the ticket or the device token.
- `HeartbeatAsync` logs the server timestamp — never the token.
- `diagnostics` command result includes app version + device id + OS + machine name + printer + queue summary — NO file contents, NO customer documents, NO PII.
- API errors are not enriched with the token; the error envelope carries `{code, message, details}` only.

### P10 — Migration safety ✅ Verified

**PrintEXE side:** the `companion_commands` table is additive (`CREATE TABLE IF NOT EXISTS`) in the same SQLCipher queue DB — no destructive migration, no column drops, no rename. The `schema_version` table bumps to 3 (idempotent `INSERT OR IGNORE`). The token-path migration (Phase 2) preserves the old credential until the new path is verified (Phase 3 `SaveAsync` extends this with the `.bak` pattern). No `IF NOT EXISTS` alone is relied on for correctness — the atomic claim uses `ON CONFLICT DO NOTHING` which is a real constraint, not a schema check.

---

## SaaS-side findings (documented for Agent 1, NOT modified by PrintEXE)

| # | Finding | Severity | Required action (Agent 1) |
|---|---|---|---|
| S1 | HMAC pepper is the literal `'print-agent-salt'`, not an env var | Medium | Move to `PRINT_AGENT_HMAC_PEPPER` env var; backward-compatible dual-pepper auth retry |
| S2 | `withDeviceAuth` does NOT check `isEnabled` | Medium | Add `if (!agent.isEnabled) throw ForbiddenError('DEVICE_DISABLED')` to `withDeviceAuth` |
| S3 | Rate-limit cache is per-invocation memory on Vercel (`CACHE_PROVIDER=memory` default) | Medium | Switch to `CACHE_PROVIDER=redis` (Upstash) for production |
| S4 | Rate-limit IP key degrades to `unknown` without `TRUSTED_PROXY` | Low | Set `TRUSTED_PROXY` to the Caddy gateway IP |
| S5 | `/agents/jobs/[id]/received` returns 200 (not 409) for terminal-state jobs — contract says 409 | Low | Either update the contract to say 200 or change the route to 409; PrintEXE already handles 200 |
| S6 | No `withDeviceAuth` route is rate-limited (a rogue companion can poll as fast as it wants) | Low | Add a per-device rate limit on `GET /companion/commands` + `GET /agents/jobs/pending` |
| S7 | `vercel.json` `REALTIME_PROVIDER=memory` default → realtime events don't cross Vercel invocations | Medium | Set `REALTIME_PROVIDER=socketio` + wire the SocketIoRelay adapter in production |

---

## Verdict

PrintEXE-side security is **production-oriented** for Phase 3: DPAPI storage is atomic + verified + recoverable, command execution is allowlisted + durable-idempotent + redacted, revocation is handled (with the SaaS-side `isEnabled` gap documented). The remaining vulnerabilities are SaaS-side (pepper, isEnabled, rate-limit cache, realtime provider) — documented for Agent 1, not modifiable from this repo. No PrintEXE-side critical vulnerability remains.
