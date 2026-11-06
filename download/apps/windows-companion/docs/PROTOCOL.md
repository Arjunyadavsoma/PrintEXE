# Companion ⇄ Backend Wire Protocol

This document defines the contract between the PrintShop Companion and the PrintShop backend. The mock backend in `src/PrintShopCompanion.Backend/` implements this contract verbatim; the production backend is expected to match it.

## 1. Authentication

| Header                          | Purpose                            | Logged? |
| ------------------------------- | ---------------------------------- | -------- |
| `X-PrintShop-Device-Token`      | Long-lived device token            | **No**   |
| `X-PrintShop-Device-Id`         | GUID identifying the device        | Yes      |
| `X-PrintShop-Companion-Version`  | Companion semver (e.g. `0.1.0`)    | Yes      |

All authenticated endpoints return `401` if the token is missing, malformed, or revoked.

## 2. Pairing (magic link)

### 2.1 The website mints a pairing token

The shop admin visits the website, goes to **Shop → Printers → Add this PC**, and the website calls:

```
POST /api/companion/v1/pairing/begin
```

Returns:

```json
{
  "pairingToken": "a1b2c3…",
  "magicLink": "printshopcompanion://pair?token=a1b2c3&shop=shop_demo_001&expires=1730000000",
  "shopId": "shop_demo_001",
  "shopName": "Demo Print Shop"
}
```

The website renders a "Open PrintShop Companion" button whose `href` is the `magicLink`.

### 2.2 The OS launches the companion with the magic link

Clicking the link:
1. Windows looks up `HKCU\Software\Classes\printshopcompanion\shell\open\command` (registered by the installer).
2. Windows launches `PrintShopCompanion.exe "printshopcompanion://pair?token=…&shop=…&expires=…"`.
3. If the companion is already running, the single-instance guard forwards the URL to the first instance via a named pipe.
4. The companion's `MagicLinkPairingService` validates the scheme, host, presence of `token`, and URL-level `expires`, then calls `POST /pair`.

### 2.3 Pairing exchange

```
POST /api/companion/v1/pair
Content-Type: application/json

{
  "pairingToken": "a1b2c3…",
  "deviceId":     "5a7f…",                       // GUID minted by the companion on first launch
  "hostname":     "SHOP-PC",
  "operatingSystem": "Microsoft Windows NT 10.0.22631.0",
  "appVersion":   "0.1.0"
}
```

Returns `200 OK`:

```json
{
  "shopId":       "shop_demo_001",
  "shopName":     "Demo Print Shop",
  "baseUrl":      "https://app.printshop.local",
  "deviceId":     "5a7f…",
  "deviceToken":  "long-lived-…",
  "refreshToken": "optional-…",
  "issuedAt":     "2026-11-05T08:00:00Z",
  "expiresAt":    "2027-11-05T08:00:00Z",
  "registeredAt": "2026-11-05T08:00:00Z",
  "lastSeenAt":   "2026-11-05T08:00:00Z"
}
```

The companion persists `deviceToken` encrypted with DPAPI (current-user scope) at `%AppData%\PrintShop\Companion\device.dat`.

## 3. Heartbeat

```
POST /api/companion/v1/heartbeat
X-PrintShop-Device-Token: …
X-PrintShop-Device-Id: …

→ 200 OK
{
  "serverTime": "2026-11-05T08:01:00Z",
  "tokenStillValid": true,
  "recommendedBackendVersion": "0.1.0"
}
```

Default interval: 60 s.

## 4. Job delivery

### 4.1 Socket.IO (production)

The companion connects to `<baseUrl>/companion-hub` with `Transport=WebSocket`. On connect it emits:

```
registerDevice { deviceId: "5a7f…" }
```

The backend then calls the companion's `DeliverJob` method with a `PrintJobDto` payload. The companion ACKs the wire event immediately, persists the job to its SQLite queue (idempotent on `jobId`), then emits `AckJob { jobId, deviceId, acceptedAt }`.

### 4.2 HTTP poll (fallback / mock)

```
GET /api/companion/v1/jobs/pending
X-PrintShop-Device-Token: …
X-PrintShop-Device-Id: …

→ 200 OK
[ PrintJobDto, PrintJobDto, … ]
```

Default interval: 2 s. The companion ACKs each returned job:

```
POST /api/companion/v1/jobs/{jobId}/ack
```

The backend will not return an acked job from `/jobs/pending` unless it is explicitly requeued.

## 4.3 Reporting final status

```
POST /api/companion/v1/jobs/{jobId}/status
X-PrintShop-Device-Token: …

{
  "jobId":      "abc123",
  "printedAt":  "2026-11-05T08:05:23Z",   // set on physical-print confirmation
  "failedAt":   null,
  "errorMessage": null,
  "spoolerJobId": 1234,
  "pdfSha256":  "deadbeef…"
}
```

Idempotent on the backend per `(jobId, final-state)`. Setting both `printedAt` and `failedAt` to `null` is interpreted as a cancellation.

## 5. PDF download

```
GET {pdfUrl from PrintJobDto}
X-PrintShop-Device-Token: …
```

Returns `application/pdf`. The companion verifies the SHA-256 against `pdfSha256` from the `PrintJobDto` (if present) and refuses the bytes on mismatch.

## 6. Errors

| Status | Meaning                                | Companion behaviour                       |
| ------ | -------------------------------------- | ---------------------------------------- |
| 401    | Device token invalid / revoked          | Stop processing; surface "Revoke this PC" |
| 404    | Job no longer available                 | Mark Cancelled in queue                   |
| 409    | Conflict (already acked / wrong state)  | Re-fetch pending; reconcile             |
| 5xx    | Transient backend error                 | Exponential backoff; 5, 15, 45, 120, 300 s |

## 7. Test print

```
POST /api/companion/v1/jobs/test
X-PrintShop-Device-Token: …

→ 200 OK
PrintJobDto { jobId: "test-print-…", ... }
```

The companion processes this job through the same queue / processor / printer pipeline as a real job, but the test-print button bypasses the queue and submits directly to the printer service for faster feedback.
