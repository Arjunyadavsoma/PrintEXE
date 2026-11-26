# PrintEXE Competitor Research

**Date:** 2026-11-25 · **Method:** clean-room study. Public sources + open-source repositories only. Four parallel research tracks; every claim carries a confidence label:

- **VFC** — verified from code (file read in the repository)
- **VFD** — verified from official documentation (page read)
- **SNIP** — quote returned by search from an official page's indexed text
- **3P** — third-party source, specific and consistent
- **ASSUMPTION** — inference; not verifiable from public material

The purpose of this document is engineering input: what each reference does, how, and which of its techniques PrintEXE should independently re-implement in C#. Nothing here was copied from any reference; see `PRINTEXE_ENGINEERING_DECISIONS.md` §License for the legal analysis.

---

## Reference 1: QZ Tray (qzind/tray) — primary source-code target

| Field | Value |
|---|---|
| Repository | https://github.com/qzind/tray (master, `2.3.1-SNAPSHOT` in `src/qz/common/Constants.java`) |
| License | **LGPL-2.1-only** (`LICENSE.txt`: "Project Source Code … Copyright (c) 2013-2016 QZ Industries, LLC — LGPL-2.1 License"; "LGPL 2.1 Applies ONLY to: qz-tray, jssc") |
| Docs | https://qz.io/ , https://qz.io/licensing |
| Stack | Java 17+ desktop app, Jetty WebSocket server, Apache PDFBox, log4j2, usb4java/hid4java/jSSC for devices; Windows installer built with **NSIS** + jlinked JRE (`build.xml` targets `nsis/pkgbuild/dmg`, `src/qz/build/JLink.java`, `Fetcher.java`) [VFC] |

### Architecture and component map [VFC — package tree]

```
src/qz/App.java                     — entry point: CLI args, single-instance, logging, certs, tray
src/qz/ws/                          — PrintSocketServer (Jetty, TLS), PrintSocketClient (JSON dispatch),
                                      SocketConnection, SingleInstanceChecker, WebsocketPorts,
                                      HttpAboutServlet (diagnostic landing page)
src/qz/auth/                        — Certificate (X.509/PKIX), Request (trust states), CRL
src/qz/common/                      — Constants (all tunables), TrayManager (tray + dialogs), PropertyHelper
src/qz/printer/                     — PrintServiceMatcher (discovery), action/{PrintRaw,PrintPDF,PrintImage,
                                      PrintHTML}, action/windows/Winspool2 (winspool interop spec),
                                      info/NativePrinterMap, status/ (StatusMonitor, WMI status threads)
src/qz/communication/               — UsbIO, SerialIO (jSSC), SocketIO, FileIO, HID
src/qz/installer/                   — WindowsInstaller (firewall, uninstall keys, shortcuts),
                                      certificate/CertificateManager (localhost CA + SSL leaf),
                                      provision/ (provision.json)
src/qz/ui/                          — Swing dialogs: GatewayDialog, SiteManagerDialog, LogDialog
```

### Key verified behaviors

**Startup & lifecycle** [VFC `src/qz/App.java`]: CLI parse → `SingleInstanceChecker` → rolling file logging (`debug.log` in user dir) → `Installer.certGen(false)` (load-or-generate localhost CA/SSL, produces `qz-tray.properties`) → hourly `ExpiryTask` → headless check → `PrintSocketServer.runServer()` → tray (`TrayManager`). Single-instance is a **port probe, not a lock file**: the new instance connects as a WS client and sends `getProgramName`; a running instance answers `QZ Tray` and the newcomer exits (close codes 4441/4442) [VFC `ws/SingleInstanceChecker.java`].

**WebSocket** [VFC `ws/PrintSocketServer.java`, `common/Constants.java`]: Jetty with two connector sets — **secure ports {8181, 8282, 8383, 8484}, insecure {8182, 8283, 8384, 8485}** with a bind-retry failover loop (`WebsocketPorts.nextSecureIndex()`). Widely-repeated "8182/8282" lore swaps the roles. Message cap `Integer.MAX_VALUE`, idle timeout 5 min, origin allow-list filter, HTTP GET serves an about/diagnostics page.

