# Phase 10.3 — Real SQLCipher, Paired Backend Integration & Encrypted Upgrade Verification (Report)

**Date:** 2026-11-28 · **Baseline:** Phase 10.2 `862cd3e` · **HEAD this phase:** `967b037` (fast-forward commits only, no force, no release/tag)

## 1. Executive summary

Phase 10.3 implemented the REAL-database verification layer that Phase 10.2 honestly deferred:

- **16 new SQLCipher tests** (12 required lifecycle + 5 negative/recovery scenarios) now run **green on Windows CI** (run 46160668078) against the application's production `QueueKeyStore`/DPAPI + `SqliteJobQueue`/SQLCipher classes.
- **A real encrypted queue database** is now materialized on the MSI matrix runner by `tools/PrintEXE.QueueHarness` — the app's own `EnqueueAsync` path (real pending + printed jobs, real PDF payloads) — and the **MSI upgrade preservation is verified machine-readably** (key fingerprint continuity, still-encrypted header, job states, PDF byte-identity, `PRAGMA integrity_check`).
- **CI exposed and fixed 11 test-harness defects** and documented **two real implementation facts**: (a) the Phase 9 quarantine contract (undecryptable DB → `queue.db.corrupt-<stamp>`, byte-identical, journaled, fresh encrypted DB), (b) a provider bind race in multi-TFM test hosts (deterministic in the shipped app; handled with honest skips).
- **Real backend integration is BLOCKED** — documented with the exact missing prerequisites (`docs/phase10.3/BACKEND_BOUNDARY.md`); contract tests (`FakePrintelyxBackend`) remain labeled as contract-level. No substitute was fabricated.
- Final matrix run 46163074067 (commit 5a43d05) was still executing at report time; its result and the last remaining gap (harness seed transient-lock retry verification) are recorded in the execution log below.

## 2. Baseline commit and repository state

- HEAD at audit start: `862cd3e` = origin/main, clean tree. Commits since: `BASELINE.md` audit → tests → QueueHarness → CI-driven fixes. Every commit fast-forward, message-referenced to the run that exposed the issue.
- Audit document: `docs/phase10.3/BASELINE.md` (implementation map, test-coverage gaps, dependency boundary).

## 3. SQLCipher architecture audit (evidence-backed)

`SqlCipherInitializer` sets the e_sqlcipher provider directly (no Batteries reflection) and PROVES availability via a keyed `:memory:` probe + `PRAGMA cipher_version` (4.5.2 community). `SqliteJobQueue` ctor fail-closes without a key store or without encryption. `QueueKeyStore`: 32-byte RNG key, DPAPI `CurrentUser` at `%LocalAppData%\PrintShop\Companion\Security\queue.key`, Phase 5 no-overwrite-on-unprotect-failure, `x'<hex>'` raw-key form, in-memory cache + `ClearCache()` on shutdown. Migrations: `schema_version` + additive v0.5 ALTERs + v0.4.7 migration with backup + row-count reconciliation.

## 4. DPAPI key lifecycle — VERIFIED

SQL-1 (first-run creation; blob ≠ raw 32 bytes), SQL-2 (persistence across restarts; same SHA-256 fingerprint, file untouched), SQL-3 (same-user retrieval from a copied key file), SQL-12 (ACLs: owner read, no Everyone-write). **The key never appears in any log or output — only SHA-256 fingerprints.**

## 5. Real backend pairing evidence

**BLOCKED** — see `docs/phase10.3/BACKEND_BOUNDARY.md`. Contract-level coverage: `AgentContractTests`/`SelfHealContractTests` (unchanged, green).

## 6. Queue database creation evidence

SQL-4: a genuine `EnqueueAsync` creates `queue.db` whose first 16 bytes are NOT the plain-SQLite magic. On the matrix runner, `QueueHarness seed` does the same at the app's real data root (snapshot JSON: path, size, header hex, key fingerprint, cipher version, job list, PDF hashes — no secrets).

## 7. Encryption proof

Not derived from a log message: 16-byte header check (`SQLite format 3\0` must NOT be present), enforced by a throw-guard in the harness seed, plus `PRAGMA integrity_check = ok` with the correct key (SQL-8), plus WAL/SHM lifecycle (SQL-10) and pool-cleanup movability (SQL-11).

