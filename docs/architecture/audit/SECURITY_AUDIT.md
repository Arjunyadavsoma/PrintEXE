# Security Audit

## Methodology
Both repositories were inspected from source code. No production systems were tested. No destructive tests were performed.

## Findings by Severity

### P0 — Critical

| ID | Finding | Repository | File | Evidence | Risk | Recommendation |
|---|---|---|---|---|---|---|
| SEC-P0-1 | Admin authentication uses hardcoded env vars (ADMIN_EMAIL, ADMIN_PASSWORD, ADMIN_ACCESS_KEY) — not a User model role | printshop-saas | `src/app/api/v1/admin/access-check/route.ts`, `src/app/admin/login/page.tsx` | `ADMIN_ACCESS_KEY` env var with constant-time compare; admin login uses hardcoded email+password from env | Cannot scale admin access, cannot create multiple admins, no audit trail of who performed admin actions | Add `role: "admin"` to User model, create admin login route with JWT, deprecate env-var auth |
| SEC-P0-2 | No subscription entitlement enforcement — Subscription model exists but no middleware checks it before serving shopkeeper routes | printshop-saas | All `/api/v1/shops/me/*` routes | No subscription check in `withAuth` wrapper or any route handler | Expired or inactive shops can still create orders, accept payments, and dispatch print jobs | Add subscription status check in `withAuth` wrapper or as separate middleware |
| SEC-P0-3 | SQLCipher encryption not runtime-verified on real Windows | PrintEXE | `SqliteJobQueue.cs`, `SqlCipherInitializer.cs` | Code calls `Batteries_V2.Init()` and sets `Password=` but no test verifies the database is actually unreadable without the key | Database may appear encrypted in code but use plain SQLite if the native binary doesn't load | Manual Windows test: create DB, open with `sqlite3` CLI, verify "file is not a database" error |

### P1 — High Risk

| ID | Finding | Repository | File | Evidence | Risk | Recommendation |
|---|---|---|---|---|---|---|
| SEC-P1-1 | Socket.IO mini-service uses dev secrets if env vars are missing in non-production | printshop-saas | `mini-services/realtime-service/index.ts` | `const JWT_SECRET = process.env.JWT_ACCESS_SECRET ?? 'dev-access-secret-change-me'` | If deployed without env vars, Socket.IO accepts any token signed with the dev secret | Already mitigated with production fail-fast check, but ensure deployment scripts verify all env vars |
| SEC-P1-2 | Cron cleanup route removed from vercel.json (Hobby plan limit) | printshop-saas | `vercel.json` | No `crons` key in vercel.json; cleanup route exists but is never triggered | Cloud files accumulate in Supabase Storage indefinitely, increasing storage costs | Move to GitHub Actions cron or external cron service |
| SEC-P1-3 | PrintEXE auto-update downloads from GitHub Releases but .bat updater is not signed | PrintEXE | `GitHubUpdateService.cs` | SHA-256 verification is implemented but the .bat file itself is not cryptographically signed | A malicious actor with write access to the app directory could replace the .bat before it runs | Recommend using the signed MSI installer path for production deployments |
| SEC-P1-4 | Dev routes (seed, mark-paid, payment simulators) are accessible in production | printshop-saas | `src/app/api/v1/dev/*` | No environment check in route handlers (only `require-dev.ts` utility exists but not applied to all dev routes) | Could be used to manipulate orders or trigger test payments in production | Add environment check to all dev routes: `if (process.env.NODE_ENV !== 'development') return 404` |
| SEC-P1-5 | PrintEXE device token stored in `%AppData%` by DeviceTokenStore (legacy path) | PrintEXE | `DeviceTokenStore.cs` | Constructor calls `layout.MigrateDeviceToken()` but the `ICredentialStore` interface is used by `HttpShopBackend` which may still reference the old path | Token may be stored in two locations, confusing the migration | Verified: DeviceTokenStore now uses `layout.DeviceTokenPath` (new `%LocalAppData%` path) — but ensure the old `%AppData%` file is deleted after migration |