**Browser→desktop security model** [VFC `ws/PrintSocketClient.java`, `auth/Certificate.java`, `auth/Request.java`, `ui/GatewayDialog.java`] — the most important flow, step by step:

1. Browser opens `wss://localhost:8181` using a localhost certificate QZ generated and installed into the OS trust store at install time.
2. The client JS sends `{call:"websocket.setup", certificate:"<PEM>"}`. The desktop parses the X.509 cert (CN/O/fingerprint=SHA-1), attaches it to the connection.
3. Trust evaluation: PKIX chain validation against (a) a built-in QZ root CA embedded in `Certificate.java`, (b) override CAs (`override.crt`), (c) user-saved certificates (Site Manager). Result ∈ TRUSTED / EXPIRED / UNSIGNED / INVALID_CERT / UNKNOWN.
4. Unknown sites trigger the **GatewayDialog** (CN, org, fingerprint, "Verified by QZ Industries" vs "Untrusted website") with allow/block + remember (fingerprint files `allowed`/`blocked` in the user dir). A **fair semaphore** guarantees only one consent dialog at a time, positioned near the browser window.
5. Every dangerous call must carry `timestamp` + `signature`: the desktop canonicalizes exactly `{call, params, timestamp}`, hashes to SHA-256 hex, verifies RSA (`SHA1withRSA` default; 256/512 selectable). Timestamp must be within **±15 min** (`VALID_SIGNING_PERIOD`). Missing/invalid → dialog again.
6. Replies are `{uid, result}` / `{uid, error}` JSON; unknown-format messages close with code 4003.

Trust is therefore **who** (cert chained to a pinned root) + **what** (signed canonical request) + **when** (anti-replay window) + **user consent memory** (fingerprint files).

**Printer discovery** [VFC `printer/PrintServiceMatcher.java`]: `javax.print.PrintServiceLookup` wrapped in `NativePrinterMap` + optional cache; fuzzy matching (exact / begins-with / partial) and default-printer fallback; JSON exposes name, driver, connection, trays, resolution.

**PDF printing** [VFC `printer/action/PrintPDF.java`]: Apache PDFBox `PDDocument`; AcroForm refresh, page ranges (splitter), copies via page duplication, rotation. Vector-first drawing through the Java print pipeline, rasterization forced on macOS (source comment). Pixel pipeline in `PrintPixel.java` with color-model sanitization.

**Raw printing** [VFC `printer/action/PrintRaw.java`]: byte payload printed via `javax.print` `SimpleDoc(DocFlavor.BYTE_ARRAY.AUTOSENSE)`; Windows-specific `Winspool2`/`WindowsRawUtils` for direct winspool access; raster converters for ESC/POS/ZPL/EPL in `raw/converter/`.

**Privileged ops & installer** [VFC `installer/`]: root-CA install into OS trust store per OS (`NativeCertificateInstaller`), firewall rules via `netsh advfirewall`, Edge loopback exemption via `CheckNetIsolation`, Startup shortcut with `--honorautostart`, HKLM uninstall keys. Data dirs: `%APPDATA%\qz` (shared `%PROGRAMDATA%\qz`).

**Logging/errors** [VFC]: log4j2 rolling file; JSON `{uid,error}` replies; per-call worker threads with typed exception mapping to friendly messages; `ping` keep-alive ignored.

**Updates** [VFC `Constants.VERSION_CHECK_URL`]: GitHub Releases check only — **no auto-update pipeline**; manual download.

**Tests** [VFC `test/`]: TestNG suites exist (`auth`, `printer`, `ws`, `installer`, …) — contrary to common assumption, QZ is not test-less.

