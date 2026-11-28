# PrintEXE Phase 10 — MSI Root-Cause Investigation, Installer Repair & Deployment Validation Report

**Baseline:** ce14622 (verified against origin/main) → **this phase's commit:** `7425fd4` (see §3 — PUSH BLOCKED, §14)
**Role:** senior Windows Installer engineer, WiX v5 specialist, deployment architect, QA
**Status:** ROOT CAUSE FIXED AND VALIDATED OFFLINE; CI EXECUTION PENDING PUSH (no credentials in this session — §14)

---

## 1. Original error 1606 root cause — VERIFIED (three independent evidence sources)

**Failure:** `msiexec /i PrintShopCompanion-0.9.254-x64.msi /qn` → exit 1603; verbose log shows
`CostFinalize → Note: 1: 1314 2: PrintShop → Note: 1: 1606 2: PrintShop` after a 49-second
network-location resolution timeout, twice (warning then fatal), then `INSTALL. Return value 3`.

**Root cause — Property/Directory Id collisions.** The MSI database of the failed package
(`PrintShopCompanion-0.9.254-x64.msi`, SHA-256 `e5c67969d1222695a27bf6bade24461a7b27934a56c74235cdbb1e9acdb9c99c`,
GitHub Actions run 140947475220) was inspected directly with pymsi plus the captured verbose log:

| Evidence source | Finding |
| --- | --- |
| MSI **Property table** | `INSTALLDIR = C:\Program Files\PrintShop\Companion` and `STARTMENUFOLDER = PrintShop` exist as property rows **whose names are also Directory-table Ids** |
| MSI **Directory table** | The tree resolves `INSTALLDIR → C:\Program Files\PrintShop` (with an orphan generated-id child `…\Companion`) and `STARTMENUFOLDER → C:\ProgramData\Microsoft\Windows\Start Menu\Programs\PrintShop` — i.e., the tree and the Property rows contradict each other |
| Verbose **install log** | At CostFinalize the Property values win: `Property(S): STARTMENUFOLDER = PrintShop` (drive-less). Windows Installer interprets a path without a drive letter as a **network location** (UNC host `PrintShop`) → 49 s SMB timeout → error 1606 → fatal 1603 |

The WiX source (baseline ce14622, `installer/PrintShopCompanion.Installer.wxs:55-56`) authored both
collisions: `<Property Id="INSTALLDIR" Value="C:\Program Files\PrintShop\Companion" />` and
`<Property Id="STARTMENUFOLDER" Value="PrintShop" />`. The `INSTALLDIR` collision survived only by
accident (its value happened to be an absolute path); the drive-less `STARTMENUFOLDER` value
`PrintShop` is exactly the string in the 1606 error message.

**Phase 9's theory disproven.** The run-12 package already contained the ce14622 "fix"
(non-advertised shortcut: Shortcut table `advertised=False`, `Target=[INSTALLDIR]PrintShopCompanion.exe`)
and still failed identically. Also ruled out: **no AppSearch/RegLocator/DrLocator/IniLocator tables**
exist in the MSI; the only custom action (`SetPerUserStartup`, Type 226) is sequenced after
`InstallFinalize` and cannot affect CostFinalize; the package uses an **embedded cabinet**
(`EmbedCab="yes"`), so no source-list resolution is involved. Full analysis:
`docs/PHASE10_ROOT_CAUSE_1606.md`; raw evidence: `download/phase10_evidence/`
(run-12 verbose log, MSI DB dump, inspector result).

## 2. Exact WiX changes — VERIFIED (implemented; compile-validated locally)

File: `installer/PrintShopCompanion.Installer.wxs` (commit `7425fd4`):

1. **Deleted both Property-table collisions** (lines 55-56 of the baseline). Directory properties
   resolve from the tree only.
