# Security

## Threat model

| Threat                                              | Mitigation                                                                            |
| --------------------------------------------------- | ------------------------------------------------------------------------------------- |
| Stolen long-lived device token                       | DPAPI (current-user) encryption at rest; revocation endpoint on the backend; heartbeat surfaces `tokenStillValid=false` so the companion can self-quarantine. |
| Replayed print job (printed twice)                   | Queue is idempotent on `JobId`; the processor never reprints a job already in a terminal state. The wire ACK is separate from the queue's `Status`. |
| False-positive print report ("printed" reported but printer didn't actually print) | `ReportJobStatus.printedAt` is only set after the Windows spooler reports `PrintJobStatus.Printed` for the matching spooler job. |
| Replay or MITM of magic-link pairing                  | One-time pairing token (consumed on first use); URL-level `expires` is checked before the API call; backend re-checks on receipt. |
| Local API abuse (a malicious website on the same PC talking to the companion's localhost API) | Local API binds to `127.0.0.1` only; `Origin` header is validated against `BackendOptions.AllowedWebOrigins`; an `X-PrintShop-Local-Auth` header carries a per-shop HMAC shared secret (mock backend uses a static value). |
| Device credentials leaked via logs                   | `DeviceTokenRedactor` enricher strips properties named `*Token`, `*Secret`, `*Password` from log events before serialisation. HTTP logging handler redacts the `X-PrintShop-Device-Token` and `Authorization` headers. |
| Drive-by URL-handler abuse (a website silently invoking `printshopcompanion://`) | User gesture is required to launch a non-`http(s)` scheme from a browser in modern Windows / Edge / Chrome; the companion also validates `Origin` of the URL fragment (handled by the backend). |
| Privilege escalation                                | The MSI is per-user; the worker service install is an opt-in deferred action; daily app runs as a normal user. |
| Crash leaving a job in-flight forever                | `ReplayInFlightAsync` runs at startup and returns any `Downloaded` / `Spooled` jobs back to `Pending`. |
| Spooler ID reuse (Windows reuses a spooler job ID after it has been purged) | The companion only uses the spooler ID for status polling; it never reads it as a proof-of-print by itself. The actual physical-print confirmation comes from `PrintJobStatus.Printed`. |
| Tampering with the cached PDF file on disk           | Each download is verified against the `pdfSha256` from the `PrintJobDto` and refused on mismatch. PDFs are cached in `%TEMP%` per job and are not reused across jobs. |

## What the companion does NOT do

- Does not run any HTTP server reachable from the network. The local API is `127.0.0.1`-only.
- Does not transmit the device token in a URL query string — only in the `X-PrintShop-Device-Token` header.
- Does not log device tokens, refresh tokens, or session secrets. The redactor runs on every log event before output.
- Does not require administrator rights for daily use. The MSI elevation prompt is for the worker service feature only; declining that feature still installs the per-user WPF app.
- Does not bundle the .NET 10 runtime in the first delivery — the MSI declares a WiX Bundle prerequisite so the runtime is installed on demand.

## Open items before release

- Sign the MSI with a real EV code-signing certificate.
- Pin the Socket.IO client to a known-good version (currently `3.0.13`).
- Add a HSM-backed code-signing pipeline in CI.
- Threat-model review with the PrintShop backend team (in particular the local-API secret rotation policy).
