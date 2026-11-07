# PrintShop Desktop — Feature-to-API Mapping

**Source:** Audit of `https://github.com/Arjunyadavsoma/printshop-saas` (branch `main`, 589 files).
**Date:** 2026-11-07.
**Method:** Every endpoint below was read from the actual route handler source code (fetched via the GitHub API). No invented endpoints, DTOs, or fields.

This document drives the Windows desktop app implementation. Every screen in the app maps to one or more real API endpoints listed here.

---

## 1. Authentication

The shopkeeper signs in with the **same email + password** they use on the website. There is no separate desktop auth.

### 1.1 Login

```
POST /api/v1/auth/login
Content-Type: application/json

Request:
{
  "email": "shop@example.com",
  "password": "..."
}

Response 200 (envelope):
{
  "success": true,
  "data": {
    "accessToken":  "<JWT, ~15 min lifetime>",
    "refreshToken": "<JWT, ~7 day lifetime>",
    "expiresIn":    900,
    "user": {
      "id":     "<uuid>",
      "email":  "shop@example.com",
      "role":   "owner",   // or "admin"
      "shopId": "<uuid>"   // null for admin
    }
  }
}

Response 401 (envelope):
{ "success": false, "error": { "code": "UNAUTHORIZED", "message": "Invalid email or password" } }
```

**Notes from source (`src/app/api/v1/auth/login/route.ts`):**
- Rate limited: 20 attempts per 60s per IP.
- Admin login is via env-var (`ADMIN_EMAIL` + `ADMIN_PASSWORD`) — the desktop app does NOT special-case this; if the user is an admin, `shopId` is null and the shop endpoints will 403.
- The response uses a uniform `ok(data)` envelope. **Every** API response in the codebase uses this envelope — see `src/lib/api/envelope.ts` for the exact shape.
- The user must have a `shopId` (i.e. be a shop owner). A user with no shop gets 401 "No shop associated with this account".

### 1.2 Refresh

```
POST /api/v1/auth/refresh
Content-Type: application/json

Request:
{ "refreshToken": "<refresh JWT>" }

Response 200:
{ "success": true, "data": { "accessToken": "<new JWT>", "refreshToken": "<new JWT>" } }
```

### 1.3 Current user

```
GET /api/v1/me
Authorization: Bearer <accessToken>

Response 200:
{ "success": true, "data": { "id": "...", "email": "...", "role": "owner", "shopId": "..." } }
```

---

## 2. Shop profile

```
GET /api/v1/shops/me
Authorization: Bearer <accessToken>

Response 200 (data field):
{
  "id":                    "<uuid>",
  "name":                  "Demo Print Shop",
  "slug":                  "demo-print-shop",
  "upiId":                 "demo@upi",
  "paymentProvider":       "phonepe",
  "pricingConfig":         "<JSON string>",
  "address":               "...",
  "phone":                 "...",
  "timezone":              "Asia/Kolkata",
  "isActive":              true,
  "subscriptionTier":      "free",
  "subscriptionExpiresAt": null,
  "description":           null,
  "businessHours":         null,
  "logoUrl":               null,
  "upiQrStorageKey":       null,
  "servicesJson":          null,
  "paymentPhone":          null,
  "paymentInstructions":   null,
  "cashOnPickup":          false
}
```

**Patchable** fields (via `PATCH /api/v1/shops/me`):
`name, upiId, pricingConfig, address, phone, description, businessHours, logoUrl, upiQrStorageKey, servicesJson, paymentPhone, paymentInstructions, cashOnPickup, paymentMethods, pamphletEnabled, pamphletPricePaise, pamphletName`.

---

## 3. Orders

### 3.1 List

```
GET /api/v1/shops/me/orders?status=&limit=&offset=
Authorization: Bearer <accessToken>

Query params:
  status  — one of ORDER_STATUSES (see below). Optional.
  limit   — default 50, max 200.
  offset  — default 0.

Response 200 (data field):
[
  { "id":"...", "shopId":"...", "customerToken":"...", "status":"awaiting_payment",
    "totalAmountPaise":15000, "currency":"INR", "paymentProvider":"phonepe",
    "paymentTxnId":null, "paymentUtr":null, "paidAt":null, "customerPhone":null,
    "notes":null, "tokenNumber":"...", "createdAt":"...", "updatedAt":"..." },
  ...
]
+ pagination: { limit, offset, total }
```

### 3.2 Detail (with files)

```
GET /api/v1/shops/me/orders/{id}
Authorization: Bearer <accessToken>

Response 200 (data field):
{
  ...order fields...,
  "files": [
    {
      "id":"...", "orderId":"...", "originalFilename":"doc.pdf",
      "storageKey":"...", "mimeType":"application/pdf", "sizeBytes":12345,
      "pageCount":3, "copies":1, "colorMode":"bw", "pageRange":null,
      "paperSize":"a4", "duplex":false, "pricePaise":1500,
      "status":"pending", "printedAt":null,
      "createdAt":"...", "updatedAt":"..."
    }
  ]
}
```

### 3.3 Order status state machine