2. **Deterministic directory tree** — `INSTALLDIR` moved to the leaf so it equals the documented
   install path without any override:
   ```xml
   <StandardDirectory Id="ProgramFiles64Folder">
     <Directory Id="VendorDir" Name="PrintShop">
       <Directory Id="INSTALLDIR" Name="Companion" />
     </Directory>
   </StandardDirectory>
   <StandardDirectory Id="ProgramMenuFolder">
     <Directory Id="STARTMENUFOLDER" Name="PrintShop" />
   </StandardDirectory>
   ```
   Every `[INSTALLDIR]` reference (shortcut target + working directory, both URL-protocol registry
   commands + DefaultIcon, autostart custom action, worker `ImagePath`) now expands consistently to
   `C:\Program Files\PrintShop\Companion`; the orphan child directory disappears.
3. **Uninstall-behavior fix (found during this phase):** the per-user Startup shortcut created by the
   install-time autostart CA (`--register-autostart`) was **not MSI-owned** and survived uninstall —
   a stale autostart entry pointing at a deleted exe on every subsequent login. Added
   `UnsetPerUserStartup` (immediate CA, `--unregister-autostart`, `Before="InstallValidate"`,
   `Condition='REMOVE ~= "ALL"'`, `Return="ignore"`) and `Condition="NOT Installed"` on the register
   CA so it cannot re-create the shortcut during uninstall or repair.
4. **Corrected the stale Phase 9 comment** (advertised-shortcut theory) so future maintainers are not
   misled.

**Local compile validation (no Windows machine in this sandbox):** WiX v5 5.0.2 on .NET 10 (Linux).
The local build caught and fixed a real defect (WIX0104: XML comment containing `--`), and the new
CustomAction/`InstallExecuteSequence`/`Condition` authoring validated through WiX's full validation
phase to the bind step (bind itself needs Windows `msi.dll`). The remaining local errors
(WIX0389 "Name is not a relative path", WIX0027 on `$(var.PublishDir)\…` joins) were **proven
environmental**: the pre-fix baseline .wxs — which compiled successfully on Windows CI (run 12) —
fails with the identical error set on Linux WiX, and a 12-line minimal .wxs with plain
`Name="PrintShop"` reproduces WIX0389. Windows CI remains the authoritative build gate.

## 3. Commit IDs

| Commit | Content |
| --- | --- |
| `7425fd4` | fix(phase10): root cause fix + `msi-preflight.ps1` + `msi-validation.ps1` + `msi-validation.yml` + root-cause doc + build-windows.yml guard. **Committed locally; push pending (§14).** |
| `ce14622` | Phase 9 baseline (verified equal to origin/main before any change) |

## 4. Windows workflow run URLs

**Pending the push** (§14). The independent validation workflow
(`.github/workflows/msi-validation.yml`) triggers on every push to main and on manual dispatch.
The main pipeline (`build-windows.yml`, job `msi`) now also runs the preflight guard.
This section will be populated with run URLs, per-job outcomes and evidence links immediately
after the push; the harness uploads `msi-validation-evidence` (results JSON/markdown + all verbose
MSI logs, 90-day retention) `if: always()`, so a failing install still produces full diagnostics.

## 5. MSI artifact SHA-256

| Package | SHA-256 | Status |
| --- | --- | --- |
| Failed run-12 package (root-cause evidence) | `e5c67969d1222695a27bf6bade24461a7b27934a56c74235cdbb1e9acdb9c99c` | VERIFIED (downloaded + inspected) |
| Fixed MSI (0.9.<run_number>, from commit `7425fd4`) | pending CI | NOT YET PRODUCED — the artifact will be recorded here with its SHA-256 from the workflow run |

## 6. Clean installation results — NOT VERIFIED YET (harness ready)

Scenario A of `installer/msi-validation.ps1` performs on a clean `windows-latest` runner: pre-clean
of any installed product, fresh-state assertions, silent install with `/l*v`, and verification that
**never relies on the msiexec exit code alone**: installed file layout (exe, worker, SumatraPDF,
appsettings.json, ≥5 DLLs), ARP registration with correct DisplayVersion, Start-menu shortcut whose
`.lnk` target is resolved via WScript.Shell and compared to the real installed exe, ACL capture,
temp-residue count, footprint. Evidence JSON + markdown + logs uploaded per run.

