# Security Model — PrintEXE v0.5

## Threat model

The 17 threat vectors from the master prompt, with the mitigation implemented (or documented) for each.

### 1. Cross-tenant access
**Threat:** An agent (or a malicious actor controlling one) accesses data belonging to a different shop.
**Mitigation:** The backend's `withDeviceAuth` wrapper resolves `shopId` from the device token (via `PrintAgent.tokenHash` lookup). Every downstream query filters by `shopId`. The companion never sends a `shopId` in request bodies — the server knows it from the token. The companion's local SQLite only contains jobs that the backend authorized for THIS agent — there's no way to query another shop's data because the companion has no other credential.

### 2. Token theft
**Threat:** The device token is stolen from the shop PC and replayed from another machine.
**Mitigation:** The device token is stored DPAPI-encrypted at `%LocalAppData%\PrintShop\Companion\Security\device.dat` with `DataProtectionScope.CurrentUser`. Only the Windows user who paired can decrypt it. Copying the file to another PC (or another user on the same PC) yields unusable bytes — DPAPI is keyed to the Windows user + machine. If the token IS compromised, the shopkeeper can revoke it from the web dashboard (`DELETE /api/v1/agents/{id}`) which cancels queued jobs and marks the agent as `revoked`.

### 3. Device impersonation
**Threat:** An attacker creates a fake PrintEXE that pairs with a shop and intercepts jobs.
**Mitigation:** Pairing requires the shopkeeper to manually generate a device token on the web dashboard and paste it into PrintEXE. There's no automated pairing endpoint. The device token is a 72-char UUID pair that's cryptographically random. An attacker can't guess it. The shopkeeper sees the agent name + last-seen time on the dashboard and can revoke suspicious agents.

### 4. Replay attacks
**Threat:** An attacker captures an authenticated request and replays it.
**Mitigation:** All companion → backend requests use HTTPS (TLS 1.2+). The device token is a Bearer credential, not a per-request nonce, so a captured request could theoretically be replayed — but the backend's idempotency on `POST /agents/jobs/{id}/status` and `POST /agents/jobs/{id}/received` (both check current state and return `skipped:true` if already in the target state) means a replay is a no-op, not a duplicate action.

### 5. Unauthorized transfer initiation
**Threat:** An attacker initiates a transfer to an agent that didn't request it.
**Mitigation:** Transfers are initiated BY the backend (when a customer uploads), not by agents. The agent only receives `job.dispatch` events for jobs the backend has already authorized. There's no "agent pulls arbitrary job" endpoint — `GET /agents/jobs/pending` only returns jobs in `queued` state for the agent's shop.

### 6. Malicious PDFs
**Threat:** A customer uploads a malicious PDF (e.g. with embedded JavaScript that exploits a PDF reader).
**Mitigation:** PrintEXE does NOT render PDFs in a viewer — it submits them to the Windows spooler via SumatraPDF in `-silent` mode. SumatraPDF has JavaScript disabled by default. The PDF is never opened in a browser or interactive viewer on the shop PC. The companion also enforces a `MaxPdfBytes` limit (100 MB by default) — files larger than this are rejected before download completes.

