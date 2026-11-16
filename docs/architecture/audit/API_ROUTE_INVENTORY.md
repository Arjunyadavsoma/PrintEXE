# API Route Inventory — printshop-saas

Complete inventory of all 71 API routes, inspected from source code.

## Authentication Wrappers

| Wrapper | File | Auth mechanism | Used by |
|---|---|---|---|
| `withAuth` | `src/lib/api/with-auth.ts` | JWT (HS256) — `Authorization: Bearer <jwt>` | Web shopkeeper routes |
| `withDeviceAuth` | `src/lib/api/with-auth.ts` | Device token — `Authorization: Bearer <deviceToken>` → HMAC-SHA256 hash lookup | PrintEXE routes |
| `withRateLimit` | `src/lib/api/with-auth.ts` | Rate limiting via Upstash Redis or in-memory | Public routes |
| `withHandle` | `src/lib/api/with-auth.ts` | No auth, just timing/logging | Health/metrics |
| Admin access-check | `src/app/api/v1/admin/access-check/route.ts` | `ADMIN_ACCESS_KEY` env var (constant-time compare) | Admin page gate only — NOT real auth |
| Admin login | `src/app/admin/login/page.tsx` | Hardcoded `ADMIN_EMAIL` + `ADMIN_PASSWORD` env vars | Admin dashboard |

## Route Inventory by Category

### Auth (3 routes)
| Method | Path | Auth | Implementation |
|---|---|---|---|
| POST | `/api/v1/auth/login` | None | Email + password → JWT access + refresh |
| POST | `/api/v1/auth/register` | None | Shop registration (creates Shop + User) |
| POST | `/api/v1/auth/refresh` | Refresh token | Exchange refresh token → new access token |

### Consumer/Shop Public (8 routes)
| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/api/v1/shops/[slug]` | None | Shop profile for consumer-facing page |
| GET | `/api/v1/shops/[slug]/orders` | None | Order list for shop (used by QR scan) |
| GET | `/api/v1/shops/[slug]/upi-qr` | None | UPI QR code image |
| POST | `/api/v1/orders/[id]/checkout` | None (customer token in body) | Create order from cart |
| POST | `/api/v1/orders/[id]/files` | None (customer token) | Upload print file |
| GET | `/api/v1/orders/[id]/files/upload-url` | None (customer token) | Get presigned upload URL |
| GET | `/api/v1/orders/[id]/receipt` | None (customer token) | Download receipt |
| POST | `/api/v1/orders/[id]/status` | None (customer token) | Check order status |
| GET | `/api/v1/qr` | None | QR code generation |

### Shopkeeper Authenticated (15 routes)
| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/api/v1/shops/me` | JWT (owner) | Shop profile |
| PATCH | `/api/v1/shops/me` | JWT (owner) | Update shop settings |
| GET | `/api/v1/shops/me/orders` | JWT (owner) | Order list with filters |
| GET | `/api/v1/shops/me/orders/[id]` | JWT (owner) | Order detail |
| POST | `/api/v1/shops/me/orders/[id]/start` | JWT (owner) | Start printing |
| POST | `/api/v1/shops/me/orders/[id]/ready` | JWT (owner) | Mark ready |
| POST | `/api/v1/shops/me/orders/[id]/complete` | JWT (owner) | Mark collected |
| POST | `/api/v1/shops/me/orders/[id]/cancel` | JWT (owner) | Cancel order |
| POST | `/api/v1/shops/me/orders/[id]/verify-payment` | JWT (owner) | Manual payment verify |
| GET | `/api/v1/shops/me/orders/[id]/files/[fileId]/download` | JWT (owner) | Download print file |
| GET | `/api/v1/shops/me/orders/[id]/files/[fileId]/print-url` | JWT (owner) | 60s presigned URL |
| POST | `/api/v1/shops/me/orders/[id]/print` | JWT (owner) | Trigger print |
| GET | `/api/v1/shops/me/orders/[id]/activity` | JWT (owner) | Order activity log |
| GET | `/api/v1/shops/me/analytics` | JWT (owner) | Revenue + order stats |
| GET | `/api/v1/shops/me/reports/daily` | JWT (owner) | Daily report |
| GET | `/api/v1/shops/me/agents` | JWT (owner) | List paired PrintEXE devices |
| GET/POST | `/api/v1/shops/me/payment-credentials` | JWT (owner) | Payment credential management |
| POST | `/api/v1/shops/me/payment-credentials/activate` | JWT (owner) | Activate payment provider |
| GET | `/api/v1/shops/me/qr` | JWT (owner) | Shop QR code |
| GET | `/api/v1/shops/me/sticker` | JWT (owner) | Shop sticker/branding |
| GET | `/api/v1/shops/me/upi-qr` | JWT (owner) | UPI QR for shop |

### Print Agent / Device Token (7 routes)
| Method | Path | Auth | Purpose |
|---|---|---|---|
| POST | `/api/v1/agents/register` | JWT (owner) | Register new PrintEXE device → returns device token |
| POST | `/api/v1/agents/token` | Device token | Exchange device token → JWT for Socket.IO |
| POST | `/api/v1/agents/heartbeat` | Device token | Refresh presence (updates isOnline + lastSeenAt) |
| GET | `/api/v1/agents/jobs/pending` | Device token | Fetch queued jobs (up to 10) with presigned download URLs |
| POST | `/api/v1/agents/jobs/[id]/status` | Device token | Atomic claim / complete / fail job |
| POST | `/api/v1/agents/jobs/[id]/received` | Device token | Durable receipt acknowledgement (gates cloud deletion) |
| DELETE | `/api/v1/agents/[id]` | JWT (owner) | Revoke PrintEXE device |

