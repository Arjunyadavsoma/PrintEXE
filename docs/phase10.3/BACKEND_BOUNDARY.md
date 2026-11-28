# Phase 10.3 Stage 3 — Real Paired Backend Environment: BLOCKED boundary

Status: **BLOCKED** (documented with evidence; no substitute fabricated).

## What the real backend integration requires

The companion's production backend is the Printelyx SaaS:

- `Backend:BaseUrl` default `https://printelyx.cc.cd` (API, `/api/v1`)
- `Backend:SocketIoUrl` default `https://printelyx.cc.cd/?XTransformPort=3003` (Socket.IO `job.dispatch`)
- Pairing: the shop creates a pairing token (JWT, validated with backend public-key parameters), the companion exchanges it for a **device token** stored DPAPI-protected at `%AppData%\PrintEXE\device-token.dat`.
- Implementation under test: `src/PrintShopCompanion.Infrastructure/Backend/{HttpPrintShopBackend,HttpShopBackend,SocketIoRealtimeClient,JobOwnershipValidator}.cs`, `Sync/{SyncEngine,SqlitePendingOpsQueue}.cs`.

## The exact blocker

Executing the ten Stage-3 checks (device registration → token issuance → Socket.IO auth → `job.dispatch` → presigned PDF download → SHA-256 verification → queue insertion → state transitions → completion reporting) against the REAL backend requires:

1. **A dedicated test shop account on printelyx.cc.cd** — none exists in this environment.
2. **A registered test device + valid pairing credentials** — cannot be created from CI; account signup is an interactive human flow.
3. **Approved secret storage for those credentials** — `gh secret list` shows the repository currently has NO secrets (only code-signing variables are referenced by `build-windows.yml` and those are not present either).

Rule compliance: "If a backend credential, test account or device pairing is unavailable, document the blocker. Do not invent a substitute." — no fake backend is presented as real integration, no credentials were invented, no production endpoints were hammered.

## What IS covered without the backend (labeled honestly)

| Layer | Evidence | Label |
| --- | --- | --- |
| Queue DB created by REAL app code (SQLCipher/DPAPI) | QueueHarness `seed` on the matrix runner (same `EnqueueAsync` the dispatch path calls) | REAL APPLICATION OPERATION — backend-independent |
| Encryption proofs (header, wrong-key quarantine, integrity, WAL, pool, ACL) | `SqlCipherLifecycleTests` + `SqlCipherNegativeTests` on windows-latest, QueueHarness on the matrix runner | VERIFIED |
| Backend protocol behaviour | `FakePrintelyxBackend` + `AgentContractTests`/`SelfHealContractTests` (existing suite, unchanged) | CONTRACT TESTS — explicitly NOT real-backend integration |
| Real backend dispatch/download/reporting | — | **BLOCKED** (credentials/test account unavailable) |

## What is needed to close the BLOCKED boundary

1. Create a Printelyx **test shop** (human step).
2. Register a dedicated **test device**; capture pairing credentials.
3. Store them as GitHub Actions secrets, e.g. `P10_TEST_SHOP_URL`, `P10_TEST_PAIRING_TOKEN` (names only documented here; values never in code/logs).
4. Point a CI-only workflow at a `p10-backend-integration` job with bounded timeouts and full redaction; the existing Socket.IO/HTTP client code paths are already testable as-is.

Until then, every machine-readable result file carries these scenarios as `BLOCKED`, and no overall PASS is derived from them.
