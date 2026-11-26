# PrintEXE — Troubleshooting Guide

**Symptom-routed**, following PaperCut Mobility Print's decision-tree pattern (research doc §Reference 2). The Diagnostics screen (tray → Diagnostics → **Run check**) collects all of the below in one view; **Export diagnostics** produces a zip (diagnostics.json + logs) for support.

## 1. App will not start

| Check | Fix |
|---|---|
| Windows 10/11 x64? | Run the 64-bit build on 64-bit Windows. |
| `.NET Desktop Runtime 10` present (MSI installs it; portable is self-contained) | Install the runtime from the bundle or use the ZIP. |
| Log file `%AppData%\PrintShop\Companion\logs\companion-*.log` — first error line | Common: corrupt `appsettings.Local.json` → a `.corrupt-*` backup is kept and defaults load; fix the JSON or delete it. |
| "Queue database cannot be opened without encryption" | The SQLCipher key (`%LocalAppData%\PrintShop\Companion\Security\queue.key`) is missing/corrupt. If jobs are dispensable: delete `queue.db` **and** `queue.key` (fail-closed design; jobs are re-fetchable while their cloud copies live). |
| Corrupt DB quarantine notice in log | `queue.db.corrupt-<ts>` was preserved next to the DB — attach it to the diagnostics export. |

## 2. Login / pairing fails

| Check | Fix |
|---|---|
| Diagnostics → Cloud reachable? | No: outbound HTTPS/443 blocked, or the backend is down. Test `https://printshop-saas-three.vercel.app` in a browser on the same PC. |
| Token valid? | Expired shop session → sign in again. Revoked device → the dashboard shows the device revoked; re-pair a new device. |
| Pairing ticket rejected (`PAIRING_TICKET_*` codes) | Tickets are single-use and short-lived — generate a fresh one on the dashboard; paste-token is the fallback. |
| Wrong-shop token pasted | The device pairs to the token's shop; if the shop looks wrong, revoke the device and re-pair with the correct shop's token. |

## 3. Jobs never arrive

| Check | Fix |
|---|---|
| Diagnostics → Device registered + token valid | Pairing incomplete — redo pairing. |
| Socket connected? | Log shows `Refreshed JWT — reconnecting` loops → check the Socket.IO URL config (the `?XTransformPort=3003` query is REQUIRED on the production gateway). |
| Poller working? | When the socket is down the HTTP poller fetches `/agents/jobs/pending` every 2 s — if neither path delivers, capture the log segment and the diagnostics export. |
| Job belongs to this shop? | A job whose shop id mismatches the device's shop is rejected by design (log: "Rejected job ... shop mismatch"). Verify the order was placed at THIS shop. |
| Approval state | Orders awaiting payment/proof approval are never dispatched by the backend; the companion refuses `delivery_failed` jobs pending operator review. |

## 4. Jobs stuck / printing problems

| Symptom | Cause / fix |
|---|---|
| Job stuck `Downloading` | Cloud download timed out (>5 min) or SHA-256 mismatch — the job backs off exponentially (5s→10m) and retries up to 5 attempts; check disk space (256 MB gate) and network. |
| Job stuck `Rendering` after inspection failure | Log/PDF-inspection reason: password-protected, corrupt, empty, oversized (>100 MB). The job failed permanently with that reason — re-upload a fixed document. |
| `UncertainSpooled` in the queue | The spooler never confirmed physical output (submission without a job id, or no confirmation within 5 min). **The app will never auto-reprint.** Confirm at the printer, then use Print Queue → "Mark as printed" (or "Reprint" after cancelling the spooler copy if the paper truly never came). |
| `OutcomeUnknown` | The print finished locally but the completion report failed after 3 retries, or the backend kept the job `printing` from a previous session. Resolve from Print Queue (report printed/failed) — the next successful sync pushes the decision. |
| Two copies of the app running | Single-instance mutex blocks the second instance. If you see duplicate prints: check whether the Worker service AND the desktop app are both enabled — disable one (they must not both run). |
| Printed wrong paper / color | The companion passes specs through verbatim and never substitutes silently. Unknown paper names fall back to the printer default **and are flagged**; check the printer's own paper-size names and the order specs in Job details. |
| Printer shows offline | Printers screen shows the Windows queue status; check cables/IP, then Pause+Resume the queue after reconnecting. |

## 5. Diagnostics reference

| Field | Healthy value |
|---|---|
| Cloud reachable | true |
| Token valid / Device registered | true / true |
| Database healthy | true |
| Queue worker | Running (Paused only when you paused it) |
| Free disk | > 256 MB (queue gate) |
| Last successful print | recent timestamp or "never" |
| Issues list | empty |

## 6. Log locations

| What | Where |
|---|---|
| Desktop app log | `%AppData%\PrintShop\Companion\logs\companion-*.log` (14-day rolling) |
| Worker log | same folder pattern via `SystemDiagnostics.CompanionLogDir()`; Windows Event Log (service mode) |
| Diagnostics bundle | Diagnostics screen → Export (zip: `diagnostics.json` + logs) |

## 7. Escalation

Send the exported diagnostics zip (never screenshots of customer documents — the bundle contains no document content) plus the timestamp of the affected job. Job IDs are stable identifiers the support team can trace in the cloud.
