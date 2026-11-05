# PrintShop Backend Mapping

This document is the explicit integration contract between the PrintShop Companion (this `apps/windows-companion/` project) and the **real** PrintShop SaaS backend at `https://github.com/Arjunyadavsoma/printshop-saas` / production website `https://printshop-saas-three.vercel.app`.

It is derived from the actual `prisma/schema.prisma` on the `master` branch of the repo (inspected 2026-11-05). Each field in the companion's domain model maps to a real Prisma field. Each endpoint the companion needs is listed with: HTTP method, path, authentication, request/response shape, error responses, retry behaviour, idempotency behaviour.

**Confirmed** entries are backed by an actual file in the repo. **Inferred** entries are required by the companion and are reasonable assumptions based on the schema — they MUST be confirmed against the actual route handlers before the companion is released. The `mini-services/print-agent/` directory referenced in the original brief could not be reached via the GitHub raw API at inspection time (rate-limited); its actual API routes are not in this file.

## 1. Real schema (excerpted from `prisma/schema.prisma`)

```prisma
model Shop {
  id                    String  @id @default(uuid())
  name                  String
  slug                  String  @unique
  ownerUserId           String
  upiId                 String
  paymentProvider       String
  paymentCredentials    String
  pricingConfig         String
  address               String
  phone                 String
  timezone              String  @default("Asia/Kolkata")
  isActive              Boolean @default(true)
  subscriptionTier      String  @default("free")
  subscriptionExpiresAt DateTime?
  ...
  agents    PrintAgent[]
  printJobs PrintJob[]
}

model User {
  id           String  @id @default(uuid())
  email        String  @unique
  phone        String
  passwordHash String
  role         String  @default("owner")
  shopId       String?
  ...
}

model PrintAgent {
  id          String    @id @default(uuid())
  shopId      String
  name        String
  tokenHash   String
  lastSeenAt  DateTime?
  printerName String?
  isOnline    Boolean   @default(false)
  version     String?
  os          String?
  createdAt   DateTime  @default(now())
  updatedAt   DateTime  @updatedAt
  shop        Shop      @relation(fields: [shopId], references: [id])
  @@index([shopId, isOnline])
}

model PrintJob {
  id          String    @id @default(uuid())
  shopId      String
  fileId      String
  agentId     String?
  status      String    @default("queued")
  attempts    Int       @default(0)
  lastError   String?
  queuedAt    DateTime  @default(now())
  startedAt   DateTime?
  completedAt DateTime?
  createdAt   DateTime  @default(now())
  updatedAt   DateTime  @updatedAt
  file        PrintFile @relation(fields: [fileId], references: [id])
  shop        Shop      @relation(fields: [shopId], references: [id])
  @@index([shopId, status])
  @@index([agentId])
}

model PrintFile {
  id               String   @id @default(uuid())
  orderId          String
  originalFilename String
  storageKey       String
  mimeType         String
  sizeBytes        Int
  pageCount        Int?
  copies           Int      @default(1)
  colorMode        String   @default("bw")
  pageRange        String?
  paperSize        String   @default("a4")
  duplex           Boolean  @default(false)
  pricePaise       Int      @default(0)
  status           String   @default("pending")
  printedAt        DateTime?
  ...
  order     Order      @relation(fields: [orderId], references: [id])
  printJobs PrintJob[]
}

model Order {
  id               String  @id @default(uuid())
  shopId           String
  customerToken    String
  status           String  @default("awaiting_payment")
  totalAmountPaise Int
  ...
  files            PrintFile[]
}
```

## 2. Domain mapping

