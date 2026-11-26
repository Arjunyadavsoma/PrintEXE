# Open-Source Repository Audit

Date: 2026-11-26 · Cloned into `research/open-source-printing/` (outside the
production tree; shallow depth 25; histories NOT merged into PrintEXE).

## Inventory

| # | Project | Clone URL | Branch | HEAD @ clone | License | Language/stack | Size | State |
|---|---|---|---|---|---|---|---|---|
| 1 | QZ Tray | github.com/qzind/tray | master | `6796fe4ef` | **LGPL-2.1** (core; deps mixed) | Java/Swing + Jetty + PDFBox | 20 MB | Active (2026-11) |
| 2 | zma-printer | github.com/Encryft/zma-printer | main | `395c035f4` | MIT | TypeScript/Electron + React | 2 MB | Active (2026-08) |
| 3 | PrintBridge | github.com/CenterfireDigital/PrintBridge | main | `6bc26da47` | MIT | Rust/Tauri 1.x + TS SDK | 1 MB | Active (2026-01) |
| 4 | print-it | github.com/huysentruitw/print-it | master | `5df7bffdd` | MIT | C# netcoreapp3.1 + PDFium | 10 MB | Dormant (2022) |
| 5 | PrintHub | github.com/Komortes/PrintHub | main | `bd01bed42` | MIT | C# **net10.0** minimal-API + SQLite | 2 MB | Active (2026-05) |
| 6 | printlatch | github.com/arturict/printlatch | main | `7a5feae1e` | Apache-2.0 | Rust/axum + SQLite | 4 MB | Active (2026-09), v0.1.x |
| 7 | printpapi | github.com/LukiPrince/printpapi | main | `34f45d549` | **Elastic-2.0** (≤v1.4.0 was MIT) | Rust server+agent + Next.js | 16 MB | Active (2026-11), v3.7.0 |
| 8 | myPrinter | github.com/ndak79/myPrinter | — | — | — | — | — | **INACCESSIBLE** (auth required: private/deleted/renamed). Per instructions, NOT substituted. |
| 9 | clawPDF | github.com/clawsoftware/clawPDF | master | `0785746b5` | **AGPL-3** | C# WPF + C++ spooler monitor + Ghostscript | 366 MB | Dormant (2023), PDFCreator fork |
| 10 | TapQueue | github.com/jthy10/TapQueue | main | `f8334b473` | **AGPL-3** | C# ASP.NET + .NET tray/systemd + SQLite | 5 MB | Active (2026-11), v0.19.0 |

## One-line verdicts

- **QZ Tray** — the mature cross-platform reference: dual-port WSS/WS Jetty
  server, per-connection X.509 signed requests, real spooler change
  notifications, full javax.print attribute mapping. Best Windows-integration
  ideas despite being Java.
- **zma-printer** — a small cloud→desktop receipt printer. Valuable mainly as
  a *negative* reference (fixed 3 s reconnect, no heartbeat, no dedup,
  fire-and-forget acks, secret in WS query string).
- **PrintBridge** — clean local WS server skeleton (loopback + origin
  allow-list + optional token), chunked uploads, batch retry semantics; print
  pipeline shells out to SumatraPDF/mspaint and silently drops
  duplex/orientation/paper.
- **print-it** — minimal, excellent PDFium→HDC GDI printing core with
  auto-rotate/aspect-fit math and a real page-range grammar; no queue, no
  status. MIT C#: the closest direct-code reference for our rendering path.
- **PrintHub** — our closest architectural cousin (net10.0, SQLite store,
  BackgroundService worker, minimal API, loopback auth bypass): guarded job
  state machine, startup crash recovery, pause/drain queue, retry-as-new-job
  lineage, orphan document GC.
- **printlatch** — security-first local-first design: hashed one-time pairing
  codes bound to origin, DNS-rebinding defenses, a 20-check PDF validation
  gauntlet (bombs, active content, encryption), atomic SQL job claims,
  `printing→unknown` on restart (at-most-once for paper).
- **printpapi** — PrintNode alternative with the strongest
  cloud-agent-delivery engineering: 25 s long-poll, pre-print fsync journal
  for exactly-once, state-guarded claims, reaper requeue, outbox with
  doubling backoff, DPAPI'd keys, EnumPrintersW level-4 (registry-only)
  discovery.
- **clawPDF** — port-monitor-based virtual printer (C++ in spooler process);
  study-only (AGPL), and its clean-room-relevant techniques (file+manifest
  job handoff, session-0 bridge, XcvData installer idempotency) are noted at
  behavior level only.
- **TapQueue** — IPP-endpoint emulation + held/release + accounting; the
  CAS-transition state machine, per-user release serialization, and
  squat-proof named-pipe trust are the standout patterns. Study-only (AGPL).

## Provenance discipline

- Reference clones live under `research/open-source-printing/` and are never
  compiled into PrintEXE's solution graph.
- No git history sharing; no subtree/vendor imports.
- PrintEXE integration happens on branch `research/opensource-integration`
  (baseline `27e1898`, v0.7.16) as original C# code, technique-level, with
  per-feature source attribution recorded in the evidence matrix and
  THIRD-PARTY-NOTICES.md.
