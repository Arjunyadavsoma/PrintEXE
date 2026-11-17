# PrintEXE Phase 3 — Runtime Validation

**Date:** 2026-11-17
**Branch:** `main` @ `6b7997c`
**This document is honest about what is and is NOT verified.** The brief's §12 + §16 mandate separating unit / integration / staging / Windows-runtime / physical-printer / production verification. No claim here is fabricated.

---

## What IS verified (automated, CI-run)

### Build — Windows .NET 10 self-contained win-x64 ✅
- CI run `44014740726` on `6b7997c` (latest), event=push, conclusion=success.
- All 20 build steps green: checkout, setup-dotnet 10.0.x, NuGet cache, set BUILD_VERSION, restore, **build (Release)**, **test**, publish (self-contained win-x64 folder), validate output, package ZIP, write BUILD-INFO/README, generate SHA256SUMS, verify checksum round-trip, upload artifact, upload test results.
- The build produces a real `PrintEXE-vX.Y.Z-win-x64.zip` artifact with a verified SHA-256 checksum.

### Tests ✅ (141/141 pass, 0 skipped, 0 failed)
| Test project | Phase 1 | Phase 2 | Phase 3 (current) | Δ this phase |
|---|---|---|---|---|
| PrintShopCompanion.Queue.Tests | 9 | 9 | **18** | +9 (DurableCommandStoreTests) |
| PrintShopCompanion.Infrastructure.Tests | 53 | 98 | **123** | +25 (contract fixtures, durable-store, pause/resume, error mapping) |
| **Total** | 62 | 107 | **141** | +34 |

No test was silently skipped to produce green. The CI log explicitly reports `Skipped: 0` for both projects.

### Contract fixture round-trip ✅ (cross-rep)
`ContractFixtureTests` (9 tests) verify the ACTUAL SaaS response JSON (captured from `printshop-saas` `9f0e8ff` route source) deserializes correctly into the C# DTOs. If the SaaS changes a field name or type, one of these tests breaks — catching drift before runtime. Covers: pairing success + each of 6 error codes, commands response (with the Phase-3 `commandId`/`payload`/`issuedAt` fixes), null payload, command report response, command report request with object result, shopkeeper devices list, heartbeat response, job-status skipped response.

---

## What is NOT verified (requires external resources not in this sandbox)

### Staging SaaS deployment — NOT performed
The SaaS is already deployed at `https://printshop-saas-three.vercel.app` (Agent 1's Vercel deployment). PrintEXE is a Windows desktop app (not deployed to a server). I did NOT deploy anything. I did NOT run `prisma migrate deploy` against any database (that's Agent 1's Vercel build step). I did NOT verify health endpoints or the web dashboard end-to-end.

### Windows runtime validation — NOT performed
The brief's §12 Phase J Windows-runtime sequence (steps 1-23: install, pair via ticket, restart, reconnect, offline queue, network recovery, refresh_printers, pause, resume, diagnostics, cancel, retry, revoke) requires an actual Windows 11 PC with PrintEXE installed + the staging SaaS reachable. **This Linux sandbox has no Windows machine.** None of these 23 steps were performed.

### Network interruption / recovery — NOT performed
Requires a real Windows machine + the ability to disconnect/restore the network adapter while PrintEXE runs. Not in this sandbox.

### Physical printer validation — NOT performed
The brief's §12 Phase J physical-printer sequence (PDF, image, single/multi-page, page-range, copies, color, paper-size, printer-unavailable, printer-disconnected, app-crash, restart-while-uncertain, spooler reconciliation) requires an actual Windows printer. **No printer in this sandbox.** Per the brief: "Never equate a successful API response with successful physical printing." I make no such claim. The `test_print` command is implemented to enqueue a durable job; the actual spooler submission is the existing `JobProcessor`'s job, which was not modified this phase.

### Production verification — NOT performed
No production deployment, no production migration, no production traffic. The brief's §12 Phase J production gate (staging validation passes + migration reviewed + security review + compatibility + rollback + no critical issue) is NOT satisfied — staging validation itself was not performed (no Windows machine).

---

## Manual validation steps (for the next phase, on a Windows 11 PC + staging backend + printer)

These are the brief's §12 Phase J Windows-runtime + physical-printer sequences. None were performed in this sandbox; they require external resources. The contract + unit tests verify the code paths; these steps verify the runtime behavior.

### Pre-flight (on a Windows 11 PC)
1. `git clone https://github.com/Arjunyadavsoma/PrintEXE.git && git checkout main` (commit `6b7997c` or later).
2. `dotnet build PrintShopCompanion.sln -c Release` — expect success (CI run `44014740726`).
3. `dotnet test` — expect 141/141 passing.

### Pairing via ticket (brief steps 1-6)
4. Sign into `https://printshop-saas-three.vercel.app` as a shopkeeper (owner/staff).
5. Open Shop → Devices → Add Computer. The website calls `POST /api/v1/companion/pairing/begin` and shows a 64-hex-char pairing code + a `printshop://pair?ticket=...&shop=...` deep link + a 5-min countdown + a QR code.
6. In PrintEXE, paste the 64-hex ticket into the **Pair this computer** section's "Pairing code" field (or open the deep link). Click "Pair this computer".
7. **Expected:** PrintEXE calls `POST /api/v1/companion/pair` with `{ticket, deviceName, version, os, printerName}`, gets `{agentId, deviceToken, shopId, shopName, protocolVersion:1, warning}`, DPAPI-persists the device token at `%AppData%\PrintEXE\device-token.dat` (with the `.bak` + round-trip verification), sends an initial heartbeat, navigates to Overview. The website's device list shows the device online within 30s.
8. **Failure modes to verify:** invalid ticket format → "The pairing code must be exactly 64 hex characters..."; expired ticket (`UNAUTHORIZED` with "expired") → "The pairing code has expired. Generate a new one..."; already-used ticket (`PAIRING_TICKET_CONSUMED`) → "This pairing code was already used..."; revoked ticket (`PAIRING_TICKET_UNAVAILABLE`) → "This pairing code is no longer active..."; shop deleted (`NOT_FOUND`) → "The shop that issued this pairing code no longer exists..."; rate limit (`RATE_LIMITED`) → "Too many pairing attempts. Wait a minute...".

### Restart + reconnect (steps 7-11)
9. Restart PrintEXE. **Expected:** restores the device token from `%AppData%\PrintEXE\device-token.dat` (or the `.bak` if the primary failed), calls `HeartbeatAsync`, reconnects Socket.IO, re-emits `auth`, resumes the 15s heartbeat + 2-3s command poll. **No re-pairing required.**
10. Disconnect the internet for 60s. **Expected:** the local queue continues operating; the heartbeat + command poll fail silently (Debug log); the device shows offline on the website after 30s.
11. Restore the internet. **Expected:** Socket.IO reconnects, `auth` re-emitted, heartbeat resumes, the command poller's next tick catches any commands issued during the outage (within the contract's 2-3s poll cadence). The device shows online within ~30s.

