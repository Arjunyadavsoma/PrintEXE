# Implementation Roadmap

## Dependency-Aware Phase Sequence

Each phase is independently testable. No phase begins while critical correctness issues remain in a previous phase.

### Phase 1 — Admin Authentication Migration (P0)
**Repo:** printshop-saas  
**Depends on:** Nothing  
**Blocks:** Phase 2 (subscription enforcement needs admin to manage), Android admin app

1. Add `role` values `"admin"` and `"staff"` to User model (additive, no migration needed — existing users default to `"owner"`)
2. Create `/api/v1/admin/login` route (email + password → JWT with admin role)
3. Update `withAuth` wrapper to accept `"admin"` role
4. Migrate all `/api/v1/admin/*` routes from env-var auth to JWT auth
5. Deprecate `ADMIN_ACCESS_KEY` env var (keep as fallback during transition)
6. Add audit log entries for admin login

**Acceptance:** Admin can log in via API, receive JWT, and access admin routes. Old env-var auth is deprecated but still works.

### Phase 2 — Subscription Entitlement Enforcement (P0)
**Repo:** printshop-saas  
**Depends on:** Phase 1 (admin needs to manage subscriptions)  

1. Add subscription status check to `withAuth` wrapper for shopkeeper routes
2. If subscription expired → return 402 Payment Required with renewal instructions
3. Add admin route: `POST /api/v1/admin/subscriptions` (assign/modify subscription)
4. Add admin route: `POST /api/v1/admin/founding-shops` (mark shop as founding member)
5. Add `isFoundingShop` boolean + `freeAccessGrantedUntil` DateTime to Shop model (additive)
6. Create admin UI for subscription management

**Acceptance:** Expired shops get 402 on shopkeeper routes. Admin can assign subscriptions via API.

### Phase 3 — PrintEXE Windows Runtime Verification (P0)
**Repo:** PrintEXE  
**Depends on:** Nothing  

1. Manual test: install v0.5.5 on clean Windows
2. Manual test: verify SQLCipher encryption (sqlite3 CLI fails to open queue.db)
3. Manual test: upgrade from v0.4.7 (if available) — verify migration preserves jobs
4. Manual test: pair with real shop, receive print job, print on real printer
5. Manual test: verify close/reopen lifecycle
6. Manual test: verify auto-update from v0.5.4 → v0.5.5
7. Manual test: verify FileRetentionManager deletes PDFs after 60 min

**Acceptance:** All manual tests pass on real Windows with real printer.

### Phase 4 — PrintEXE UI Completion (P1)
**Repo:** PrintEXE  
**Depends on:** Phase 3 (verify existing functionality works before adding new UI)

1. Implement DeliveryView + DeliveryViewModel (delivery status, download progress, checksum, receipt)
2. Add SettingsView retention controls (local retention, max storage, concurrent downloads)
3. Add connection diagnostics (ping backend, Socket.IO test, round-trip time)
4. Test all new UI elements at 100/125/150/200% DPI scaling

**Acceptance:** All new UI controls work, no clipped content, no UI-thread blocking.

### Phase 5 — Customer Account System (P1)
**Repo:** printshop-saas  
**Depends on:** Phase 2 (subscription enforcement must work first)

1. Add consumer authentication (register/login with phone + OTP or email + password)
2. Link Customer model to User model (customerId → userId)
3. Add `/api/v1/customer/*` routes (profile, order history, saved shops)
4. Add consumer-facing UI (account page, order history, saved shops)

**Acceptance:** Consumers can create accounts, view order history, track orders.

### Phase 6 — Notification System (P1)
**Repo:** printshop-saas  
**Depends on:** Phase 5 (customer accounts needed for notifications)

1. Wire up email adapter (currently stub `StubEmailAdapter`)
2. Wire up SMS adapter (currently stub `StubSmsAdapter`)
3. Add push notification infrastructure (FCM for Android, APNs for iOS)
4. Add NotificationLog model for tracking
5. Add notification triggers: order paid, order ready, order collected, subscription expiring

**Acceptance:** Customers receive notifications when order status changes.

### Phase 7 — Financial Reporting Fix (P1)
**Repo:** printshop-saas  
**Depends on:** Nothing  

1. Separate shop order revenue from PrintShop subscription revenue in admin analytics
2. Add refund tracking (currently no refund flow exists — only payment + cancel)
3. Add "net subscription revenue" metric (subscription revenue minus refunds)
4. Ensure payment webhook does NOT count as bank settlement

**Acceptance:** Admin analytics show 7 distinct financial metrics as specified.

### Phase 8 — Cron Cleanup Fix (P1)
**Repo:** printshop-saas  
**Depends on:** Nothing  

1. Create GitHub Actions workflow that hits `/api/v1/cron/cleanup` every 5 minutes
2. Or deploy to Render/Railway as a persistent worker
3. Verify FileCleanupTask records are being processed

**Acceptance:** Cloud files are deleted 15 minutes after receipt confirmation.

### Phase 9 — Mobile-Responsive Dashboard (P1)
**Repo:** printshop-saas  
**Depends on:** Nothing  

1. Make shopkeeper dashboard responsive (sidebar collapses to hamburger menu on mobile)
2. Make order list responsive (card layout on mobile, table on desktop)
3. Make printer setup page responsive
4. Make analytics page responsive

**Acceptance:** Shopkeeper dashboard is usable on mobile browser.

### Phase 10 — API Contract Stabilization
**Repo:** Both  
**Depends on:** Phases 1-9  

1. Document all API routes with OpenAPI/Swagger
2. Version all breaking changes (v2 prefix for new contracts)
3. Freeze the v1 contract for Android development

**Acceptance:** API contracts are documented, versioned, and frozen for Android development.

### Phase 11 — Android Consumer/Shopkeeper App
**Repo:** NEW  
**Depends on:** Phase 10  

1. Create Android project
2. Implement consumer ordering, tracking, payment
3. Implement shopkeeper dashboard, order management
4. Implement push notifications

**Acceptance:** Android app can create orders, track status, receive notifications.

### Phase 12 — Android Platform-Admin App
**Repo:** NEW  
**Depends on:** Phases 1, 2, 11  

1. Create Android project
2. Implement shop management, subscription administration
3. Implement financial reports, staff management, audit logs
4. Implement founding-shop allocation

**Acceptance:** Admin can manage platform from Android app.