## 7. Application launch results — NOT VERIFIED YET (harness ready)

Scenario "S7" launches the **installed** executable (never a development build): process-alive
check after 20 s, working-set capture, first-launch latency until the SQLCipher `queue.db` appears,
app-log verification, graceful-shutdown attempt (`CloseMainWindow` → 30 s wait → Kill fallback,
recorded honestly). SQLCipher/DPAPI health is verified **behaviorally**: per the Phase 9
fail-closed contract, a living process implies successful DI host build, configuration load,
SQLCipher initialization and MainWindow construction (startup is fatal otherwise).

## 8. Upgrade results — NOT VERIFIED YET (harness ready)

Scenarios E/F build **two distinct MSI versions** (A = `0.9.<run_number>`, B = `0.9.<run_number+1>`)
and verify: MajorUpgrade removes exactly the old product (exactly one ARP entry afterwards),
registered version moves A→B, exe file version matches, and — the critical check — the **SQLCipher
`queue.db`, DPAPI-protected `queue.key` and device token are byte-identical (SHA-256) across the
upgrade**, with a post-upgrade launch proving the key still decrypts the database.

## 9. User-data preservation results — NOT VERIFIED YET (harness ready)

The preflight records the **absence of a RemoveFile table** (the uninstaller can only remove owned
components — the documented policy). Scenario J seeds a pending job (`Jobs\queued\…pdf` with real
queue.db present from the launch scenario) and verifies after uninstall: all of
`queue.db`/`queue.key`/device token/queued PDFs survive **byte-identical**, while program files,
shortcuts, ARP entry and the autostart `.lnk` are removed. Scenario C records the no-previous-data
state before the first install; scenario B verifies a pre-existing directory with a foreign file is
neither blocked nor destructively cleaned.

## 10. Uninstallation results — NOT VERIFIED YET (harness ready)

Scenario D (reinstall after uninstall) and I (interrupted setup: msiexec force-killed mid-install,
remnant cleared, recovery install verified) plus J (§9) cover the uninstall contract; each records
the removal state machine-check (exe/worker/SumatraPDF/shortcuts/ARP/autostart lnk) rather than
exit codes.

## 11. Security findings (Stage 6/8)

- **MSI secret scan — VERIFIED clean:** Property table contains no password/secret/apikey/token-like
  properties (inspector + preflight CHECK 5); the MSI carries no connection strings or keys; the CI
  workflow additionally scans all captured installer logs for `password=/secret/apikey/bearer/authorization`
  patterns and fails the run on any hit.
- **No destructive custom actions — VERIFIED:** the only CAs register/unregister a per-user Startup
  `.lnk`; the cleanup CA runs only on full uninstall, before file removal, never breaks the
  uninstall (`Return="ignore"`), and deletes exactly one known file. No `RemoveFile` rows.
- **SQLCipher/DPAPI untouched — VERIFIED:** the installer neither touches nor weakens
  `%LocalAppData%\PrintShop\Companion\Security\*` or `Database\queue.db`; upgrade preservation is
  explicitly tested (§8). DPAPI CurrentUser scope means the key does not roam with the profile —
  by design (documented in `LocalStorageLayout.cs`).
- **Unsigned MSI — KNOWN LIMITATION (unchanged):** no PFX secret is configured; integrity relies on
  the GitHub artifact SHA-256 channel. Signing remains a pre-release TODO.