### Sync (3 routes)
| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/api/v1/sync/pull?cursor=&limit=` | Device token | Incremental pull since cursor (cursor-based, max 500) |
| POST | `/api/v1/sync/push` | Device token | Submit customer changes (upsert/delete, max 100 per request) |
| GET | `/api/v1/sync/status` | Device token | Device sync state (cursor, lastSyncAt, lastPushAt, isOnline) |

### Admin (7 routes)
| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/api/v1/admin/access-check` | ADMIN_ACCESS_KEY env | Gate admin page (NOT real auth) |
| GET | `/api/v1/admin/analytics` | Admin (env vars) | Platform-wide analytics |
| GET | `/api/v1/admin/audit` | Admin (env vars) | Audit log viewer |
| GET | `/api/v1/admin/orders` | Admin (env vars) | All orders across shops |
| GET | `/api/v1/admin/orders/[id]` | Admin (env vars) | Order detail |
| GET | `/api/v1/admin/shops` | Admin (env vars) | All shops |
| PATCH | `/api/v1/admin/shops/[id]` | Admin (env vars) | Update shop |

### Infrastructure (7 routes)
| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/api/v1/health` | None | Liveness probe |
| GET | `/api/v1/ready` | None | Readiness check (DB + storage + cache) |
| GET | `/api/v1/db-health` | None | Database health check |
| GET | `/api/v1/metrics` | None | Application metrics |
| POST | `/api/v1/setup-database` | None (dev only) | Initialize database + create tables |
| GET/POST | `/api/v1/storage/get` | None | Storage adapter proxy (dev) |
| GET/POST | `/api/v1/storage/put` | None | Storage adapter proxy (dev) |
| GET | `/api/v1/cron/cleanup` | CRON_SECRET env | Cloud file cleanup (was in vercel.json, removed due to Hobby plan) |

### Dev (4 routes)
| Method | Path | Auth | Purpose |
|---|---|---|---|
| POST | `/api/v1/dev/seed` | None (dev only) | Seed test data |
| POST | `/api/v1/dev/orders/[id]/mark-paid` | None (dev only) | Mark order as paid |
| GET | `/api/v1/dev/qr` | None (dev only) | QR code test |
| POST | `/api/v1/payments/dev/phonepe/simulate` | None (dev only) | Simulate PhonePe webhook |
| POST | `/api/v1/payments/dev/paytm/simulate` | None (dev only) | Simulate Paytm webhook |

### Payments (5 routes)
| Method | Path | Auth | Purpose |
|---|---|---|---|
| POST | `/api/v1/payments/phonepe/webhook` | PhonePe webhook auth | PhonePe payment webhook |
| POST | `/api/v1/payments/paytm/callback` | Paytm callback auth | Paytm payment callback |
| GET | `/api/v1/payments/upi/verify/[orderId]` | None (customer token) | Verify UPI payment |
| GET | `/api/v1/downloads/native-printer` | None | Download PrintEXE installer |

### Other
| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/api/v1/me` | JWT | Current user profile |
| POST | `/api/v1/devices/register` | JWT | Device registration (legacy) |
| GET | `/api/v1/shops` | None | List all shops (public) |
| GET | `/api/v1/shops/[slug]` | None | Shop by slug |

## Socket.IO Events

### Emitted by backend (server → client)
| Event | Payload | Trigger |
|---|---|---|
| `job.dispatch` | `{jobId, fileId, orderId, filename, downloadUrl, copies, colorMode, duplex, paperSize, pageRange}` | New print job created |
| `order.paid` | `{orderId, amountPaise}` | Payment confirmed |
| `order.ready` | `{orderId}` | All files printed |
| `job.printing` | `{jobId, fileId}` | Agent claimed job |
| `job.completed` | `{jobId, fileId}` | Agent completed job |
| `job.failed` | `{jobId, fileId, error}` | Agent failed job |
| `agent.heartbeat` | `{agentId}` | Agent heartbeat |
| `delivery.received` | `{jobId, agentId, agentName, receivedAt, checksum}` | PrintEXE confirmed receipt |

### Received by backend (client → server)
| Event | Payload | Purpose |
|---|---|---|
| `auth` | `{token: jwt}` | Socket.IO authentication handshake |

## Cross-Repository Contract Verification

| Endpoint | PrintEXE method | Backend route | Contract match |
|---|---|---|---|
| POST /agents/token | `ExchangeDeviceTokenForJwtAsync` | `src/app/api/v1/agents/token/route.ts` | ✅ Match |
| POST /agents/heartbeat | `HeartbeatAsync` | `src/app/api/v1/agents/heartbeat/route.ts` | ✅ Match |
| GET /agents/jobs/pending | `FetchPendingJobsAsync` | `src/app/api/v1/agents/jobs/pending/route.ts` | ✅ Match |
| POST /agents/jobs/{id}/status | `ReportJobStatusAsync` | `src/app/api/v1/agents/jobs/[id]/status/route.ts` | ✅ Match (status: printing/completed/failed) |
| POST /agents/jobs/{id}/received | `MarkJobReceivedAsync` | `src/app/api/v1/agents/jobs/[id]/received/route.ts` | ✅ Match (checksum: pure hex, no prefix) |
| GET /sync/pull | `SyncPullAsync` | `src/app/api/v1/sync/pull/route.ts` | ✅ Match (cursor-based) |
| POST /sync/push | `SyncPushAsync` | `src/app/api/v1/sync/push/route.ts` | ✅ Match (Customer entity only in v1) |
| GET /sync/status | `SyncStatusAsync` | `src/app/api/v1/sync/status/route.ts` | ✅ Match |
