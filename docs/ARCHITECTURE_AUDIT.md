# Architecture Audit — PrintEXE + PrintShop SaaS

## Repository Comparison

| Dimension | PrintEXE (Windows) | PrintShop SaaS (Web) |
| --- | --- | --- |
| **Language** | C# (.NET 10) | TypeScript (Next.js 16) |
| **Framework** | WPF | Next.js App Router |
| **Database** | SQLite (local cache) | PostgreSQL (Prisma ORM) |
| **Auth** | JWT access + refresh tokens | JWT access + refresh tokens (same) |
| **API Base** | `https://printshop-saas-three.vercel.app/api/v1/` | Same (it IS the backend) |
| **Realtime** | Socket.IO client (poll fallback) | Socket.IO server |
| **File Storage** | S3 presigned URLs (download) | S3 + Supabase/R2 (upload + download) |
| **Print** | Windows spooler + SumatraPDF | N/A (sends to PrintEXE) |
| **Offline** | SQLite durable queue | N/A (always online) |

## Authentication (SHARED — already compatible)

Both use the same `POST /api/v1/auth/login` endpoint:
- Request: `{ email, password }`
- Response: `{ accessToken, refreshToken, expiresIn, user: { id, email, role, shopId } }`
- PrintEXE stores both tokens DPAPI-encrypted at `%AppData%\PrintShop\Companion\device.dat`
- The website stores them in localStorage
- Both auto-refresh via `POST /api/v1/auth/refresh` on 401

## Database Schema (from Prisma — the source of truth)

| Model | Key Fields | Notes |
| --- | --- | --- |
| **Shop** | id, name, slug, ownerUserId, upiId, pricingConfig | Tenant root — everything is shop-scoped |
| **User** | id, email, phone, passwordHash, role, shopId | role: owner, staff, admin |
| **Order** | id, shopId, customerToken, status, totalAmountPaise | status: awaiting_payment, paid, printing, ready, collected, cancelled |
| **PrintFile** | id, orderId, originalFilename, storageKey, copies, colorMode, duplex, paperSize, pageRange | The actual document to print |
| **PrintJob** | id, shopId, fileId, agentId, status, attempts, lastError | status: queued, printing, completed, failed |
| **PrintAgent** | id, shopId, name, tokenHash, lastSeenAt, isOnline | The Windows PC (PrintEXE registers as one) |
| **PaymentAttempt** | id, shopId, orderId, provider, merchantOrderId, amountPaise, status | UPI payment tracking |
| **PaymentEvent** | id, attemptId, eventType, signatureValid, processingStatus | Webhook dedup |
| **Subscription** | id, shopId, tier, status, currentPeriodStart, currentPeriodEnd | Free/paid tier |
| **AuditLog** | id, shopId, userId, action, entityType, entityId, metadata | Append-only audit trail |

## What PrintEXE already implements (from PrintShop API)

| Endpoint | PrintEXE Status |
| --- | --- |
| `POST /api/v1/auth/login` | ✅ LoginViewModel |
| `POST /api/v1/auth/refresh` | ✅ HttpShopBackend auto-refresh |
| `GET /api/v1/me` | ✅ HttpShopBackend.GetCurrentUserAsync |
| `GET /api/v1/shops/me` | ✅ HttpShopBackend.GetShopAsync |
| `PATCH /api/v1/shops/me` | ✅ HttpShopBackend.UpdateShopAsync |
| `GET /api/v1/shops/me/orders` | ✅ HttpShopBackend.ListOrdersAsync |
| `GET /api/v1/shops/me/orders/{id}` | ✅ HttpShopBackend.GetOrderAsync |
| `PATCH /api/v1/shops/me/orders/{id}` | ✅ HttpShopBackend.UpdateOrderNotesAsync |
| `POST /api/v1/shops/me/orders/{id}/start` | ✅ HttpShopBackend.StartOrderAsync |
| `POST /api/v1/shops/me/orders/{id}/complete` | ✅ HttpShopBackend.CompleteOrderAsync |
| `POST /api/v1/shops/me/orders/{id}/cancel` | ✅ HttpShopBackend.CancelOrderAsync |
| `GET /api/v1/shops/me/orders/{id}/files/{fileId}/download` | ✅ HttpShopBackend.DownloadFileAsync |
| `GET /api/v1/shops/me/analytics` | ✅ HttpShopBackend.GetAnalyticsAsync |
| `GET /api/v1/shops/me/reports/daily` | ✅ HttpShopBackend.GetDailyReportsAsync |
| `GET /api/v1/shops/me/agents` | ✅ HttpShopBackend.ListDevicesAsync |
| `POST /api/v1/agents/register` | ✅ HttpShopBackend.RegisterDeviceAsync |
| `POST /api/v1/agents/heartbeat` | ✅ HttpPrintShopBackend.HeartbeatAsync |
| `GET /api/v1/agents/jobs/pending` | ✅ HttpPrintShopBackend.FetchPendingJobsAsync |
| `POST /api/v1/agents/jobs/{id}/status` | ✅ HttpPrintShopBackend.ReportJobStatusAsync |