## 8. Wrong-key rejection — VERIFIED as the Phase 9 quarantine contract

CI revealed the REAL documented behaviour (SqliteJobQueue `EnsureSchemaOrRecoverAsync`): an undecryptable DB (wrong key, foreign plaintext file) is **quarantined byte-identical as `queue.db.corrupt-<stamp>`**, a fresh ENCRYPTED DB is created, and the failure is journaled to `Database/recovery-log.jsonl`. SQL-6/SQL-7/Scenario-A/B assert exactly this (quarantine file exists, bytes identical to pre-failure, fresh DB encrypted + operational, journal present, repeat guard bounded). No plaintext fallback; no destructive cleanup.

## 9. Encrypted upgrade preservation

Machine-readable comparison implemented (`QueueHarness verify`): key fingerprint continuity, still-encrypted header, job-count + per-job state identity (pending stay pending, printed stay printed), PDF byte-identity, integrity check, no duplicates/loss. First integration run hit a transient file-lock (46160668069) — a bounded 3-attempt retry for that specific IOException class landed in 5a43d05; the confirming run was in progress at report time.

## 10-12. Corruption recovery / missing key / permission & disk-full

- **B (corrupt):** page-1 damage → quarantine + journal (verified).
- **C (missing key):** replacement key generated for FUTURE databases; the existing encrypted image is quarantined byte-identical, never overwritten; restoring the original key + quarantined image recovers every job — the documented operator path, verified end-to-end.
- **D (permission):** failure surfaces; classified `Unknown` under CANTOPEN-under-ACL — **documented classifier gap** (SqliteError 14 has no dedicated mapping); no destructive cleanup.
- **E (disk full):** genuine SQLite FULL via `max_page_count` + page-growth UPDATE on the encrypted table; classified `DiskFull`; queue fully recoverable afterwards.

## 13. Standard-user installer refusal (G1)

Still **PARTIALLY VERIFIED**: refusal proven by registration absence; the msiexec exit code is not observable through the standard-user cmd hand-off (documented in the 10.2 report; unchanged this phase).

## 14. Phase 10.2 regression matrix

windows-build 46160668078: **SUCCESS** (all suites green incl. the 16 new tests). Matrix runs through the phase kept A/B/C/D/E/H/J/I + S7/F2/G2 VERIFIED (last full evidence set: 46160668069 results file). No gate weakened; skips are reported as skips.

## 15-17. CI runs, artifacts, SHA-256

- Tests: 46158534088 (6+3 fail → fixed), 46159727262 (2 fail → fixed), **46160668078 SUCCESS**.
- Matrix: 46157255871, 46158533976, 46160668069 (transient lock), **46163074067 (in progress at report time)**.
- Evidence artifacts: `msi-validation-evidence` per run (validation-results.json/md, MSI logs, QueueHarness JSON, ACL captures). Local copies under `download/phase10_evidence/phase10.3/`.
- MSI SHA-256 (final built pair, run 46160668069 preflight): recorded in that run's build job log (same preflight gates as 10.2).

## 18. Remaining risks

1. **Real backend integration BLOCKED** (credentials) — the dispatch → download → SHA-256 → insert → report path is untested against production.
2. **CANTOPEN classifier gap** — SqliteError 14 classifies `Unknown`; a dedicated mapping should be considered upstream (not changed in this phase to avoid behaviour drift).
3. **Provider bind race in multi-TFM test hosts** — shipping artifacts unaffected; test-host skips are the honest handling.
4. **G1 exit code** remains not independently observable (10.2 limitation).
5. **Matrix run 46163074067** was executing at report time — its outcome must be inspected before declaring Stage 4/5 fully green on the runner.

## 19. Final release-readiness assessment

**PARTIALLY READY.** The encrypted local-data layer is now genuinely verified (real operations, real encryption proofs, real recovery). Release remains gated on: (a) the real-backend integration path (BLOCKED on credentials), (b) the confirming matrix run, (c) the standing 10.2 items (Win10/11 desktop pass, code signing). No release or tag was created.
