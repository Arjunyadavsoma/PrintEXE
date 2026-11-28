# Phase 10.3 — Baseline and Repository Audit

Audit performed 2026-11-28 before any code change.

## Verified commit and working-tree state

- **HEAD:** `862cd3e` — `docs(phase10.2): final report - S7/F/G/I root causes + resolutions, full matrix OVERALL PASS (run 46154774959: 11 VERIFIED / 3 PARTIALLY / 0 FAILED)`
- **origin/main:** `862cd3e` (identical — in sync, fast-forward only, no force-push history)
- **Working tree:** clean at audit start
- **Phase 10.2 validation run:** 46154774959 (msi-validation, SUCCESS; windows-build 46154774948, SUCCESS)
- **Commits since Phase 10.2 baseline:** 0 (this phase starts at the 10.2 tip)

## Relevant implementation files (SQLCipher / queue / backend lifecycle)

| Area | File | Key facts verified by reading the code |
| --- | --- | --- |
| Provider init | `src/PrintShopCompanion.Infrastructure/Storage/SqlCipherInitializer.cs` | Direct `SQLitePCL.raw.SetProvider(new SQLite3Provider_e_sqlcipher())` (v0.7.2 fix — no Batteries reflection); availability **proven** via `:memory:` keyed probe + `PRAGMA cipher_version` (4.5.2 community); idempotent/thread-safe; static `IsEncryptionAvailable` |
| Key lifecycle | `src/PrintShopCompanion.Infrastructure/Storage/QueueKeyStore.cs` | 32-byte `RandomNumberGenerator` key; DPAPI `CurrentUser` at `%LocalAppData%\PrintShop\Companion\Security\queue.key`; Phase 5 fail-closed fix — unprotect failure **throws, never overwrites**; in-memory cache + `ClearCache()` on shutdown; key passed as `x'<hex>'` raw-key form (no PBKDF2) |
| Queue DB | `src/PrintShopCompanion.Infrastructure/Queue/SqliteJobQueue.cs` (1482 lines) | ctor calls `SqlCipherInitializer.Initialize` then resolves the key; **fail-closed** — throws if `keyStore` present but encryption unavailable or key load fails; connection `Mode=ReadWriteCreate`, `Cache=Shared`; schema `CREATE TABLE IF NOT EXISTS jobs` + `schema_version`; v0.4.7 migration with `PrepareQueueMigration()` backup + row-count reconciliation |
| Recovery | `src/PrintShopCompanion.Infrastructure/Queue/DatabaseRecovery.cs` | `DbFailureClass` classification (`KeyMismatch → StopPermanent`), quarantine (not delete), repeat guards, "probing every 60s" loop |
| Pending ops | `src/PrintShopCompanion.Infrastructure/Sync/SqlitePendingOpsQueue.cs` | Same `queue.db`, same SQLCipher provider requirement |
| Backend | `src/PrintShopCompanion.Infrastructure/Backend/{HttpPrintShopBackend,HttpShopBackend,SocketIoRealtimeClient,JobOwnershipValidator}.cs` | Production defaults `https://printelyx.cc.cd` (API) and `https://printelyx.cc.cd/?XTransformPort=3003` (Socket.IO); JWT pairing-token validation params; device token file `%AppData%\PrintEXE\device-token.dat` (DPAPI) with `DeviceTokenPath` override |
| Pairing | `src/PrintShopCompanion.Core/Options/Options.cs` | `BaseUrl`/`SocketIoUrl`/`DeviceTokenPath` config; pairing sets BaseUrl |
| Job model | `src/PrintShopCompanion.Core/Models/PrintJobRecord.cs`, `DeliveryStatus.cs` | `JobStatus.Pending/...`, `DeliveryStatus` v0.5 rename; `JobId` idempotency |

## Existing test infrastructure

| Project | Covers |
| --- | --- |
| `tests/PrintShopCompanion.Queue.Tests` | `SqliteJobQueueTests` (idempotency, ordering, retries) — **uses `keyStore: null` ⇒ PLAIN SQLite**; `CorruptionRecoveryTests`; `DurableCommandStoreTests`; `QueuePriority/StressTests`; `JobProcessorPauseResumeTests`; `StorageLayoutTests`; `InfrastructureDiCompositionTests` |
| `tests/PrintShopCompanion.Infrastructure.Tests` | `MigrationTests`, `SqliteConnectionPragmasSafetyTests`, `DiagnosticsServiceTests` |
| `tests/PrintShopCompanion.Integration.Tests` | `FakePrintelyxBackend` + `AgentContractTests` / `SelfHealContractTests` — **contract tests, explicitly NOT real-backend integration** |
| `installer/msi-validation.ps1` | Phase 10.2 matrix A–J + S7/SQLCIPHER/F2/G2 (harness) |

## Known limitations carried in from Phase 10.2 (this phase's targets)

1. No `queue.db` is created on an unpaired runner (lazy creation) ⇒ header proof, wrong-key/plaintext rejection, integrity, and DB-content upgrade preservation could not run there.
2. SQLCipher tests never exercise the ENCRYPTED path (existing queue tests pass `keyStore: null`).
3. G1 refusal evidence = registration absence only (exit code not captured).
4. Real-backend integration (`printelyx.cc.cd`) untested; no test-shop credentials exist.

## Required external dependencies (Stage 3 boundary)

- A Printelyx **test shop account** + **test device registration** + pairing credentials — **NOT AVAILABLE** in this environment (no GitHub secrets exist for a backend account; `gh secret list` shows none; account signup cannot be performed from CI). ⇒ Real-backend dispatch tests are classified **BLOCKED**, precisely and with evidence. No substitute backend will be fabricated; `FakePrintelyxBackend` contract tests remain labeled as contract-level.
- Everything else (DPAPI, SQLCipher, migrations, recovery) is testable **in-process through the application's real classes** with isolated storage paths and bounded timeouts — no external dependency.

## Audit conclusion

The implementation map above is the baseline of record for Phase 10.3. No application code has been changed during this audit.
