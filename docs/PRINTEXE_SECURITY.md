# PrintEXE — Security Model

**Date:** 2026-11-25 · Threat model: a small print shop's Windows PC running PrintEXE, handling customer documents over the Printelyx cloud. Assets: customer documents (confidential), device credentials (grant print-job authority), shop data (orders, revenue), local queue metadata.

## 1. Identity and credentials

| Control | Implementation |
|---|---|
| Device credential storage | DPAPI (`ProtectedData`, CurrentUser scope) via `DeviceTokenStore`; contract path `%AppData%\PrintEXE\device-token.dat` with 3-tier legacy migration. Compromise requires running as the same Windows user. |
| Shopkeeper session | Access (~15 min) + refresh (~7 d) JWTs in the same DPAPI bundle. |
| Least privilege | The companion never holds private signing keys, never receives shop-owner passwords after login, and holds only its device token (revocable server-side). |
| Token rotation | v1 contract token-rotate endpoint; new token returned once (server stores HMAC verifier only). |
| Revocation | `agent.revoked` / `device.revoked` Socket.IO events → immediate credential clear + UI stop; 401 on any call → re-auth flow; 403-DEVICE_DISABLED is surfaced distinctly from 401. |
| Credential hygiene in logs | `DeviceTokenRedactor` enricher wired into BOTH Serilog pipelines (App + Worker, D-06): any property named `*Token*`, `*Secret*`, `*Password*` is replaced with `***` before serialization. |

## 2. Transport and message trust

- All cloud traffic is HTTPS with certificate validation ON; the only bypass (`Backend:BypassTlsVerification`) is a loud-warning dev-only flag, never default (adopted pattern; warned on both HTTP clients).
- Socket.IO uses the same TLS + JWT (`auth {token}`) handshake; the shop room scoping is server-side.
- **In-vehicle message integrity:** the v1 backend contract does not sign `job.dispatch` payloads. The companion therefore applies layered admission controls instead of pretending a signature exists: (a) transport TLS, (b) JWT-authenticated socket, (c) shop-ownership validation on every received job (`JobOwnershipValidator`, D-12 — a payload naming a foreign shop is rejected), (d) idempotent enqueue (a redelivered `jobId` can never become a second physical print), (e) atomic backend claim before spooling. A payload HMAC (QZ Tray's signed-request model) is the next contract addition we recommend to the backend team; the client-side admission hooks are in place.

## 3. Document confidentiality and integrity

| Control | Implementation |
|---|---|
| At-rest encryption | Queue DB encrypted with SQLCipher, fail-closed: if the key store is missing or the cipher provider is unavailable, startup refuses to run a plain-text DB. Key: 32 random bytes per installation, DPAPI-protected (`queue.key`). |
| Download integrity | SHA-256 verified when the backend supplies a checksum; Content-Length checked; 100 MB cap. |
| Managed inspection before printing (D-04) | Encrypted/password-protected PDFs are REFUSED (`PdfInspectionError.Encrypted`) — the companion never prints what it cannot parse; corrupt/empty/oversized refused with precise reasons. |
| Local retention | PDFs deleted `localDeleteAfter = terminal + LocalRetentionMinutes` (default 60) by `FileRetentionManager`; volume cap (default 1 GB) forces earlier cleanup; terminal jobs purged from the DB after 7 days. |
| Temporary files | Downloads are written to the layout's job cache; the test page lives in `%TEMP%\PrintShop`. Logs and diagnostics never contain document content — diagnostics export includes logs (already redacted), queue counts, and settings names only. |
| Cloud copy lifecycle | The companion's `/received` receipt gates server-side cloud deletion (15-min grace) — the companion itself never deletes cloud objects. |

## 4. Authorization boundaries

- Only devices paired to a shop can fetch that shop's jobs (server-enforced by device-scoped tokens; client-enforced by the ownership validator).
- Shop isolation on every record: `ShopId` stamped at dispatch (D-12) and persisted; job metadata travels with the job.
- Production state ownership: the companion can move a job only along `printing` (atomic claim) / `completed` / `failed` / `needs_review` — it can never mark an order delivered, modify invoices, or alter customer specifications (spec fields are passed through and validated, never rewritten; "never silently modify customer print specifications" — the raw-GDI path surfaces unsupported paper names instead of claiming a substitute succeeded).
- Operator reconciliation is the only path from uncertain states to a re-print — always human, never automatic.

## 5. Update security

GitHub Releases discovery → semver compare → asset download → **SHA-256 verification against the release's digest** → staged `.bat` swap with backup → app restart handoff. Unsigned-installation risk is documented; EV code signing remains a CI-secret task (see INSTALLATION).

## 6. Known residual risks (honest)

1. No payload HMAC on `job.dispatch` (contract limitation; layered controls above). Recommend the backend add per-job signatures (QZ-style) — client admission hooks ready.
2. `TokenStillValid` is not present in heartbeat responses; revocation latency is therefore bounded by the next 401 or socket event, not by the heartbeat itself.
3. Windows Service mode under LocalSystem exposes the DPAPI machine-context storage and misses per-user printers (documented PrintNode caveat); desktop mode is the recommended deployment.
4. SQLCipher key loss renders the queue unreadable by design (fail-closed) — operators recover by quarantining the DB; jobs are re-fetchable from the cloud (documents survive until the receipt/retention windows pass).