**Weaknesses observed** (opportunities for PrintEXE): PKIX revocation disabled (`setRevocationEnabled(false)` + TODO in `Certificate.java`); signature verifier hashes to SHA-256 hex *before* `SHA1withRSA` verification (an interoperability quirk to design more cleanly); no auto-update; dialogs are Swing-era UX.

### License implications for PrintEXE

- Independent C# re-implementation of **techniques** (ports, signed-request model, trust dialogs, failover ports): LGPL does not apply — no copying, no linking, no derivation. ✅
- Translating QZ Java source into C# would create a derivative → LGPL obligations (release modifications, relink rights, attribution). ❌ Not done.
- Must not copy: Java source text, the embedded root CA PEM, `qz-tray.js` client structure, icons/branding (trademarks are not licensed by LGPL at all).

---

## Reference 2: PaperCut Mobility Print — native-pipeline discipline (documentation reference)

| Field | Value |
|---|---|
| Docs | https://www.papercut.com/help/manuals/mobility-print/ |
| License | Proprietary (closed source) — **documentation reference only; no code or assets copied** |
| Stack | Server with an in-house **Go IPP stack** (TCP 9163 IPP / 9164 IPPS), mDNS broadcaster (UDP 5353), embedded mini-DNS for DNS-SD (port 53), Ghost Trap sandboxed Ghostscript on Windows [VFD] |

### Key documented behaviors

- **It explicitly does not bypass the native pipeline**: Mobility Print "sits on the print server in front of the existing print services… and uses the existing native print queuing system and drivers installed on the system (**they're not bypassed**)" [VFD how-it-works]. Job conversion "using the **drivers installed on the server**"; "The job is placed in the operating system's print queue (for example, the Windows print queue)" [VFD print-job-delivery].
- **Two rendering paths** [VFD]: mobile clients deliver PDF over IPP/HTTP → server converts PDF→PDL with server-installed drivers (Ghost Trap sandbox for PS→PDF); Windows clients deliver PostScript from the Global Printer Driver → passthrough ("no conversion") or normalize via Ghost Trap.
- **Client anatomy on Windows** [VFD authentication]: "A standard Windows IPP printer is automatically created" from an IPP URL with an **encoded access token**; the server validates the token per job. No custom port monitor; the OS IPP class driver does the client-side work.
- **Discovery is decoupled from delivery** [VFD printer-discovery]: mDNS (single-subnet, packet-loss fragile), DNS-SD with delegated subzone (robust at scale), known-host setup URLs. Delivery is always direct IPP to the server.
- **Capacity & limits** [VFD system-requirements]: 20 simultaneous connections on desktop Windows; per-printer publish toggles with minutes of cache lag; per-job auth impossible on Windows clients (token persists "Indefinitely"); unsupported: label printers/plotters.
- **Diagnostics** [VFD troubleshooting]: symptom-routed decision tree keyed to discovery method; one-shot "collect logs for support"; dashboard shows active discovery mode.
- **Cloud relay** [VFD cloud-print-overview]: WebRTC peer-to-peer, local-first routing, no inbound server ports, E2E encryption.

**Unknowns (proprietary internals):** exact Windows service/port-monitor artifacts, spool-file formats, live printer-status protocol to clients, offline retry policy, WebRTC signaling topology. All marked unknown in our notes; nothing invented.

### What PrintEXE adopts

The **architectural principle**: transport/auth layers are a shim in front of the Windows spooler; all device-specific knowledge stays in installed drivers; never bypass the spooler without a strong technical reason; server-side (or agent-side) rendering with sandboxed converters; token-per-job validation; symptom-routed diagnostics with a one-shot log bundle.

---

## Reference 3: ZMA Printer Agent (Encryft/zma-printer) — communication patterns (open source)