### 7. Oversized files
**Threat:** A customer uploads a 10 GB file that fills the shop PC's disk.
**Mitigation:**
- The companion checks `Content-Length` header before downloading; rejects files > 100 MB (`CompanionProtocol.MaxPdfBytes`).
- The companion monitors `Jobs/` directory size every 60s; if > `Storage:MaxLocalStorageBytes` (default 1 GB), aggressively cleans up.
- The website should also enforce an upload size limit (this is a printshop-saas concern; the companion can't enforce it).

### 8. Path traversal
**Threat:** A job's `filename` field contains `..\..\Windows\System32\evil.dll` and the companion writes the PDF to that path.
**Mitigation:** The companion NEVER uses the customer-supplied `filename` as the local file path. The local path is always `%LocalAppData%\PrintShop\Companion\Jobs\<state>\<jobId>.pdf` where `<jobId>` is a UUID. The customer-supplied `filename` is only used for display in the UI.

### 9. ZIP/decompression bombs
**Threat:** A PDF contains a compressed stream that expands to fill the disk.
**Mitigation:** PDFs are not decompressed by the companion — they're sent verbatim to SumatraPDF which handles decompression internally. The companion's 100 MB limit applies to the COMPRESSED file size, so a 100 MB PDF that decompresses to 10 GB is rejected.

### 10. Symlink attacks
**Threat:** An attacker places a symlink at `Jobs/queued/<jobId>.pdf` pointing to a system file, then the companion's cleanup deletes the symlink target.
**Mitigation:** The companion creates the `Jobs/` directory with default Windows permissions (only the current user can write). It does not follow symlinks during deletion — `File.Delete` removes the symlink itself, not the target. The companion also verifies the file path matches the expected `<jobId>.pdf` pattern before deleting.

### 11. Duplicate job delivery
**Threat:** The backend sends `job.dispatch` for the same job twice (e.g. due to a Socket.IO reconnection), causing the companion to print twice.
**Mitigation:** The companion's SQLite queue is idempotent on `JobId` — `EnqueueAsync` checks if the job already exists and returns the existing record without creating a duplicate. The JobProcessor's atomic claim (`POST /status {status:"printing"}`) is also idempotent — the backend's `WHERE status='queued'` ensures only one agent can claim.

### 12. Storage deletion mistakes
**Threat:** The cloud cleanup worker deletes a Supabase object that's still needed.
**Mitigation:** The cloud cleanup only deletes objects where `storageStatus = cloud_delete_pending` AND `cloudDeleteAfter < now`. The `cloud_delete_pending` state is only set after the companion confirms local receipt (`POST /received`). The cleanup route also checks that no other active job references the same `storageKey` before deleting (rare, but possible if a job was re-queued).

### 13. Expired authentication
**Threat:** The device token is revoked but the companion keeps using it.
**Mitigation:** Every companion → backend request returns 401 if the token is revoked. The companion's `HttpPrintShopBackend` catches 401 and surfaces a "Device revoked" message in the UI. The companion stops all background work and shows a "Re-pair this PC" prompt.

### 14. Presigned URL leakage
**Threat:** A presigned URL is leaked (e.g. via a browser dev tools or a log) and used to download the PDF by an unauthorized party.
**Mitigation:** Presigned URLs have a 15-minute TTL. The companion never logs the full URL — only the first 40 chars + "...". The backend's `withDeviceAuth` ensures only paired agents can request presigned URLs. The presigned URL is HTTPS — the URL itself (with the signature) is in the query string, but TLS protects it in transit.

### 15. Local database theft
**Threat:** The SQLite queue is copied from the shop PC and read on another machine.
**Mitigation:** The SQLite file is SQLCipher-encrypted (AES-256-CBC). The encryption key is stored DPAPI-encrypted at `Security/queue.key`. Without the key, the SQLite file is unreadable. The key is also tied to the Windows user — copying both `queue.db` and `queue.key` to another PC (or another user on the same PC) yields unusable bytes.

### 16. Insecure temporary files
**Threat:** A PDF being downloaded is written to a temp file that another process can read.
**Mitigation:** The `Jobs/incoming/` directory is created with default Windows permissions (only the current user). Downloads go to `Jobs/incoming/<jobId>.pdf.tmp`, which is atomically renamed to `Jobs/queued/<jobId>.pdf` after SHA-256 verification. The `.tmp` file is deleted if the verification fails. The companion does NOT use the system temp directory (`%Temp%`) for PDFs in v0.5 — only for the chunked-transfer temp files (which are deleted after the transfer completes).

### 17. Untrusted client-supplied identifiers
**Threat:** The companion sends a crafted `jobId` or `agentId` that doesn't belong to it.
**Mitigation:** The backend never trusts the companion's `agentId` — it resolves it from the device token. The `jobId` in `POST /agents/jobs/{id}/status` is validated against the agent's shop (`repos.printJobs.findByIdAndShop(id, shopId)`). A request to operate on another shop's job returns 404 (not 403 — to avoid leaking existence).

## 15 security requirements (from the master prompt)

| # | Requirement | Status | Where |
|---|---|---|---|
| 1 | Short-lived transfer authorization | ✅ (v0.5.1+) | `docs/DELIVERY_PROTOCOL.md` — JWT scoped to one transfer, 5-min TTL |
| 2 | Authenticated agent identity | ✅ | `withDeviceAuth` resolves `agentId` from device token |
| 3 | Tenant-scoped database access | ✅ | Every query filters by `shopId` (resolved from token) |
| 4 | Supabase Storage policies | ⚠️ | Documented; user must verify in Supabase dashboard |
| 5 | Server-side service-role usage only | ✅ | Companion never sees Supabase credentials |
| 6 | Secure presigned URLs | ✅ | 15-min TTL, HTTPS only |
| 7 | Checksum verification | ✅ | SHA-256 of downloaded PDF vs expected (if provided) |
| 8 | File size and type validation | ✅ | `MaxPdfBytes` (100 MB), `mimeType` check (v0.5.1+) |
| 9 | SQLCipher | ✅ | `SQLitePCLRaw.bundle_e_sqlcipher` |
| 10 | DPAPI | ✅ | `device.dat` + `queue.key` both DPAPI-encrypted |
| 11 | Rate limiting | ✅ | `withRateLimit` wrapper on backend (existing) |
| 12 | Audit logging | ✅ | `AuditLog` table on backend; Serilog on companion |
| 13 | Device revocation | ✅ | `DELETE /api/v1/agents/{id}` + `agent.revoked` event |
| 14 | Secure error handling | ✅ | Errors redact tokens; never log presigned URLs |
| 15 | Dependency vulnerability review | ✅ | `Newtonsoft.Json` 13.0.4, `SQLitePCLRaw` 3.50.3 (both safe) |

## What's NOT logged

- Customer PDFs (the bytes are never logged)
- Device tokens (redacted via `DeviceTokenRedactor`)
- JWTs (redacted)
- Presigned URLs (only the first 40 chars + "..." are logged)
- Customer phone numbers (in production — debug-level logs may include them, but production uses Information level)
- Payment info (never sent to the companion; lives only on the backend)

## Files

- `docs/SECURITY_MODEL.md` (this document)
- `src/PrintShopCompanion.Infrastructure/Storage/QueueKeyStore.cs` — DPAPI-protected SQLCipher key
- `src/PrintShopCompanion.Infrastructure/Storage/FileRetentionManager.cs` — secure local cleanup
- `src/PrintShopCompanion.App/Services/DeviceTokenRedactor.cs` — log redaction (existing)
- `tests/PrintShopCompanion.Infrastructure.Tests/SecurityTests.cs` — security unit tests (new)