- **Per-machine components writing HKCU (`ProtocolRegistration*`) — pre-existing antipattern, kept
  unchanged** (out of scope): URL-protocol registration is per-user of the installing user; other
  users get no protocol handler until first per-user sync. Symmetric limitation documented for the
  autostart `.lnk` cleanup (a per-machine uninstall cannot clean other users' Startup folders).
- **No production customer data used** — all testing is synthetic on ephemeral runners; no secrets
  in logs (enforced in-CI).

## 12. Remaining compatibility limitations (Stage 8 — documented)

- **Intended minimum OS:** Windows 10 / Windows 11 (per `INSTALL.md`); the runner-verified
  environment is Windows Server (windows-latest image) — real Windows 10/11 desktop verification
  requires a VM/hardware pass, NOT VERIFIED from CI.
- **.NET runtime:** the app is published **self-contained** in CI (`-p:SelfContained=true`) — no
  Desktop Runtime needed for the app. The **worker** is framework-dependent
  (`--self-contained false -f net10.0-windows`): a shop PC without .NET 10 Desktop Runtime gets a
  running app but a non-starting worker. Recommendation (post-phase): publish the worker
  self-contained too.
- **Windows Installer 5.0** required (MSI engine features used are 5.0-era; all supported Windows
  versions ship it).
- **No WebView2/VC++ runtime dependencies found** (no WebView2 references in source; self-contained
  .NET). SumatraPDF 3.6.1 ships its own runtime deps.
- **Worker service:** the `WorkerServiceInstall` component writes HKLM service keys but does **not**
  actually create a Windows service (pre-existing stub, documented in the .wxs comment) — the
  background worker currently runs only when launched; NOT VERIFIED beyond the keys' existence.
- **MSI unsigned** (§11); **per-machine scope requires admin** (by design, scenario G tests the
  refusal); per-user data is untouched by design.

## 13. Recommended release readiness

**Not release-ready until the CI matrix passes** (§6-10). The installer architecture itself is now
correct: deterministic paths, no property collisions, no network-location resolution risk (guarded
permanently by the preflight check), honest uninstall behavior, evidence-producing validation on
every push. Release readiness = `msi-validation.yml` green (all scenarios VERIFIED) + code signing
+ a real Windows 10/11 desktop pass. The preflight guard makes a recurrence of the 1606 failure
class a build-time error rather than a deployment-time 1603.

## 14. CURRENT BLOCKER — push pending

This sandbox session has **no GitHub credentials** (checked: environment variables, git
credential config, `~/.ssh`, `~/.netrc`, `.env`, workspace files, git logs — the PAT used in
earlier phases was session-provided and the sandbox was recycled since). Commit `7425fd4` is
complete and ready to push; every CI-dependent verification above starts the moment it lands on
`origin/main`. Required action: supply a fresh PAT with **repo contents:write** (and note the
previously pasted PAT was exposed in chat **twice** — rotate it; do not reuse it) or push manually:

```
git push origin main          # HEAD = 7425fd4
```

## 15. Classification summary (current state)

| Item | Status |
| --- | --- |
| Error 1606 root cause (log + MSI DB + source, three sources) | **VERIFIED** |
| WiX fix implemented + WiX v5 compile validation (local, to bind phase) | **VERIFIED** |
| Preflight guard + validation harness + workflows implemented | **VERIFIED** (parser/YAML validated) |
| MSI builds on CI / artifact SHA-256 | **NOT VERIFIED** (pending push) |
| Clean install (A/B/C/D), launch (S7), upgrade (E), encrypted-data (F), admin (H), interrupted (I), uninstall-with-data (J) | **NOT VERIFIED** (harness ready; runs on push) |
| Standard-user refusal (G) | **NOT VERIFIED** (expected-refusal test ready) |
| Uninstaller removes autostart `.lnk` | **NOT VERIFIED** (new behavior; test ready) |
| Performance metrics vs Phase 8 baseline (~146.5 MB, 1.5-2 s) | **NOT VERIFIED** (metrics instrumentation ready) |
| Security: MSI secret-free, no destructive CAs, SQLCipher/DPAPI untouched | **VERIFIED** (offline inspection) |
| MSI signing | **NOT VERIFIED** (no PFX secret — documented limitation) |