| Field | Value |
|---|---|
| Repository | https://github.com/Encryft/zma-printer (main; 2 commits, 0★; original credited upstream `Khalidd3v/zma-printer`) |
| License | **MIT** (© 2026 ZmaPOS) — pattern reference usable without copyleft; we did not copy code |
| Stack | Electron 33 + TypeScript, only runtime dep `ws` ^8.21.3; electron-builder NSIS/DMG/portable [VFC `package.json`] |

### Key verified behaviors (all VFC from fetched source files)

- **Startup** (`src/main/index.ts`): single-instance lock → settings init → WS/job logs → IPC → window + tray → cloud connection. `window-all-closed` intentionally empty (stay in tray); `before-quit` stops the connection.
- **Config** (`src/main/config/settings.ts`, `defaults.ts`): `settings.json` in userData, atomic save (tmp+rename), corrupt → reset to defaults; defaults hardcode `https://api.zmapos.com` / `wss://api.zmapos.com/ws/printing/`.
- **Connection** (`src/main/cloud/connection.ts`): WS URL + `?secret=<urlencoded>` **secret in query string**; close code 4001 = invalid secret; JSON job accepted in 4 envelope shapes (`job`/`data`/`payload`/top-level `type:"print"`); declared `schema` field ignored.
- **Printing** (`src/main/print/render.ts`, `printer.ts`): serialized promise-chain queue; printer resolution chain (requested → configured → type-default → any → first); per-job hidden offscreen BrowserWindow, fixed **250 ms** wait (no layout-ready event), `webContents.print` silent; copies as a sequential loop; no duplex/collate; **no PDF path, no ESC/POS** — everything is HTML rasterized by Electron.
- **Ack**: REST `POST /api/v1/printing/jobs/{id}/ack/` with `Authorization: Bearer <secret>`, body `{status:"printed"|"failed", error_message, printer_name}`; **no retry** on ack failure.
- **Recovery**: fixed **3000 ms** reconnect timer, no backoff/jitter, **no heartbeat** — liveness depends on the server closing the socket. **No pending-job queue**: reconnect does not re-fetch; **no dedup** — a server redelivery prints twice. README's "auto-reconnect with job queueing" claim is not implemented in code (verified by absence).
- **History** (`src/main/print/jobLog.ts`): local JSON log capped at 50 entries, atomic writes, locally-generated UUID (not the server job id).
- **Security weaknesses found** (each informs a PrintEXE requirement): secret in WS query string (log/proxy leak); plaintext credential on disk (no DPAPI); no HMAC/signature on job messages; no idempotency/dedup; ack without retry; no heartbeat; permissive 4-shape payload guessing; unbounded debug retention of printed (PII) content; silent config wipe on corruption.

### What PrintEXE adopts / must do better

Adopt: separation of `cloud` / `print` / `config` modules; printer resolution chain with typed errors; serial job queue; atomic writes; connection status machine broadcast (`disabled/connecting/connected/disconnected/no_internet`); structured ack `{status, error_message, printer_name}`; local capped history.
Do better (each maps to a ZMA gap): auth via header/ticket — never URL query; DPAPI-encrypted credentials; HMAC/signature validation of job payloads; heartbeat + exponential backoff with jitter; server-side pending-job re-fetch on reconnect; durable queue with exactly-once printing; single validated payload schema; bounded debug-file retention with PII cleanup; never silently wipe settings.

---

## Reference 4: PrintNode — operational model (documentation reference)

| Field | Value |
|---|---|
| Docs | https://www.printnode.com/en/docs (landing, API/curl, installation/windows, FAQ, troubleshooting, webhooks) |
| License | Proprietary (closed-source client) — behavioral/documentation reference only |

### Key documented behaviors

