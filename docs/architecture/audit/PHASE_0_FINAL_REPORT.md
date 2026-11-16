# Phase 0 Final Report — PrintShop Ecosystem Audit

**Date:** 2026-11-09
**Auditor:** Principal Software Architect
**Repositories audited:** `Arjunyadavsoma/printshop-saas` (commit `a968266`) + `Arjunyadavsoma/PrintEXE` (commit `199f04d`)
**Audit type:** Evidence-based, read-only. No source code was modified.

---

## A. Current System — What Exists and How It Actually Works

### printshop-saas (Cloud Backend + Website)

**Technology:** Next.js 16.1.1 (App Router), React 19, Prisma 6.11, PostgreSQL (Supabase), Socket.IO 4.8.4, TailwindCSS 4, Zod 4.

**Hosting:** Vercel (region: bom1 — Mumbai). Socket.IO mini-service runs on a separate process (ports 3003/3004) behind a Caddy reverse proxy that routes via `?XTransformPort=3003` query parameter.

**Database:** Supabase PostgreSQL. 15 Prisma models (Shop, User, Order, PrintFile, PrintJob, PrintAgent, Subscription, AuditLog, PaymentAttempt, PaymentEvent, DeviceSyncState, Customer, PrintDeliveryAttempt, FileCleanupTask). 2 migrations (init + add_delivery_and_sync).