From `packages/core/src/domain/order.ts` (the `canTransitionOrder` function used by every order-mutation route). The ORDER_STATUSES are:

```
awaiting_payment → paid → printing → ready → collected
                                ↓
                              cancelled (from any pre-`collected` state)
```

The desktop app's **Order Details** screen exposes these transitions as buttons, only enabled when `canTransitionOrder(currentStatus, targetStatus)` is true.

### 3.4 Order mutations — all shopkeeper-facing

| Endpoint | Transition | When to enable the button |
| --- | --- | --- |
| `POST /api/v1/shops/me/orders/{id}/start` | `paid → printing` | Order is in `paid` |
| `POST /api/v1/shops/me/orders/{id}/print` | `paid → printing` (refuses if no pending files) | Order is in `paid` AND has ≥1 file in `pending`/`queued` |
| `POST /api/v1/shops/me/orders/{id}/ready` | `printing → ready` | Order is in `printing` |
| `POST /api/v1/shops/me/orders/{id}/complete` | `ready → collected` (deletes stored files) | Order is in `ready` |
| `POST /api/v1/shops/me/orders/{id}/cancel` | `* → cancelled` (with refund handling) | Order is in any pre-`collected` state |
| `POST /api/v1/shops/me/orders/{id}/verify-payment` | re-checks payment status with PSP | Order is in `awaiting_payment` |
| `PATCH /api/v1/shops/me/orders/{id}` | updates shopkeeper private `notes` | Always |
| `POST /api/v1/shops/me/orders/{id}/activity` | audit log fetch | Always |

### 3.5 File download (shopkeeper view)

```
GET /api/v1/shops/me/orders/{id}/files/{fileId}/download
Authorization: Bearer <accessToken>

Response 200:
  - Body: the raw PDF bytes (or a 302 redirect to a presigned S3/R2 URL).
  - Content-Type: application/pdf
```

Used by the Order Details screen's "Preview / Download" button.

---

## 4. Analytics + Reports (Dashboard data)

### 4.1 Analytics (the dashboard cards)

```
GET /api/v1/shops/me/analytics
Authorization: Bearer <accessToken>

Response 200 (data field):
{
  "revenue":     { "totalPaise": 150000, "formatted": "₹1,500.00" },
  "orderCount":  { "total": 42, "paid": 35 },
  "byStatus": {
    "awaiting_payment": 4, "paid": 2, "printing": 1, "ready": 30, "collected": 4, "cancelled": 1
  },
  "popular": { "color": 12, "bw": 23, "duplex": 5, "simplex": 30, "a4": 32, "a3": 1, "other": 2 },
  "peakHourUtc": 14,
  "hours": [0,0,0,...,24 numbers...]
}
```

### 4.2 Daily reports

```
GET /api/v1/shops/me/reports/daily?from=&to=
Authorization: Bearer <accessToken>

Query params:
  from  — ISO date (yyyy-mm-dd)
  to    — ISO date (yyyy-mm-dd)

Response 200 (data field):
[
  { "date":"2026-11-07", "orders":12, "revenuePaise":45000, "files":18, "byStatus": {...} },
  ...
]
```

---

## 5. Print agents (devices)

The desktop app acts as BOTH a shopkeeper client (auth via `accessToken`) AND a print agent (auth via `deviceToken`). These are two separate auth flows.

### 5.1 Register a new device (shopkeeper-authed)

```
POST /api/v1/agents/register
Authorization: Bearer <accessToken>
Content-Type: application/json

Request:
{ "name": "Shop PC", "os": "Windows 11", "version": "0.1.2", "printerName": "HP LaserJet" }

Response 200 (data field):
{
  "agentId":     "<uuid>",
  "deviceToken": "<uuid>-<uuid>",   // shown ONCE — only the SHA-256 hash is stored
  "shopId":      "<uuid>",
  "shopName":    "Demo Print Shop",
  "warning":     "Save this token — it is shown only once."
}
```

### 5.2 Heartbeat (device-token-authed)

```
POST /api/v1/agents/heartbeat
X-PrintShop-Device-Token: <deviceToken>

Request (optional body):
{ "printerName": "HP LaserJet", "version": "0.1.2", "os": "Windows 11" }

Response 200:
{ "agentId":"...", "online":true, "ts":"2026-11-07T..." }
```

### 5.3 Pending jobs (device-token-authed)

```
GET /api/v1/agents/jobs/pending
X-PrintShop-Device-Token: <deviceToken>

Response 200 (data field):
{
  "jobs": [
    {
      "jobId":       "<uuid>",
      "fileId":      "<uuid>",
      "orderId":     "<uuid>",
      "filename":    "doc.pdf",
      "downloadUrl": "https://...presigned...",
      "copies":      1,
      "colorMode":   "bw",
      "duplex":      false,
      "paperSize":   "a4",
      "pageRange":   null
    }
  ],
  "agentId": "<uuid>"
}
```

### 5.4 Report job status (device-token-authed)