### P2 — Important

| ID | Finding | Repository | Evidence | Risk | Recommendation |
|---|---|---|---|---|---|
| SEC-P2-1 | No CSRF protection on state-changing API routes | printshop-saas | API routes use `Authorization: Bearer` header (not cookies) — CSRF is not applicable for API routes, but the web UI uses Next.js server actions which may be vulnerable | Low risk (Bearer auth mitigates CSRF), but server actions need verification | Verify Next.js server actions include CSRF tokens |
| SEC-P2-2 | No rate limiting on PrintEXE device-token routes | printshop-saas | `withDeviceAuth` does not call `rateLimit` | A compromised device token could hammer the API | Add rate limiting to device-token routes |
| SEC-P2-3 | Payment webhook idempotency relies on `(provider, eventId)` unique constraint | printshop-saas | `PaymentEvent` model `@@unique([provider, eventId])` | If eventId is null (some PSPs don't send one), dedup fails | Add fallback dedup on `(merchantOrderId, eventType)` |
| SEC-P2-4 | PrintEXE logs PDF filenames in download log messages | PrintEXE | `JobProcessor.cs:151` | `LogInformation("Downloaded PDF ({Bytes} bytes, sha256={Sha})", dl.SizeBytes, dl.Sha256)` — filename is in `job.Title` which may be logged elsewhere | Customer filenames could be sensitive | Redact filenames in production logs or use only job IDs |

### P3 — Improvements

| ID | Finding | Repository | Recommendation |
|---|---|---|---|
| SEC-P3-1 | No dependency vulnerability scan in CI | Both | Add `npm audit` (SaaS) + `dotnet list package --vulnerable` (PrintEXE) to CI |
| SEC-P3-2 | No Content-Security-Policy header | printshop-saas | Add CSP header in `vercel.json` headers |
| SEC-P3-3 | PrintEXE SQLite database key is per-installation but not rotatable | PrintEXE | Add key rotation support for future security requirements |
| SEC-P3-4 | No audit log for PrintEXE device token pairing/unpairing | printshop-saas | Add AuditLog entries when agents are registered/revoked |

## Positive Security Findings

| Aspect | Status | Evidence |
|---|---|---|
| Tenant isolation | ✅ Strong | Every query filters by `shopId` resolved from token; `withDeviceAuth` resolves shopId from `PrintAgent.tokenHash` lookup |
| Token redaction in logs | ✅ Implemented | `DeviceTokenRedactor` Serilog enricher in PrintEXE; SaaS logger doesn't log tokens |
| DPAPI credential storage | ✅ Implemented | PrintEXE stores device token + SQLCipher key via `ProtectedData.Protect(..., DataProtectionScope.CurrentUser)` |
| SQLCipher fail-closed | ✅ Implemented | Both `SqliteJobQueue` and `SqlitePendingOpsQueue` throw `InvalidOperationException` if encryption unavailable |
| HTTPS enforcement | ✅ Implemented | Backend on Vercel (HTTPS by default); PrintEXE uses HTTPS for all API calls |
| Payment webhook verification | ✅ Implemented | PhonePe webhook uses username/password auth; Paytm uses callback verification |
| Atomic job claiming | ✅ Implemented | `UPDATE ... WHERE status='queued'` — first-writer-wins; PrintEXE handles `skipped:true` response |
| Device revocation | ✅ Implemented | `DELETE /api/v1/agents/{id}` revokes device; PrintEXE handles 401 → "Device revoked" UI |
| SHA-256 download verification | ✅ Implemented | PrintEXE verifies SHA-256 after download, before atomic rename + receipt acknowledgement |
| Auto-update SHA-256 verification | ✅ Implemented | `GitHubUpdateService` verifies downloaded ZIP against GitHub asset digest |
| Path traversal protection | ✅ Implemented | PrintEXE uses `<jobId>.pdf` (UUID) as filename, never customer-supplied filename |
| File size limits | ✅ Implemented | PrintEXE enforces 100 MB download limit; backend presigned URLs have 15-min TTL |
