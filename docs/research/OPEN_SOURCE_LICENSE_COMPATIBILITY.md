# Open-Source License Compatibility Audit

Date: 2026-11-26 · Scope: the ten reference repositories and any dependency
their recommended techniques would pull into PrintEXE. PrintEXE is a
proprietary commercial product (Printelyx platform); the safe default is
**no copyleft code enters our tree**.

## License determinations (from the LICENSE files in each clone)

| Repository | License | Evidence | Commercial proprietary use |
|---|---|---|---|
| qz-tray | **LGPL-2.1** (qz-tray + jssc); bundled deps mixed (Apache-2.0 etc.) | `LICENSE.txt` "LGPL 2.1 / Applies ONLY to: qz-tray, jssc" | Code copy: **No** (would trigger LGPL obligations incl. source availability of modifications to LGPL parts). Technique learning: yes. |
| zma-printer | MIT | `LICENSE` "MIT License, Copyright (c) 2026 ZmaPOS" | Code copy: allowed with notice. (We copy nothing — negative reference.) |
| PrintBridge | MIT | `LICENSE` "MIT, (c) 2024 PrintBridge Contributors" | Code copy allowed with notice. |
| print-it | MIT | `LICENSE` "(c) 2019-2020 Wouter Huysentruit" | Code copy allowed with notice. |
| PrintHub | MIT | `LICENSE` "(c) 2026 Komortes" | Code copy allowed with notice. |
| printlatch | Apache-2.0 | `LICENSE` "Apache License Version 2.0" | Code copy allowed with NOTICE + attribution. |
| printpapi | **Elastic-2.0** (+one permission; ≤v1.4.0 was MIT — `docs/licenses/MIT-until-v1.4.0.txt`) | `LICENSE:1-45, 66-76` | Code copy: **No** — ELv2 obligations + managed-service restrictions travel with the code. Standard-protocol techniques (winspool, backoff, journal) re-implemented in our own C# are unrestricted. |
| myPrinter | — | inaccessible | n/a |
| clawPDF | **AGPL-3.0** | `LICENSE` "GNU AFFERO GPL v3" | **No code, ever.** Network-use clause makes even agent-side integration a source-disclosure risk for the whole combined work. Behavior-level study only. |
| TapQueue | **AGPL-3.0** | `LICENSE` | **No code, ever.** Same rationale. |

## What actually enters PrintEXE in this integration (v0.7.17)

**Nothing is copied from any repository.** All integration items are
technique-level adaptations written as original C# against documented
Microsoft APIs (System.Printing, winspool P/Invoke signatures from Microsoft
docs, SQLite via our existing Microsoft.Data.Sqlite.Core + SQLCipher stack).
No new NuGet dependencies are introduced by this work.

For each adapted technique, the engineering source is credited in
`docs/research/OPEN_SOURCE_FEATURE_EVIDENCE_MATRIX.md` and summarized in
`THIRD-PARTY-NOTICES.md` as inspiration/attribution (not derivative code):

| Integrated technique | Inspired by | Their license | Our obligation |
|---|---|---|---|
| Printer status mapping vocabulary | PrintHub `MapPrinterStatus` | MIT | Attribution note (courtesy, not required) |
| Spooler change notification watcher | qz-tray `WmiPrinterStatusThread` | LGPL-2.1 | None — API usage pattern from public Microsoft docs (`FindFirstPrinterChangeNotification`); no code copied |
| Reconnect backoff + jitter | printpapi `run.rs` | Elastic-2.0 | None — exponential backoff with jitter is a published standard technique (AWS architecture blog et al.); no code copied |
| Pre-ack durable outcome intent row | printpapi journal | Elastic-2.0 | None — generic SQLite pattern, original implementation |
| PDF structural checks (magic/trailer/size) | printlatch `pdf_guard` | Apache-2.0 | None — file-format validation is public knowledge (PDF spec §7.5); no code copied |

## Dependency audit for the integration

- **New packages: zero.** All implementation uses: `System.Printing`
  (WindowsDesktop framework), `winspool.drv` P/Invoke (OS), existing
  `Microsoft.Data.Sqlite.Core`/SQLCipher queue, existing Serilog, existing
  xUnit test stack.
- Existing dependency posture is unchanged (v0.7.2's SQLitePCLRaw identity
  discipline, v0.7.7's SQLCipher bundle, transitive-vulnerability overrides
  documented in the csproj comments).

## Clean-room discipline applied to AGPL repositories

- clawPDF and TapQueue were analyzed by a research agent for *behavioral*
  understanding (architecture, patterns, test strategy). The flagged
  high-risk areas (clawmon C++ monitor internals, IPP wire codec, pdfforge
  settings serialization) were explicitly marked "do not study in depth" in
  the agent's brief and its report; none of their code, schemas, GUIDs, or
  wire formats appear in PrintEXE.
- If a future PrintEXE feature ever needs an IPP listener or port monitor,
  the implementation contract is "from RFC 8010/8011 and Microsoft spooler
  documentation", never from those trees.

## Verdict

The v0.7.17 integration is license-clean: proprietary distribution
unaffected, no copyleft obligations triggered, no new dependencies, honest
attribution recorded. Any future change to "copy actual code" from the
MIT/Apache repos must re-open this audit at that commit and add the required
notices.