**API surface:** 71 API routes across 6 categories:
- Auth (3): login, register, refresh
- Consumer/Shop public (8): shop by slug, orders, QR, checkout, file upload
- Shopkeeper authenticated (15): shops/me/*, orders, agents, analytics, reports, payments
- Print agent device-token auth (7): register, token, heartbeat, jobs/pending, jobs/status, jobs/received, agent delete
- Sync (3): pull, push, status
- Admin (7): access-check, analytics, audit, orders, shops
- Infrastructure (7): health, ready, db-health, metrics, setup-database, storage get/put, cron cleanup
- Dev (4): seed, mark-paid, QR, payment simulators

**Authentication model:**
- JWT (HS256) for web users (owner role) — `withAuth` wrapper
- Device token (HMAC-SHA256 hash lookup) for PrintEXE — `withDeviceAuth` wrapper
- Admin: hardcoded email + password via env vars, gated by `ADMIN_ACCESS_KEY` query param
- No customer authentication system (customers use 6-char token URLs)

**Payment:** PhonePe + Paytm webhook integration. PaymentAttempt + PaymentEvent models for durable webhook mapping. UPI deep-link payment via `upipay` package.

**Realtime:** Socket.IO server on port 3003, HTTP emit relay on 3004. Events: `job.dispatch`, `order.paid`, `order.ready`, `job.printing`, `job.completed`, `job.failed`, `agent.heartbeat`, `delivery.received`. Browser dashboard authenticates with shop-owner JWT.

**Storage:** Supabase Storage (presigned URLs, 15-min TTL for downloads, 60s for print URLs). Also supports R2/S3 via adapter pattern.

**Architecture quality:** Well-structured with a DI container pattern (12 adapter interfaces), repository pattern, service layer (delivery coordinator, durable queue, print service, seed service). Clean separation of concerns.

### PrintEXE (Windows Desktop Companion)

**Technology:** C# .NET 10, WPF, CommunityToolkit.Mvvm 8.4, SQLite + SQLCipher 2.1.11, Serilog, SocketIOClient 3.1.2, H.NotifyIcon.Wpf 2.4.1.

**Solution:** 6 projects (Core, Shared, Infrastructure, App, Worker, Backend-mock). 89 C# files, 18 XAML files, 11,650 lines of C#, 2,102 lines of XAML.

**Authentication:** Paste-token pairing flow. Shopkeeper generates device token on web dashboard, pastes into PrintEXE. Token stored DPAPI-encrypted at `%LocalAppData%\PrintShop\Companion\Security\device.dat`.

**Storage:** SQLCipher-encrypted SQLite at `%LocalAppData%\PrintShop\Companion\Database\queue.db`. Per-installation 32-byte key, DPAPI-protected at `Security\queue.key`. Fail-closed: throws if SQLCipher unavailable.

**State machines:** Three independent states (Delivery, Printing, Storage) — properly separated.

**Auto-update:** GitHub Releases API + SHA-256 verification + backup/rollback via .bat file swap.

**Tests:** 48 tests (41 Infrastructure + 7 Queue) — all pass on Linux + Windows CI.

**CI/CD:** GitHub Actions on `windows-latest` — 21 published releases from v0.1.2 to v0.5.5.

---

## B. Target System — What Must Be Built

### Website (extend existing)
- Consumer ordering: EXISTS (checkout, file upload, payment)
- Shopkeeper dashboard: EXISTS (orders, printers, analytics, reports, settings)
- Shop management: EXISTS (registration, profile, payment credentials)
- Customer management: PARTIAL (Customer model exists with sync fields, but no UI for shopkeeper to view/manage customers)
- Order tracking: EXISTS (status workflow: awaiting_payment → paid → printing → ready → collected)
- Subscription management: PARTIAL (Subscription model exists, but no admin UI for the "first 100 shops" launch cohort selection)
- Platform administration: PARTIAL (admin routes exist for analytics, audit, orders, shops — but no UI for subscription assignment, founding-shop allocation, staff management, notification campaigns)

### Windows (extend existing)
- Shopkeeper operations: EXISTS (dashboard, orders, print queue, printers)
- Device registration: EXISTS (paste-token pairing)
- Offline printing: EXISTS (SQLite queue + polling fallback)
- Secure file storage: EXISTS (SQLCipher + DPAPI)
- Cloud synchronization: EXISTS (SyncEngine + cursor-based pull/push)
- Delivery acknowledgement: EXISTS (POST /agents/jobs/{id}/received)
- **Missing:** DeliveryView UI, SettingsView retention controls, connection diagnostics UI

### Consumer/Shopkeeper Android (NEW — future)
- Requires stable API contracts first
- Consumer: ordering, tracking, payment
- Shopkeeper: dashboard, order management, printer status

### Platform-Admin Android (NEW — future)
- Requires admin authentication model to be stabilized
- Shop management, subscription administration, financial reports, staff management, audit logs

### Shared Platform
- One identity system: PARTIAL (JWT for web, device token for PrintEXE — separate but compatible)
- One tenant model: EXISTS (shopId resolved from token, enforced in every query)
- One database: EXISTS (Supabase PostgreSQL)
- One order lifecycle: EXISTS (Order.status state machine)
- One payment lifecycle: EXISTS (PaymentAttempt + PaymentEvent)
- Subscription engine: PARTIAL (model exists, manual admin assignment needed)
- Notification system: MISSING (no push notification infrastructure)
- Audit system: EXISTS (AuditLog model + middleware)
- Versioned API contract: PARTIAL (v1 prefix, but no formal versioning strategy)

---

## C. Reuse Strategy

### Keep unchanged
- printshop-saas: DI container, adapter pattern, repository layer, API envelope, rate limiting, Prisma schema (extend, don't rewrite)
- PrintEXE: Core models, Infrastructure (queues, storage, printers), App lifecycle, auto-update mechanism

### Extend
- printshop-saas: Add admin subscription management routes, customer management UI, notification infrastructure, Android API contracts
- PrintEXE: Add DeliveryView, SettingsView retention controls, connection diagnostics

### Refactor
- printshop-saas admin auth: Replace hardcoded email+password with a proper admin role in the User model (currently admin auth is via env vars — not scalable)
- printshop-saas Socket.IO: Ensure it's portable (currently depends on Caddy gateway + specific port configuration)

---

## D. Critical Blockers (P0/P1)

### P0 — Critical

| ID | Finding | Repository | Risk |
|---|---|---|---|
| P0-1 | Admin authentication uses hardcoded env vars (ADMIN_EMAIL/ADMIN_PASSWORD) — not a User model role | printshop-saas | Cannot scale admin access for platform-admin Android app |
| P0-2 | No formal subscription entitlement enforcement — `Subscription` model exists but no middleware checks it before serving shopkeeper routes | printshop-saas | Shops on expired subscriptions can still operate |
| P0-3 | SQLCipher encryption not runtime-verified on Windows | PrintEXE | Database may not actually be encrypted |
| P0-4 | No physical printing verification on real Windows printer | PrintEXE | Printing may fail silently |
| P0-5 | Socket.IO mini-service is a separate process not managed by Vercel — deployment fragility | printshop-saas | Realtime may break on Vercel cold starts |

### P1 — High Risk

| ID | Finding | Repository | Risk |
|---|---|---|---|
| P1-1 | Customer model exists but has no consumer-facing UI — customers cannot create accounts | printshop-saas | Android consumer app has no account foundation |
| P1-2 | No notification system (push, email, SMS stubs exist but are not wired) | printshop-saas | Customers get no order status updates |
| P1-3 | Financial reporting mixes shop revenue with subscription revenue — no separation | printshop-saas | Incorrect financial reporting for platform owner |
| P1-4 | Cron cleanup route exists but was removed from vercel.json (Hobby plan limit) | printshop-saas | Cloud file cleanup not running automatically |
| P1-5 | DeliveryView + SettingsView retention UI not implemented | PrintEXE | Shopkeeper cannot see delivery status or configure retention |
| P1-6 | No mobile-responsive shopkeeper dashboard | printshop-saas | Shopkeeper cannot use phone to manage orders |
| P1-7 | v0.4.7 → v0.5.5 migration not tested on real Windows install | PrintEXE | Upgrade may lose user data |

---

## E. Database Plan (no execution)

**Existing models (15):** Shop, User, Order, PrintFile, PrintJob, PrintAgent, Subscription, AuditLog, PaymentAttempt, PaymentEvent, DeviceSyncState, Customer, PrintDeliveryAttempt, FileCleanupTask

**Required additions:**
1. `PlatformAdmin` model (or add `role: "admin"` to User with null shopId) — for platform-admin Android
2. `SubscriptionEntitlement` model (if the "first 100 shops" free-access rule needs per-shop customization)
3. `NotificationLog` model — for tracking push/email/SMS notifications
4. `StaffInvitation` model — for staff management

**Required modifications:**
1. `User.role` — add "admin" and "staff" values (currently only "owner")
2. `Subscription` — add `isFoundingShop` boolean and `freeAccessGrantedUntil` DateTime for the launch cohort
3. `Shop` — add `foundingShopOrder` Int (1-100) for the launch cohort

**Migration order:** Additive only (new tables + nullable columns). No breaking changes to existing tables.

---

## F. API Plan

### Existing contracts to preserve
- All `/api/v1/agents/*` routes (PrintEXE depends on these)
- All `/api/v1/auth/*` routes (web login)
- All `/api/v1/shops/me/*` routes (shopkeeper dashboard)
- All `/api/v1/sync/*` routes (PrintEXE sync)
- All `/api/v1/orders/*` routes (consumer checkout)
- All `/api/v1/payments/*` routes (webhook + UPI verify)

### Contracts to modify
- `/api/v1/admin/*` — replace env-var auth with JWT-based admin auth
- Add subscription enforcement middleware to shopkeeper routes

### Contracts to add
- `/api/v1/admin/subscriptions` — admin manages shop subscriptions
- `/api/v1/admin/founding-shops` — admin selects launch cohort
- `/api/v1/admin/staff` — admin manages platform staff
- `/api/v1/notifications/*` — notification system
- `/api/v1/customer/*` — consumer account management (for Android)

---

## G. PrintEXE Plan

1. **Add DeliveryView** — display delivery status, download progress, checksum verification, receipt status
2. **Add SettingsView retention controls** — local retention period, max local storage, concurrent downloads
3. **Add connection diagnostics** — ping backend, show round-trip time, test Socket.IO connection
4. **Windows runtime verification** — test SQLCipher encryption, v0.4.7 migration, printing, auto-update on real Windows
5. **No breaking changes** to existing API client — all endpoint contracts verified and matching

---

## H. Website Plan

1. **Add customer management UI** — shopkeeper can view/manage Customer records
2. **Add admin subscription management** — admin can assign free access to founding shops
3. **Add admin founding-shop selection** — admin UI for the "first 100" launch cohort
4. **Add admin staff management** — admin can invite/manage platform staff
5. **Add notification infrastructure** — wire up the stub email/SMS adapters
6. **Make shopkeeper dashboard mobile-responsive** — for future Android shopkeeper app
7. **Fix cron cleanup** — move to external cron (GitHub Actions) since Vercel Hobby plan doesn't support cron

---

## I. Android Readiness

### API prerequisites (must be stable before Android development begins)
1. Admin authentication model (JWT with admin role) — currently broken (env vars)
2. Consumer account system (Customer model needs auth integration)
3. Notification system (push notification infrastructure)
4. Subscription entitlement enforcement middleware
5. Mobile-responsive API responses (pagination, cursors — already partially in place)

### Consumer/Shopkeeper Android
- Depends on: consumer auth, order tracking API, shopkeeper dashboard API
- Can reuse: existing `/api/v1/orders/*`, `/api/v1/shops/me/*` routes
- New needed: `/api/v1/customer/*` for account management

### Platform-Admin Android
- Depends on: admin auth model, admin routes
- Can reuse: existing `/api/v1/admin/*` routes (after auth fix)
- New needed: subscription management, founding-shop allocation, staff management routes

---

## J. Implementation Sequence

### Phase 1 — Admin Authentication (P0-1)
Fix admin auth from env vars to User model with admin role. Add admin login route + JWT issuance.

### Phase 2 — Subscription Enforcement (P0-2)
Add middleware that checks Subscription status before serving shopkeeper routes. Add admin UI for subscription management.

### Phase 3 — Founding Shop Allocation (P1)
Add admin UI for selecting the first 100 shops and granting free access durations.

### Phase 4 — PrintEXE Windows Verification (P0-3, P0-4, P1-7)
Manual Windows testing: SQLCipher, migration, printing, auto-update.

### Phase 5 — PrintEXE UI Completion (P1-5)
Implement DeliveryView, SettingsView retention controls, connection diagnostics.

### Phase 6 — Customer Account System (P1-1)
Add consumer authentication, profile management, order history.

### Phase 7 — Notification System (P1-2)
Wire up email/SMS adapters, add push notification infrastructure.

### Phase 8 — Financial Reporting Fix (P1-3)
Separate shop revenue from subscription revenue in admin analytics.

### Phase 9 — Cron Cleanup Fix (P1-4)
Move cloud file cleanup to GitHub Actions cron (Vercel Hobby plan limitation).

### Phase 10 — Mobile-Responsive Dashboard (P1-6)
Make shopkeeper dashboard responsive for future Android app.

### Phase 11 — API Contract Stabilization
Finalize all API contracts, document with OpenAPI/Swagger.

### Phase 12 — Android Development
Begin consumer/shopkeeper Android app, then platform-admin Android app.

---

## K. Risk Assessment

| Risk | Probability | Impact | Mitigation |
|---|---|---|---|
| SQLCipher not actually encrypting on Windows | Medium | High — data exposure | Manual Windows test: open queue.db with sqlite3 CLI |
| v0.4.7 migration loses user data | Low | Critical — data loss | Test on real v0.4.7 install before production release |
| Socket.IO breaks on Vercel cold start | High | High — no realtime | Move to persistent process (Render/Railway) or use Vercel Edge Functions |
| Admin auth via env vars is compromised | Medium | Critical — full platform access | Migrate to User model with admin role ASAP |
| Cron cleanup not running | High | Medium — storage costs | Move to GitHub Actions cron immediately |
| Physical printing fails silently | Medium | High — customer impact | Test on real Windows printer before production |
| Payment webhook replay | Low | High — duplicate payments | PaymentEvent dedup table + idempotency keys already exist |
| Cross-tenant data access | Low | Critical — data breach | Every query filters by shopId from token — verified in audit |
| Vercel lock-in prevents migration | Medium | Medium — hosting dependency | Adapter pattern allows swapping storage/cache/DB providers |

---

## Evidence Labels

| Label | Meaning | Used for |
|---|---|---|
| VERIFIED | Tested in actual target environment | Cross-repo contract matches, test results |
| CI-VERIFIED | Passed in CI but not independently runtime-tested | Windows CI build, Linux test results |
| IMPLEMENTED | Code exists but runtime behavior unverified | FileRetentionManager, Socket.IO, offline sync |
| BLOCKED | External hardware/credentials/approval required | Physical printing, Windows runtime, v0.4.7 upgrade |
| NOT VERIFIED | No sufficient evidence | SQLCipher encryption on Windows, auto-update on real install |

---

## Conclusion

The PrintShop ecosystem has a solid foundation: a well-structured Next.js backend with clean architecture, a mature Windows companion with SQLCipher encryption and state machines, and 48 passing tests. The primary blockers are operational (admin auth model, subscription enforcement, Socket.IO deployment fragility) rather than architectural. The existing codebase can support all target applications (consumer Android, shopkeeper Android, admin Android) with additive changes — no rewrite is needed.

The most critical path is: fix admin auth → add subscription enforcement → verify PrintEXE on real Windows → stabilize API contracts → begin Android development.
