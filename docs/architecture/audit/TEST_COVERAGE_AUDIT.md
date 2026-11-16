# Test Coverage Audit

## printshop-saas

| Test | File | Type | Status | Evidence |
|---|---|---|---|---|
| Auth tests | `src/lib/auth/auth.test.ts` | Unit (Vitest) | NOT EXECUTED | File exists but not run in this audit |
| Container tests | `src/lib/container.test.ts` | Unit | NOT EXECUTED | File exists |
| Credential encryption | `src/lib/crypto/credential-encryption.test.ts` | Unit | NOT EXECUTED | File exists |
| DB tests | `src/lib/db.test.ts` | Unit | NOT EXECUTED | File exists |
| Payment tests | `src/lib/payments/payments.test.ts` | Unit | NOT EXECUTED | File exists |
| PhonePe webhook | `src/lib/payments/phonepe-v2-webhook.test.ts` | Unit | NOT EXECUTED | File exists |
| Print job idempotency | `src/lib/payments/print-job-idempotency.test.ts` | Unit | NOT EXECUTED | File exists |
| Webhook audit | `src/lib/payments/webhook-audit.test.ts` | Unit | NOT EXECUTED | File exists |
| Delivery coordinator | `src/lib/services/delivery-coordinator.test.ts` | Unit | NOT EXECUTED | File exists |
| Atomic claim | `src/lib/services/atomic-claim.test.ts` | Unit | NOT EXECUTED | File exists |
| Durable queue | `src/lib/services/durable-queue.test.ts` | Unit | NOT EXECUTED | File exists |
| Upload lifecycle | `src/lib/upload-lifecycle.test.ts` | Integration | NOT EXECUTED | File exists |
| Token consistency | `src/lib/token-consistency.test.ts` | Unit | NOT EXECUTED | File exists |
| Require dev | `src/lib/api/require-dev.test.ts` | Unit | NOT EXECUTED | File exists |
| Smoke test | `tests/smoke.spec.ts` | E2E (Playwright) | NOT EXECUTED | File exists |

**SaaS test status:** 15 test files exist but were NOT EXECUTED in this audit (requires `bun install` + `bun test` which was not performed to avoid modifying the repository). CI workflow runs `bun test` on every push.

## PrintEXE

| Test file | Tests | Type | Status | Evidence |
|---|---|---|---|---|
| `SqliteJobQueueTests.cs` | 7 | Unit | ✅ PASSED (Linux + Windows CI) | Queue idempotency, dequeue order, retry, crash replay, printed status, cancellation |
| `GitHubUpdateServiceTests.cs` | 11 | Unit | ✅ PASSED (Linux + Windows CI) | Version comparison, SHA-256 streaming, GitHub release JSON, UpdateState enum |
| `SqlitePendingOpsQueueTests.cs` | 10 | Unit | ✅ PASSED (Linux + Windows CI) | Enqueue, MarkCompleted, MarkFailed, MarkRetry backoff, in_flight exclusion |
| `MigrationTests.cs` | 10 | Unit | ✅ PASSED (Linux + Windows CI) | v0.4.7 migration: pending/completed/failed/multiple jobs, idempotency, backup, path isolation |
| `JobProcessorIdempotencyTests.cs` | 1 | Unit | ✅ PASSED (Linux + Windows CI) | Reports completed only after spooler confirms |
| `MagicLinkPairingServiceTests.cs` | 5 | Unit | ✅ PASSED (Linux + Windows CI) | Pairing validation |
| **Total** | **48** | | **All pass** | Verified via `dotnet test` on Linux .NET 10 SDK + Windows CI |

## Test Gap Analysis

| Gap | Repository | Severity | Recommendation |
|---|---|---|---|
| SQLCipher encryption verification (wrong key, no key, plaintext check) | PrintEXE | P0 | Add Windows integration test that creates DB with key, tries opening without key |
| v0.4.7 migration on real Windows | PrintEXE | P0 | Manual test: install v0.4.7, add jobs, upgrade to v0.5.5 |
| Physical printing test | PrintEXE | P0 (BLOCKED) | Test on real Windows printer with real PDF |
| Auto-update end-to-end test | PrintEXE | P1 | Test from v0.5.4 → v0.5.5 via auto-updater |
| Concurrent queue access test | PrintEXE | P1 | Test SqliteJobQueue + SqlitePendingOpsQueue accessing same DB concurrently |
| Socket.IO reconnection test | PrintEXE | P1 | Test reconnection after network loss |
| SaaS: subscription enforcement test | printshop-saas | P0 | Test that expired subscription blocks shopkeeper routes |
| SaaS: admin auth migration test | printshop-saas | P0 | Test JWT-based admin auth after migration from env vars |
| SaaS: cross-tenant access test | printshop-saas | P1 | Test that agent from shop A cannot access shop B's jobs |
| SaaS: payment webhook replay test | printshop-saas | P1 | Test duplicate webhook handling with null eventId |