```
POST /api/v1/agents/jobs/{id}/status
X-PrintShop-Device-Token: <deviceToken>
Content-Type: application/json

Request (one of three transitions):
{ "status":"printing",  "printerName":"HP LaserJet" }
{ "status":"completed" }
{ "status":"failed",    "lastError":"out of paper" }

Response 200:
{ "jobId":"...", "status":"printing", "applied":true }

Response 200 (skip — race):
{ "jobId":"...", "skipped":true, "reason":"job is 'printing' — already claimed by another agent" }
```

**Atomic claim:** `printing` does a conditional `UPDATE ... WHERE id AND status='queued'` — only one agent wins. The desktop app's queue layer relies on this.

### 5.5 List agents (shopkeeper-authed — for the Settings → Devices screen)

```
GET /api/v1/shops/me/agents
Authorization: Bearer <accessToken>

Response 200 (data field):
[
  { "id":"...", "name":"Shop PC", "isOnline":true, "lastSeenAt":"...",
    "printerName":"HP LaserJet", "version":"0.1.2", "os":"Windows 11" }
]
```

---

## 6. Realtime (Socket.IO)

The backend publishes realtime events to a per-shop channel `shopChannel(shopId)`. The desktop app subscribes to its shop's channel for live updates.

| Event | Payload | When |
| --- | --- | --- |
| `agent.heartbeat` | `{ agentId }` | Agent polls for jobs |
| `job.printing` | `{ jobId, fileId }` | Agent claims a job |
| `job.completed` | `{ jobId, fileId }` | Agent reports completion |
| `job.failed` | `{ jobId, fileId, error }` | Agent reports failure |
| `order.ready` | `{ orderId }` | All files in an order are completed |

The desktop app uses these to refresh the Dashboard and Order Details screens without polling.

---

## 7. What the desktop app does NOT have

These exist on the website but are NOT exposed via documented shopkeeper APIs in the audited routes:

- **Customer list / management** — there is no `/api/v1/shops/me/customers` route. Customer info is attached to orders (`customerToken`, `customerPhone`) but there's no customer CRUD. The desktop app will surface customer info inline on the Order Details screen, not as a separate module.
- **Standalone transactions list** — there is no `/api/v1/shops/me/transactions` route. Transactions are derivable from `payments.phonepe.webhook` + `payments.paytm.callback` events, but those are server-to-server. The desktop app shows payment info from the Order record itself (`paymentTxnId`, `paymentUtr`, `paidAt`).
- **Notifications** — no notification API exists. The desktop app surfaces realtime events as transient toast notifications, not a persistent notification center.
- **Reports / analytics beyond `/analytics` and `/reports/daily`** — these are the only two report endpoints. The desktop app's Reports screen uses just these two.

These are NOT in the desktop app's nav rail. We're not building fake screens.

---

## 8. The desktop app's nav rail (mapped to the real API)

| Nav module | Screen | Backed by |
| --- | --- | --- |
| **Overview** | Dashboard cards + recent orders | `GET /shops/me/analytics`, `GET /shops/me/orders?limit=10` |
| **Orders** | Orders list + filters | `GET /shops/me/orders?status=&limit=&offset=` |
| **Order Details** | Single order + files + actions | `GET /shops/me/orders/{id}`, `POST /shops/me/orders/{id}/start\|ready\|complete\|cancel` |
| **Print Queue** | Local queue + uncertain jobs | Local SQLite queue (the print engine's view) |
| **Printers** | Installed printer list | Windows `System.Printing` (no backend call) |
| **Payments** | Payment info per order | Inline in Order Details (no separate API) |
| **Reports** | Daily revenue + analytics charts | `GET /shops/me/reports/daily`, `GET /shops/me/analytics` |
| **Settings** | Shop profile + devices + autostart | `GET/PATCH /shops/me`, `GET /shops/me/agents`, `POST /agents/register` |
| **Account** | Sign-out, device revoke | Local: clear tokens |

That's 9 modules. The website has more (admin views, customer-facing shop page, etc.) but those don't belong on the shopkeeper desktop app.

---

## 9. Auth model summary

The desktop app holds TWO tokens:

1. **`accessToken` (JWT, ~15 min)** — issued by `POST /api/v1/auth/login`. Sent as `Authorization: Bearer <accessToken>` on every shopkeeper API call. Auto-refreshed via `POST /api/v1/auth/refresh` when it expires.
2. **`deviceToken` (opaque UUID, long-lived)** — issued by `POST /api/v1/agents/register` (which itself requires a valid `accessToken`). Sent as `X-PrintShop-Device-Token: <deviceToken>` on every print-agent API call. Stored encrypted with DPAPI at `%AppData%\PrintShop\Companion\device.dat`.

Both are stored in the same DPAPI-encrypted file. On app launch:
1. Restore `accessToken` + `refreshToken` + `deviceToken` from `device.dat`.
2. If `accessToken` is expired but `refreshToken` is valid → `POST /auth/refresh` → get a new `accessToken`.
3. If `refreshToken` is expired → show the login screen.
4. If `deviceToken` exists → start the background print engine (heartbeat + job polling).
5. If no `deviceToken` → after login, prompt the shopkeeper to register this PC as a print agent (`POST /agents/register`).

This is the **complete** auth flow. No magic links for shopkeepers — they sign in with the same email + password they use on the website.