- **Model** [VFD]: app POSTs print jobs to `api.printnode.com/printjobs`; cloud stores the document; the locally installed client (one per machine) detects OS-visible printers and delivers jobs. Client is "event driven software and sockets"; makes outbound connections on ports **443 and 6123** (role of 6123: push channel — inference). The client "only performs prints as a result of an API call" — no folder watching, no virtual driver. Documents deleted after printing (privacy promise) [VFD FAQ/features].
- **Windows installation** [VFD FAQ + 3P]: Inno Setup EXE to `C:\Program Files\PrintNode\`; silent install `PrintNode-4.28.21.exe /VERYSILENT /TASKS=installasservice /SUPPRESSMSGBOXES`; "install as Windows Service" task for multi-user machines — one client instance regardless of sessions (duplicate-print guard). Per-user data dir `~\.printnode` holds machine identity; **copying that folder to another machine causes identity conflicts and duplicate prints** [VFD FAQ]. Service runs under LocalSystem → per-user default printers not visible (documented caveat).
- **Print job lifecycle** [VFD API]: `POST /printjobs` with `printerId`, `contentType ∈ {pdf_uri, pdf_base64, raw_uri, raw_base64}`, `qty` (redelivery count; separate from `options.copies`), options `{bin, collate, color, copies, dpi, duplex(long/short/one-sided), fit_to_page, media, pages("1,3","-5"), paper, rotate}`; **idempotency via `X-Idempotency-Key`** (409 on replay, 24 h retention); **states are an append-only event log** (`new → sent_to_client → done|error`, plus `expired`/`deleted`) with `{state, message, age, clientVersion}` per event and an explicit warning that states "are not fixed"; `sent_to_client` emitted per delivery attempt — "two sent_to_client states" is the documented signature of a duplicate delivery.
- **Registration model** [VFD API]: computer auto-registers on connect (`{id, name, inet, hostname, version, state}`); printers reported with a **rich capabilities dictionary** (`papers` in tenths-of-mm, `bins`, `collate`, `color`, `copies`, `dpis`, `duplex`, `medias`, `nup`, `printrate`); delegated client key endpoint `GET /client/key/{UUID}` → `ck-…` for fleet provisioning; child-account headers for integrators.
- **Webhooks** [VFD]: server→app notifications with HMAC secret + delivery metrics (`receivedEvents/droppedEvents/successfulRequests/failedRequests`), instead of polling.
- **Client config/diagnostics** [VFD FAQ]: `.PrintNode.conf` in home dir; logs under `~\.printnode\printnode\<cfgver>\logs` (service: `systemprofile\...`); "Send to Support" log-upload returning a reference id; tray icon color = connectivity.

**Unknowns (client internals):** socket protocol on 6123, job-download endpoint, client-side offline queue rules, reconnect/backoff algorithm, webhook payload JSON, full installer task list. Nothing invented.

### What PrintEXE adopts

Append-only job **state event vocabulary** with `age`; idempotency keys end-to-end; separate `qty` vs `copies` semantics; rich printer capability reporting; machine auto-registration + "never copy the data folder" guidance; delegated client key for fleet pairing; silent installer flags **documented** (PrintNode failed to — a gap we beat); service-vs-desktop duplicate-print guard; log-bundle support UX.

---

## Cross-reference summary: what each reference is best at

| Capability | Best reference | Technique |
|---|---|---|
| Browser/desktop trust model | QZ Tray | Pinned-root PKIX + signed canonical requests + consent memory |
| Native spooler discipline | PaperCut | Shim in front of the spooler; drivers do the rendering |
| Cloud→agent job transport | ZMA / PrintNode | Persistent socket + REST ack; append-only states |
| Exactly-once printing | PrintNode | Idempotency keys + duplicate-delivery detection |
| Fleet onboarding | PrintNode | Delegated client key endpoint; silent installer flags |
| Fleet-friendly discovery | PaperCut | DNS-SD/known-host; discovery decoupled from delivery |
| Diagnostics UX | QZ + PaperCut | About page on the port; symptom-routed triage + log bundle |
| Test discipline | QZ | TestNG suites alongside the desktop app |

Confidence labels throughout; verified file paths in the QZ/ZMA sections; every PaperCut/PrintNode claim traceable to the cited doc pages. Unverifiable internals are explicitly listed per reference rather than guessed.