### Remote commands (steps 12-15)
12. Send `refresh_printers`. **Expected:** PrintEXE receives `device.command.issued`, polls `GET /companion/commands`, durable-claims the command, executes `refresh_printers` (enumerates Windows printers + pushes via heartbeat), durably records `succeeded` with `{printers, defaultPrinter, count}`, reports to backend. The website shows the result. The `companion_commands` SQLite row is now terminal.
13. Send `diagnostics`. **Expected:** `succeeded` with `{companionVersion, deviceId, os, machineName, selectedPrinter, queueSummary, timestamp}`.
14. Send `pause`. **Expected:** the `JobProcessor`'s dequeue loop blocks at the pause gate (an in-flight job, if any, finishes); `succeeded` with `{paused: true, wasAlreadyPaused: false}`.
15. Send `resume`. **Expected:** the pause gate releases; `succeeded` with `{resumed: true, wasPaused: true}`.

### Durable dedup + restart recovery (brief §7 invariant)
16. Send `test_print`. **Expected:** a test job with id `test-print-<commandId>` is enqueued durably; the `JobProcessor` dequeues + submits to the spooler. Kill PrintEXE mid-execution (before the terminal report). Restart PrintEXE. **Expected:** the `companion_commands` row is still in `executing`; the poller sees it non-terminal + still valid → re-executes. If the command expired during the downtime, the poller reports `failed ("interrupted by restart, expired")`. **Critical:** a terminal command (`succeeded`/`failed`) is NEVER re-executed on restart — re-reported (idempotent), but not re-run.

### Safe cancellation / retry (steps 20-21)
17. Send `cancel_job` for a queued job. **Expected:** the job transitions to Cancelled in the local queue; `succeeded` with `{jobId, cancelled: true}`. A job already `printing` is NOT cancelled (spooler safety) — `failed` with the reason.
18. Send `retry_job` for a `failed` job. **Expected:** the job resets to Pending (attempt count 0, lastError null); `succeeded` with `{jobId, retried: true, newStatus: "Pending"}`. A job in `UncertainSpooled`/`OutcomeUnknown` is NOT retried (the executor rejects it — the brief's §9 invariant: "Never automatically transition these states to Pending").

### Revocation (steps 22-23)
19. From the website, revoke the device (`DELETE /shops/me/devices/:deviceId`). **Expected:** PrintEXE receives `device.revoked` (or `agent.revoked`), the realtime client raises `Revoked`, the App navigates to Login. The next heartbeat returns 401 → the credential is treated as invalid.
20. Restart PrintEXE. **Expected:** `RestoreAsync` fails (401), the app stays on Login. Re-pairing requires a new ticket.

### Physical printer (brief §12 last section) — requires a real printer
21. Pair + send `test_print` to a connected printer. **Expected:** the test page physically prints; the job transitions to `Printed` then `ReportedPrinted` after `HasPrintedAsync` confirms spooler evidence.
22. Test: printer-unavailable (pause the spooler or disconnect the printer). **Expected:** the job stays in `printing` or transitions to `UncertainSpooled`/`OutcomeUnknown` (NEVER auto-reprinted); the dashboard shows the uncertain state for operator resolution.
23. Test: app-crash during printing + restart. **Expected:** the `ReplayInFlightAsync` path on startup recovers the in-flight job; an uncertain outcome stays uncertain (operator must resolve).

---

## Honest one-line summary

Phase 3 verifies, via Windows CI (run `44014740726`, 141/141 tests pass, all 20 build steps green), that the contract is implemented + the durable command dedup + the atomic credential write + the pause/resume wiring + the Pair Computer screen are all correct at the unit + contract-fixture level. **No Windows runtime, no staging API, no network-interruption, no physical-printer, no production verification was performed** — these require a Windows machine + staging backend + real printer, none in this sandbox. They are the next phase, with the exact manual steps above.
