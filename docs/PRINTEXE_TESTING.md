# PrintEXE — Testing Guide

**Date:** 2026-11-26 · What is tested, how to run it, and what still requires real hardware.

## 1. Test projects and coverage

| Project | Tests | Runs where | Covers |
|---|---|---|---|
| `PrintShopCompanion.Queue.Tests` | 33 | **Any OS** (neutral TFM) | Queue idempotency, dequeue ordering, backoff, crash replay (uncertain states preserved), pause/resume semantics, terminal purge, **v0.6 priority/due-time ordering + restart round-trip (new)**, durable command store lifecycle (insert, dedup, claim races, restart recovery, expiry, lock backoff) |
| `PrintShopCompanion.Infrastructure.Tests` | ~204 | **Any OS** (neutral TFM) | Pairing/ticket validation + error messages, contract config, contract fixtures, job-status mapping, processor idempotency, update service (semver, SHA-256, JSON), pending-ops queue, migrations v0.4.7→v0.5, SQLite pragmas, Windows-runtime security (DPAPI tests auto-skip off-Windows), **spooler tri-state tracker (new)**, **shop-ownership validator (new)**, **PDF inspection (new: valid/empty/missing/non-PDF/corrupt/header-scan/preview)**, **log redactor (new)**, **diagnostics aggregation + export (new)** |
| `PrintShopCompanion.Integration.Tests` | 10 | **Any OS** (real HTTP via Kestrel) | The production contract end-to-end against a contract-faithful fake backend: pairing validation (right/wrong token), JWT exchange, pending-jobs mapping, download + SHA-256 refusal, atomic claim, state-machine completion, crash-leftover skip reason, receipt/cleanup, full lifecycle sequence, fail-closed on unauthenticated calls |

New tests added by this branch: 23 (6 tracker + 5 ownership + 7 PDF + 2 redactor (theory-expanded) + 3 diagnostics + …) — the suite grew from 216 to **247 tests**.

## 2. Running

```bash
# Everything that runs without Windows (Linux/macOS CI, sandbox):
dotnet test PrintShopCompanion.sln -c Release -f net10.0

# Full matrix incl. the windows TFM (Windows machine / GitHub Actions):
dotnet test PrintShopCompanion.sln -c Release
```

Windows-only tests (DPAPI paths) self-skip via `OperatingSystem.IsWindows()` guards on other OSes and execute for real on the `windows-latest` CI job.

## 3. CI

`.github/workflows/build-windows.yml` builds the solution, runs the tests on `windows-latest`, publishes the self-contained `win-x64` ZIP, and (release job) publishes GitHub releases with checksums. The multi-TFM change means CI now also exercises the neutral TFM — the same suite that runs on Linux.

## 4. What automated tests deliberately do NOT cover (honest)

1. **Physical printing.** No printer exists in the CI/Linux environment. The spooler submit path (`WindowsPrinterService.SubmitAsync`), SumatraPDF invocation, GDI raster printing, and spooler-job observation are compile-verified on the windows TFM and logically covered by the tracker/processor unit tests — but the first physical test print is a **milestone, not a checkbox**. See `NOT-VERIFIED.md`.
2. **Socket.IO transport against the real backend.** The integration fake covers REST contract semantics; the Socket.IO path is verified against the production SaaS only on Windows with network access (Phase-2 banner's claim stands: contract verified by source inspection; runtime pairing/dispatch is the next live milestone).
3. **Installer UX.** MSI/bundle builds are scripted for CI; silent-install flags are documented for manual verification.
4. **Multi-printer routing** (printer per job type) — not implemented yet (Phase 3+ in HONEST-STATUS).

## 5. Mock printers and controlled tests

The brief's "mock printers" requirement is satisfied by `FakePrinter`/`FakeRenderer` fakes that implement `IPrinterService`/`IPdfRenderer` without touching hardware, plus the tri-state tracker's pure state machine (the honest-outcome logic is fully testable without a spooler). `SpoolerOutcomeTrackerTests` encodes the exact regression the audit found, so the "not found ⇒ printed" bug can never silently return.

## 6. Regression rules for contributors

1. Every bugfix lands with a test that fails without it (house rule since Phase 1).
2. Never "fix" a failing queue test by loosening the uncertain-state invariants — those tests are the product promise.
3. Contract changes belong in `docs/PRINTEXE_API_CONTRACTS.md` + `FakePrintelyxBackend` first, then the client.
