# Contract sync — PrintEXE ↔ printshop-saas — 2026-11-26 (v0.7.2)

Verified the PrintEXE agent client field-by-field against the **actual
implementation** of `Arjunyadavsoma/printshop-saas` (commit `0701237`,
companion contract **v1.1**, SSOT `docs/integration/COMPANION_V1_CONTRACT.md`,
machine registry `src/lib/contracts/companion.ts`). The machine artifact
`companion-v1.schema.json` is copied byte-identical into
`docs/contracts/companion-v1.schema.json` (as the SaaS contract requires).

## Verified ALIGNED (no change needed)

- **Auth model**: device token as `Authorization: Bearer <token>` on all
  `/agents/*` + `/companion/*` routes; JWT (from `POST /api/v1/agents/token`,
  `{accessToken, expiresIn, shopId}`) only for Socket.IO — client DTOs match.
- **Pairing**: `POST /companion/pair` body caps enforced client-side exactly
  at the route's runtime zod (ticket 64-hex, deviceName ≤120, os/version ≤40,
  printerName ≤200) — PrintEXE deliberately follows the RUNTIME caps, which
  are stricter than the registry (255/80) — correct choice, see drift table.
- **Pair response mapping**: `{agentId, deviceToken, shopId, shopName,
  protocolVersion}` → DPAPI persistence → validated.
- **Pending jobs**: `GET /agents/jobs/pending` → `{jobs, failedJobs, agentId}`,
  dispatch gate semantics (queued + AUTO + not-held + unclaimed) understood;
  presigned `downloadUrl` (900 s TTL) consumed directly.
- **Status report semantics**: atomic claim (`printing` from `queued`),
  `skipped` + enriched crash-recovery reason, terminal `completed`,
  `failed` unconditional — client state machine matches.
- **Receipt**: `POST /agents/jobs/{id}/received` — checksum sent as bare
  64-hex (the `sha256:` prefix is stripped client-side), all-optional body,
  `cleanupScheduled`/`cleanupScheduledAt` response mapping.
- **Socket.IO**: URL `?XTransformPort=3003`, path `/`, app-level `auth` event
  `{token: <jwt>}` after connect, `auth-ok`/`auth-error` handling,
  `job.dispatch` enqueue, `device.command.issued` → immediate REST poll,
  reconnection + HTTP 2 s poll fallback.
- **Commands**: `GET/POST /companion/commands` (payload re-serialized for the
  executor; ack statuses incl. `outcome_unknown`).
- **Sync**: `/sync/pull` cursor/limit + `PullResult` change shape (entity, id,
  action, data, revision, updatedAt), `/sync/status`, `/devices/register`.
- **Shop snapshot**: `GET /companion/shop-snapshot`.
- **401/403 semantics**: disabled device = 403 with `DEVICE_DISABLED:` message
  prefix (code stays `FORBIDDEN`); cross-shop resources = 404, never 403;
  previous-token overlap window = read-only paths only.
- **Deployment defaults**: BaseUrl `https://printshop-saas-three.vercel.app`,
  ApiBasePath `api/v1`, SocketIoUrl `.../?XTransformPort=3003` — match
  `BackendOptions` defaults. NOTE (from the SaaS repo): Vercel deployments run
  `REALTIME_PROVIDER=memory` — no realtime push on Vercel; the agent's REST
  poll fallback is what actually delivers jobs there.

## FIXED in PrintEXE (v0.7.2) — drifts that would break against production

1. **`lastError: null` → 400** (would have failed EVERY claim/failure report):
   the SaaS route zod is `z.string().max(500).optional()` — NOT `.nullable()`.
   The client now (a) serializes status bodies with
   `JsonIgnoreCondition.WhenWritingNull`, (b) truncates `lastError` to 500 and
   `printerName` to 200 client-side. The integration fake now REJECTS
   `"lastError":null` with `400 VALIDATION_ERROR` exactly like production, so
   this drift can never silently return.
2. **Revocation/command events never matched**: `agent.revoked`,
   `device.revoked`, `device.command.issued` carry the SERVER-assigned agentId
   (PrintAgent uuid). The client compared against the LOCAL device GUID —
   never equal, so a revoked device kept printing. `IPrintShopBackend.
   ServerAgentId` (new) is captured from pair/heartbeat/pending responses and
   used for the match.
3. **`failedJobs` ignored**: pending responses with undeliverable jobs
   (DOCUMENT_MISSING, JOB_INVALID, STORAGE_UNAVAILABLE) were silently
   dropped; now logged with job ids + codes and never enqueued.
4. **`pdfSha256` unused**: the pending route sends it; the client now carries
   it into the record so the download integrity gate can verify it.

## Drift table (SaaS-side — raise upstream in printshop-saas)

| # | Item | Registry/doc says | Route actually does | Impact on agent |
|---|------|-------------------|---------------------|-----------------|
| 1 | `pairRequest.deviceName` cap | 255 | **120** | agent enforces 120 (correct) |
| 2 | `pairRequest.os` cap | 80 | **40** | agent enforces 40 (correct) |
| 3 | `jobStatusRequest.lastError` | `.nullable()` | `.optional()` only | fixed client-side; doc should change |
| 4 | `jobReceivedRequest.checksum` | required | all-optional | none |
| 5 | `DEVICE_DISABLED` / `TOKEN_OVERLAP_READ_ONLY` | error codes | message prefixes on `FORBIDDEN` | client matches on 403, tolerant either way |
| 6 | `pdfSha256` on pending job | not in registry | sent by route | now consumed |
| 7 | `Retry-After` on 429 | documented §14 | never set | no client reliance |
| 8 | `order.paid` socket event | documented §12 | no publisher | dormant; client does not depend on it |

## Test coverage added (this sync)

- `Server_agent_id_is_captured_and_null_lastError_is_never_serialized` —
  fails if the client regresses to serializing nulls or loses the server id.
- `Over_length_lastError_is_truncated_to_the_saas_cap` — 600-char error is
  truncated to 500 and accepted.
- The contract-faithful fake backend (status route zod emulation) protects
  all existing lifecycle tests against this class of drift.
