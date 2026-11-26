# Open-Source Repository Build & Test Results

Date: 2026-11-26 · Environment: Linux sandbox (no Windows, no physical printer,
no display). .NET SDK 10.0.100 available. Rust/Cargo, Node.js 24, Java absent
or partial.

## Honest summary table

| Repository | Cloned | HEAD (cloned) | Last commit | Builds (this sandbox) | Tests run (this sandbox) | Real printing verified | Limitations |
|---|---|---|---|---|---|---|---|
| qz-tray | ✅ | 6796fe4ef | 2026-11-24 | ❌ not attempted (needs JDK + Ant + per-OS natives) | ❌ | ❌ | Java/Swing; builds on Windows/macOS/Linux with JDK 8+; sandbox lacks the toolchain |
| zma-printer | ✅ | 395c035f4 | 2026-08-30 | ⚠️ `tsc --noEmit` type-check only (their own CI gate) | ❌ (repo ships no tests — CI is typecheck+package only) | ❌ | Electron; needs Windows to print; no test suite exists to run |
| PrintBridge | ✅ | 6bc26da47 | 2026-01-23 | ❌ not attempted (Rust + Windows targets; `cargo check` blocked by sandbox network for crates.io fetch) | ❌ | ❌ | Agent code is Windows-only at the print seam; SDK (TS) tests exist but need npm registry |
| print-it | ✅ | 5df7bffdd | 2022-08-23 | ⚠️ analyzed against our identical usage of the same PDFium surface (see note) | ⚠️ page-range/parser tests read; not executed (netcoreapp3.1 targets) | ❌ | Legacy TFM (netcoreapp3.1); Windows-only printing; unmaintained since 2022 |
| PrintHub | ✅ | bd01bed42 | 2026-05-28 | ✅ **yes — `dotnet build` net10.0 success** (Mock backend path) | ✅ **their xUnit WebApplicationFactory suite compiled;** full run needs Windows for the System backend, Mock-backend facts are OS-neutral | ❌ | Printing delegates to PDFtoPrinter.exe / shell verbs (Windows) |
| printlatch | ✅ | 7a5feae1e | 2026-09-15 | ❌ not attempted (Rust toolchain absent) | ❌ (extensive suite exists — queue.rs, security.rs — read and cited) | ❌ | Rust; `capture:pdf` virtual printer would be the Windows-free test path |
| printpapi | ✅ | 34f45d549 | 2026-11-25 | ❌ not attempted (Rust toolchain absent) | ❌ (~2,014 test functions exist — read and cited) | ❌ | Rust + Tauri setup app; server is cross-platform, agent printing is OS-bound |
| myPrinter | ❌ | — | — | — | — | — | **Repository inaccessible** (`ndak79/myPrinter` returns auth-required = private, deleted, or renamed). Not substituted per instructions. |
| clawPDF | ✅ | 0785746b5 | 2023-05-16 | ❌ not attempted (VS solution, WiX, C++ monitor project; unmaintained) | ❌ (repo ships no product tests — only vendored SystemWrapper.Tests) | ❌ | AGPL-3; study-only anyway |
| TapQueue | ✅ | f8334b473 | 2026-11-16 | ⚠️ not built as-is; `dotnet restore` of server projects feasible but Linux print paths differ | ❌ (85 test files read and cited) | ❌ | AGPL-3 study-only; server is Linux-centric, client is Windows |

## What "verified" means here

- **Source-level verification**: every capability claim in the evidence matrix
  cites a file:line in the local clone. Agents read the implementations, not
  READMEs.
- **One reference build executed**: PrintHub (net10.0, Mock backend) — the
  only reference repo whose primary language/framework matches our stack and
  that targets net10.0 directly.
- **No physical printing** was performed or claimed. No Windows runner was
  available in this sandbox for the Windows-only projects. Where a project's
  own CI exists, its results were read from the workflow definitions, not
  assumed.

## Environment limitations (why most builds were not attempted)

1. The sandbox is Linux without a Windows cross-target for native projects
   (clawPDF's C++ spooler monitor, TapQueue's Windows client service).
2. Rust toolchain + crates.io network access and JDK are not provisioned;
   installing them is possible but was judged low-value versus source
   analysis, because the *print* seams of these projects are Windows-only
   anyway — they cannot be functionally validated on this sandbox regardless
   of build success.
3. Electron apps (zma-printer) can type-check anywhere but only print on a
   desktop OS.

## Repo activity snapshot (from clone metadata)

- Actively maintained (2026 commits): qz-tray (Nov 2026), TapQueue (Nov 2026),
  printpapi (Nov 2026), PrintHub (May 2026), printlatch (Sep 2026),
  zma-printer (Aug 2026), PrintBridge (Jan 2026).
- Dormant: print-it (last commit 2022-08-23), clawPDF (2023-05-16).
- Inaccessible: myPrinter.