## What PrintEXE does NOT implement yet

| Feature | Backend API | Status |
| --- | --- | --- |
| Customer CRUD | No API exists | Not started — website also lacks this |
| Product/service management | No API exists | Not started |
| Invoice generation | No API exists | Not started |
| Payment recording (manual) | `POST /shops/me/orders/{id}/verify-payment` | Not started |
| Expense tracking | No API exists | Not started |
| Inventory | No API exists | Not started |
| Staff management | No API exists | Not started |
| Subscription management | No API exists (read-only on web) | Not started |
| Audit log viewing | `GET /admin/audit` (admin only) | Not started |
| Offline sync engine | N/A | Not started |
| Bidirectional sync | N/A | Not started |

## Key Finding

The PrintShop SaaS backend does NOT have APIs for customers, products, invoices, expenses, inventory, or staff management. The website has shop pages for dashboard, orders, printer setup, reports, and settings — but no customer CRUD, no product catalog, no invoicing module.

**PrintEXE can only implement what the backend supports.** Inventing modules that don't have backend APIs would create fake data — which the prompt explicitly forbids.

## Current Sync Architecture (Print Jobs Only)

```
Website → POST /orders/{id}/print → PrintJob created in DB
  ↓ Socket.IO 'printJob' event
PrintEXE → receives event → persists to local SQLite queue
  → downloads PDF → submits to Windows spooler
  → POST /agents/jobs/{id}/status { status: 'completed' }
  → backend updates PrintJob + PrintFile status
  → Socket.IO 'job.completed' event → website updates UI
```

This is **one-directional for business data** (website creates orders, PrintEXE prints them) and **bidirectional for print status** (PrintEXE reports back). There is no sync of customers, products, or invoices because those don't exist in the backend.

## Security Assessment

| Area | Status |
| --- | --- |
| Credential storage | ✅ DPAPI encrypted, current-user scope |
| TLS | ✅ HTTPS only (Vercel auto-TLS) |
| Token redaction in logs | ✅ DeviceTokenRedactor enricher |
| Shop isolation | ✅ Every API route uses `repos.shops.getByOwner(principal.userId)` |
| Device token storage | ✅ SHA-256 hash stored in DB, plaintext returned once |
| Local DB encryption | ⚠️ SQLite is NOT encrypted — only the credentials file is DPAPI-encrypted |
| Input validation | ✅ Zod schemas on every backend route |
| Audit logging | ✅ Every mutation writes to AuditLog |

## Recommendation

The biggest gap is NOT sync — it's that the backend doesn't have APIs for the business modules the prompt asks for (customers, products, invoices, expenses, inventory). Before implementing sync, those APIs need to be added to the printshop-saas repo.

For now, the highest-value work is:
1. Encrypt the local SQLite database
2. Add a local cache of orders + analytics (for offline viewing)
3. Add a pending-operations queue for order status changes
4. Auto-sync when connectivity returns