| Companion model (C#) | Real Prisma field | Notes |
| --- | --- | --- |
| `PrintJobRecord.JobId` | `PrintJob.id` (UUID) | Stable, used for idempotency. |
| `PrintJobRecord.ShopId` | `PrintJob.shopId` | UUID. |
| `PrintJobRecord.DeviceId` | `PrintJob.agentId` (nullable on the backend) | The companion always sets it; the backend tracks which agent claimed the job. |
| `PrintJobRecord.Copies` | `PrintFile.copies` | Default 1. |
| `PrintJobRecord.PageRange` | `PrintFile.pageRange` (nullable string) | e.g. "1-3,5". |
| `PrintJobRecord.PaperSize` | `PrintFile.paperSize` | Default "a4". |
| `PrintJobRecord.ColorMode` | `PrintFile.colorMode` | **String** "bw" or "color" — not a bool. The companion must NOT convert to bool. |
| `PrintJobRecord.Duplex` | `PrintFile.duplex` | **Boolean** (true = double-sided). |
| `PrintJobRecord.PageCount` | `PrintFile.pageCount` (nullable) | May be null on the backend if page counting hasn't completed. |
| `PrintJobRecord.OriginalFilename` | `PrintFile.originalFilename` | Shown in the UI as the file name. |
| `PrintJobRecord.OrderToken` | `Order.customerToken` | Shown in the UI as the customer order ID. |
| `PrintJobRecord.PdfUrl` | derived from `PrintFile.storageKey` via a signed S3 URL | The backend must mint a short-lived signed URL — see §3.6 below. |
| `PrintJobRecord.Status` (enum) | `PrintJob.status` (string) | Mapping table below. |

### Status mapping

| Companion `JobStatus` | Backend `PrintJob.status` | Direction |
| --- | --- | --- |
| `Pending` | `"queued"` | companion reads from backend |
| `Downloading` | `"downloading"` | companion reports |
| `Rendering` | `"rendering"` | companion reports |
| `Spooled` | `"printing"` | companion reports |
| `Printed` | `"printed"` | companion reports |
| `ReportedPrinted` | `"printed"` | (acknowledgement only) |
| `UncertainSpooled` | `"printing"` (no change) | companion keeps at "printing" until operator resolves; **never auto-flips to "queued"** |
| `OutcomeUnknown` | `"printed"` (or whatever last reported) | heartbeat reconciliation re-fetches the backend's view |
| `Failed` | `"failed"` | companion reports with `lastError` |
| `Cancelled` | `"cancelled"` | companion reports |

## 3. Required API endpoints

> **WARNING**: The exact paths in the table below are INFERRED from the Prisma schema. The actual `app/api/print-agent/*/route.ts` files in the real repo could not be reached during inspection (GitHub API rate-limit). The companion ships with a settings.json default of `Backend:ApiBasePath = /api/companion/v1`, but the real backend may use a different prefix (e.g. `/api/print-agent`). The companion's `BackendOptions.ApiBasePath` is configurable; once the real paths are confirmed, update the default in `src/PrintShopCompanion.App/appsettings.json` and `src/PrintShopCompanion.Shared/Constants/CompanionProtocol.cs`.

### 3.1 Pairing ticket — mint (website → backend) — INFERRED

```
POST /api/print-agent/pairing/begin
Auth: shopkeeper session (NextAuth)
Request:  { shopId }
Response: { pairingTicket, expiresAt, magicLink }
```

The companion never calls this — the website does, after the shopkeeper signs in. The website then renders the magic-link button.

### 3.2 Pairing ticket — exchange (companion → backend) — INFERRED

```
POST /api/print-agent/register
Auth: pairingTicket (single-use, short-lived)
Request:
{
  pairingTicket: "abc…",
  deviceId:     "5a7f…",         // UUID minted by the companion on first launch
  hostname:     "SHOP-PC",
  os:           "Microsoft Windows NT 10.0.22631.0",
  version:      "0.1.0",
  name:         "SHOP-PC"        // human-friendly; defaults to hostname
}
Response 200:
{
  agentId:      "5a7f…",
  shopId:       "...",
  shopName:     "Demo Print Shop",
  deviceToken:  "long-lived-…",   // backend stores SHA-256 of this in PrintAgent.tokenHash
  issuedAt:     "2026-11-05T08:00:00Z",
  expiresAt:    "2027-11-05T08:00:00Z",
  registeredAt: "2026-11-05T08:00:00Z",
  lastSeenAt:   "2026-11-05T08:00:00Z"
}
Response 400: { error: "invalid_or_expired_ticket" }
Response 409: { error: "ticket_already_used" }
```

**Idempotency:** The pairing ticket is single-use; a second request with the same ticket must 409. The companion mints its own `deviceId` (UUID v4) on first launch and persists it to `%AppData%\PrintShop\Companion\device-id.txt`.

**Retry:** No automatic retry. UI shows a "Pairing failed, please try again" message.

### 3.3 Heartbeat (companion → backend) — INFERRED

```
POST /api/print-agent/heartbeat
Auth: deviceToken
Headers:
  X-PrintShop-Device-Token: <long-lived token>
  X-PrintShop-Device-Id:    <agentId>
  X-PrintShop-Companion-Version: 0.1.0
Request:  { printerName?: string, isOnline?: bool }
Response 200:
{
  serverTime:   "2026-11-05T08:01:00Z",
  tokenValid:   true,
  recommendedBackendVersion: "0.1.0"
}
Response 401: device token invalid or revoked
```

**Side effects:** Backend sets `PrintAgent.lastSeenAt = serverTime` and `PrintAgent.isOnline = true`. Backend MAY also accept `printerName` and persist it to `PrintAgent.printerName`.

**Retry:** Exponential backoff 5s, 15s, 45s, 2m, 5m (capped). On `401`, the companion stops heartbeating and shows "Revoke this PC" in the UI.

### 3.4 Job claim (companion → backend) — INFERRED

```
POST /api/print-agent/jobs/{jobId}/claim
Auth: deviceToken
Request:
{
  agentId: "5a7f…",
  claimedAt: "2026-11-05T08:02:00Z"
}
Response 200:
{
  job: PrintJobDto,   // includes fileId, file metadata, signed PDF URL
  accepted: true
}
Response 409:
{
  error: "already_claimed",
  currentAgentId: "..."
}
```

**Idempotency:** The backend must atomically claim the job (Postgres `UPDATE ... WHERE agent_id IS NULL`). A second claim attempt for the same job by the same agent must return 200 with the existing assignment; a claim by a different agent must return 409.

The companion calls this **once per job** after the job has been delivered via Socket.IO or HTTP poll, and **before** downloading the PDF. The companion's local SQLite queue is the source of truth for in-flight state; the backend's `agentId` is the source of truth for "who is responsible".

### 3.5 Job status report (companion → backend) — INFERRED

```
POST /api/print-agent/jobs/{jobId}/status
Auth: deviceToken
Request:
{
  status:        "printed" | "downloading" | "rendering" | "printing" | "failed" | "cancelled",
  printedAt?:    "2026-11-05T08:05:23Z",
  failedAt?:     "2026-11-05T08:05:23Z",
  errorMessage?: "spooler rejected: out of paper",
  attempts:      3,
  spoolerJobId?: 1234,
  pdfSha256?:   "deadbeef…"
}
Response 200: { accepted: true }
Response 409: { error: "status_conflict", currentStatus: "printed" }
```

**Idempotency:** Idempotent on the backend per `(jobId, status)`. Reporting `printed` twice is a no-op. Reporting `failed` after `printed` must 409 (the backend should refuse to move backwards from a terminal state).

**UncertainSpooled handling:** When the companion cannot confirm whether the paper came out, it reports `status: "printing"` (unchanged) and does NOT report `printed`. The job stays in the backend's queue with the agent's `lastSeenAt` updating. Only an operator-driven resolution in the companion triggers a follow-up `printed` report.

### 3.6 PDF download (companion → backend / S3) — INFERRED

```
GET {signedUrl from PrintJobDto.pdfUrl}
Auth: signed S3 URL (no device token needed)
Response 200: application/pdf
Response 403: signed URL expired
```

The backend mints a short-lived (5 minute) presigned S3 URL from `PrintFile.storageKey` using `@aws-sdk/s3-request-presigner` (already in the real repo's `package.json`). The companion verifies the SHA-256 against `PrintFile.storageKey`-derived hash if the backend includes it.

### 3.7 Job list / poll (companion → backend) — INFERRED

```
GET /api/print-agent/jobs?status=queued
Auth: deviceToken
Response 200: [ PrintJobDto, ... ]
```

Used by the HTTP-poll fallback (every 2 seconds) when Socket.IO is unavailable.

### 3.8 Socket.IO job delivery (backend → companion) — INFERRED

```
Backend emits: 'printJob' { job: PrintJobDto }
Companion emits: 'ack' { jobId, agentId, acceptedAt }
```

The backend's Socket.IO server path is unknown. The companion's `BackendOptions.HubPath` defaults to `/companion-hub`; once the real path is confirmed, update the default.

### 3.9 Device revocation (website → backend) — INFERRED

```
POST /api/print-agent/{agentId}/revoke
Auth: shopkeeper session (NextAuth)
Request:  { reason?: string }
Response 200: { revoked: true }
```

Side effect: backend sets `PrintAgent.isOnline = false` and rotates the token (so the old `tokenHash` no longer matches). The companion's next heartbeat gets a 401, surfaces "Revoke this PC" in the UI, and clears its local `device.dat`.

## 4. Required backend changes

The following routes do not appear to exist in the real PrintShop repo as of inspection time and **must be added** before the companion can pair against the production backend:

1. `POST /api/print-agent/pairing/begin` — mint a one-time pairing ticket, bound to the authenticated shopkeeper's `shopId`. Ticket is signed, expires in 10 minutes, single-use.
2. `POST /api/print-agent/register` — exchange the ticket for a long-lived device token. Persists a new `PrintAgent` row with `tokenHash = sha256(deviceToken)`. Returns the device token (one-time view — never returned again).
3. `POST /api/print-agent/heartbeat` — update `PrintAgent.lastSeenAt`, accept `printerName` and `isOnline` updates. Return whether the device token is still valid.
4. `GET /api/print-agent/jobs` — list queued jobs for this agent's shop, including signed S3 URLs for each `PrintFile`.
5. `POST /api/print-agent/jobs/{jobId}/claim` — atomically assign the job to the agent (`UPDATE ... WHERE agent_id IS NULL`).
6. `POST /api/print-agent/jobs/{jobId}/status` — update `PrintJob.status`, `PrintJob.attempts`, `PrintJob.lastError`, `PrintJob.startedAt`, `PrintJob.completedAt`.
7. `POST /api/print-agent/{agentId}/revoke` — invalidate the device token (rotate `tokenHash`).
8. Socket.IO server endpoint that emits `printJob` events to authenticated agents.

A Prisma migration is needed for the following (optional but recommended):

- Add `PrintAgent.lastSeenAt` index: `@@index([shopId, lastSeenAt])` (the schema currently indexes `[shopId, isOnline]`, which is fine).
- Consider adding `PrintAgent.tokenRotatedAt` to support token rotation policies.
- Consider adding `PrintJob.reportedAt` to track when the agent last reported (separate from `updatedAt`).

## 5. Existing print-agent compatibility

The companion reuses the real repo's `PrintAgent` and `PrintJob` models verbatim. The companion does NOT modify any existing rows; it only inserts new `PrintAgent` rows on pairing and updates `PrintJob.status` / `PrintJob.agentId` / `PrintJob.lastError` during processing. The existing Bun/TypeScript print-agent CLI can keep running against the same backend; both clients will see the same set of queued jobs, but each job is claimed atomically by exactly one agent.

The companion DOES NOT change any customer-facing endpoints. Customer order placement, payment, and file upload are untouched.

## 6. What to verify before release

Before the companion is released:

- [ ] The actual `app/api/print-agent/*/route.ts` files exist in the real repo (or are added).
- [ ] The companion's `BackendOptions.BaseUrl` defaults to `https://printshop-saas-three.vercel.app` (or the backend's API origin).
- [ ] The companion's `BackendOptions.ApiBasePath` matches the real prefix (likely `/api/print-agent` based on schema naming, not `/api/companion/v1`).
- [ ] The companion's `BackendOptions.HubPath` matches the real Socket.IO path.
- [ ] A real `PrintAgent` row is created on pairing and visible in the shop's printer page.
- [ ] A real `PrintJob` row's `status` transitions through `queued → downloading → printing → printed` as the companion processes it.
- [ ] Revoking the device from the website causes the companion's next heartbeat to return 401.
- [ ] Customer order end-to-end: customer uploads a PDF on the website → companion receives the job → physical printer prints → website shows "Printed".

If any of these fail, the companion's `BackendOptions` defaults need to be adjusted in `src/PrintShopCompanion.App/appsettings.json` and `src/PrintShopCompanion.Shared/Constants/CompanionProtocol.cs`.
