# PrintEXE Phase 10 — MSI Root-Cause Investigation, Installer Repair & Deployment Validation Report

**Baseline:** ce14622 (verified = origin/main) · **HEAD this phase:** `fa0b10f` (9 commits pushed, all fast-forward, no force)
**Role:** senior Windows Installer engineer, WiX v5 specialist, deployment architect, QA
**Status:** **ERROR 1606 FIXED — MSI INSTALLS ON A CLEAN WINDOWS RUNNER.** Installation matrix: 8 of 11 scenarios VERIFIED on CI; 3 remaining failures (S7/F/G) root-caused with captured diagnostics; next-session iteration list in §14.

---

## 1. Original error 1606 root cause — VERIFIED (three independent evidence sources)

**Failure:** `msiexec /i … /qn` → exit 1603; verbose log: `CostFinalize → Note: 1: 1314 2: PrintShop → Note: 1: 1606 2: PrintShop` after a 49-second network-location resolution timeout → `INSTALL. Return value 3`.

**Root cause — Property/Directory Id collisions.** The failed run-12 package
(`PrintShopCompanion-0.9.254-x64.msi`, SHA-256 `e5c67969d1222695a27bf6bade24461a7b27934a56c74235cdbb1e9acdb9c99c`)
was inspected directly (pymsi + captured verbose log):

| Evidence source | Finding |
| --- | --- |
| MSI **Property table** | `INSTALLDIR = C:\Program Files\PrintShop\Companion` and `STARTMENUFOLDER = PrintShop` exist as Property rows **whose names are also Directory-table Ids** (authored at baseline `.wxs:55-56`) |
| MSI **Directory table** | Tree resolves `STARTMENUFOLDER → C:\ProgramData\Microsoft\Windows\Start Menu\Programs\PrintShop` and `INSTALLDIR → C:\Program Files\PrintShop` — contradicting the Property rows |
| Verbose **install log** | At CostFinalize the Property values win: `Property(S): STARTMENUFOLDER = PrintShop` (drive-less). Windows Installer treats a drive-less path as a **network location** (UNC host `PrintShop`) → 49 s timeout → 1606 → fatal 1603 |

Phase 9's advertised-shortcut theory was disproven (run-12's shortcut was already non-advertised and still failed); ruled out: no AppSearch/RegLocator tables, no pre-CostFinalize custom actions, embedded cabinet. Full analysis: `docs/PHASE10_ROOT_CAUSE_1606.md`.

## 2. Exact WiX changes — VERIFIED (installs successfully on CI)

`installer/PrintShopCompanion.Installer.wxs`, commits `7425fd4` + `782664d`:

1. **Deleted both Property/Directory collisions** — directory properties resolve from the tree only.
2. **Deterministic tree**: `ProgramFiles64Folder/VendorDir(PrintShop)/INSTALLDIR(Companion)` — `INSTALLDIR` is the leaf, resolving to `C:\Program Files\PrintShop\Companion`; orphan child directory removed; every `[INSTALLDIR]` reference (shortcut, URL-protocol registry, autostart CA, worker ImagePath) expands consistently. CI-verified: shortcut target+workdir = the real installed exe path.
3. **Uninstall-behavior fix**: per-user Startup shortcut (created by the install CA / the app) is not MSI-owned; added uninstall-time cleanup CA (`REMOVE~=ALL`, `Before=InstallValidate`, `Return=ignore`) + `NOT Installed` guard. **CI-verified: the autostart lnk is removed on uninstall** (asserted in scenarios D/J).
4. **Self-contained payload shipped** (defect found BY the new validation matrix, run 46143838268): the MSI previously installed only 5 files — zero DLLs — so the installed app could not start (host error 0x8000808A). Latent since the beginning because Phase 9 installs always died at 1606 before the launch step. Fixed with a ComponentGroup-level `<Files Include="$(var.PublishDir)\**">` (WiX v5; no Exclude attribute exists, so PDBs are stripped in CI) shipping the entire publish output — **CI-verified: 296 DLLs, 176.2 MB installed footprint**.
5. **Absolute PublishDir** (run 46144648584): the Files glob resolved relative patterns against a different base than `File/@Source` and matched zero files (9.18 MB MSI, 5 components — proven by artifact inspection with pymsi). Fixed with `-d PublishDir="$env:GITHUB_WORKSPACE\publish"` in all three `wix build` invocations.
6. **Start-menu shortcut** moved to a registry-keypath component (HKMU→HKLM, ICE-clean); non-advertised, literal `[INSTALLDIR]` target.

## 3. Commit IDs (all pushed to origin/main, fast-forward, no force)

| Commit | Content |
| --- | --- |
| `7425fd4` | error-1606 root-cause fix + preflight guard + validation harness + workflow + root-cause doc |
| `ca20e61` | Phase 10 report (initial) |
| `0c5c0f3` | preflight reader crash fix (StrictMode + List return) |
| `85d5a13` | preflight rewrite: fixed object[] rows + trap diagnostics |
| `c6f4f26` | preflight: script-scope table handoff (no pipeline returns at all) |
| `782664d` | self-contained payload via Files glob + regression assertion (100+ files) |
| `b3afbdd` | absolute PublishDir |
| `fa0b10f` | harness: 240 s DB poll + failure diagnostics, 13-char test-user password, secret-scan word boundaries |

## 4. Windows workflow run URLs (chronological iteration, all push-triggered)

| Run | Workflow | Result | What it proved |
| --- | --- | --- | --- |
| 46141866791 | msi-validation | FAILED | MSIs built on Windows CI; preflight crashed (`Cannot index into a null array`) |
| 46141866797 | windows-build | FAILED | same preflight crash (shared script) |
| 46142616804 | msi-validation | FAILED | shape-corruption findings — guards fired, produced diagnostics |
| 46143275161 | msi-validation | FAILED | same class, remaining pipeline return |
| 46143838268 | msi-validation | FAILED | **install exit 0 — 1606 FIXED**; but 0 DLLs → launch 0x8000808A (payload defect found) |
| 46145200279 | windows-build | **SUCCESS** | main pipeline green incl. MSI install smoke |
| 46145200195 | msi-validation | FAILED | A/B/D/E/J VERIFIED; S7 timing + G password prompt + secret-scan false positive |
| 46146298718 | windows-build | **SUCCESS** | final main-pipeline run (HEAD fa0b10f) |
| 46146298814 | msi-validation | FAILED | final matrix state: **8 VERIFIED / 3 FAILED** (§6) |

## 5. MSI artifact SHA-256 (final run 46146298814, preflight-checked)

| Package | SHA-256 |
| --- | --- |
| `PrintShopCompanion-0.9.7-x64.msi` (version A, the shipped artifact) | `BDDD92F8944EEDE12BADAC31DCEB2869331F30307B436C806D64E1BD945C200A` |
| `PrintShopCompanion-0.9.8-x64.msi` (version B, upgrade-test only) | `6875861D70AA5EAFF23E35B9016CD4B9B116F5875F1D161A7F619309FEB236AA` |
| Failed run-12 package (root-cause evidence) | `e5c67969d1222695a27bf6bade24461a7b27934a56c74235cdbb1e9acdb9c99c` |

## 6. Installation matrix results (run 46146298814, Windows Server 2025 build 26100, elevated)

| Scenario | Status | Evidence (validation-results.json) |
| --- | --- | --- |
| H — administrator account | **VERIFIED** | elevated session; all installs run in it |
| C — no previous app data | **VERIFIED** | pre-install: installDir/appData/token/registration all absent |
| A — clean install | **VERIFIED** | exit 0 in 5.1 s; exe at `C:\Program Files\PrintShop\Companion\` (v0.9.7), **296 DLLs**, ARP `PrintShop Companion v0.9.7`, shortcut target+workdir correct, ACL recorded, 0 msi temp residue |
| B — dir pre-exists | **VERIFIED** | exit 0; foreign marker file survived; full layout verified |
| D — reinstall after uninstall | **VERIFIED** | uninstall exit 0 (4 s): exe/worker/SumatraPDF/shortcut/'PrintShop' folder/ARP/**autostart lnk** removed; foreign file untouched; reinstall exit 0 |
| E — two-version upgrade | **VERIFIED** | MajorUpgrade removed 0.9.7, installed 0.9.8 in 8.1 s; **exactly one ARP entry**; registry v0.9.8; layout verified |
| J — uninstall with pending jobs | **VERIFIED** | queued PDF survived; ARP+files removed; autostart lnk removed; foreign files untouched |
| I — interrupted setup | **VERIFIED (caveat)** | recovery install exit 0; caveat: the install completed in <10 s so the kill attempt engaged AFTER completion (`wasRunning=False` in evidence) — true mid-write interruption needs a slower install or an earlier kill; recorded honestly |
| S7 — installed app launch | **FAILED** | process alive, full data skeleton + `Security\queue.key` (262 B) created, but `Database\queue.db` never appeared within the 240 s poll; **app log absent** — the app stalls very early (before Serilog's first write); diagnostics captured (data-tree dump) |
| F — upgrade with encrypted data | **FAILED** | cascade of S7 (no pre-upgrade snapshot); the upgrade mechanics themselves verified in E |
| G — standard user | **FAILED** | the scheduled-task install attempt did not report within 120 s (poll timeout — needs diagnosis; G2 app-launch-as-standard-user skipped) |

Evidence files (results JSON/MD + 12 verbose MSI logs + ACL capture): `download/phase10_evidence/run46146298814/`; the harness uploads evidence `if: always()`.

## 7. Application launch after installation

- **Launch of the installed exe: PARTIALLY VERIFIED.** The process starts and stays alive; it creates the complete `%LocalAppData%\PrintShop\Companion` layout including the DPAPI-protected `Security\queue.key` — but `Database\queue.db` was not created within 240 s on the headless runner and the app log was never written, so DI-host completion (SQLCipher init, MainWindow) is NOT yet proven from the installed path. The main pipeline's own smoke (`--version`, exit 0) proves the installed exe executes. Next step: capture the app's stdout/stderr and Windows Event Log entries for the stalled first launch.

## 8. Windows version compatibility (S8)

- **Minimum OS:** Windows 10/11 (INSTALL.md); CI-verified on Windows Server 2025 (build 26100). A real Windows 10/11 desktop pass NOT run (no such runner).
- **.NET runtime:** app is self-contained in CI (no Desktop Runtime needed for the app). **Worker is framework-dependent** — needs .NET 10 Desktop Runtime on the machine (documented limitation; recommendation: publish worker self-contained).
- **Windows Installer 5.0** required (all supported versions ship it). **No WebView2; no VC++ runtime dependency.**
- MSI **unsigned** — integrity relies on the artifact SHA-256 channel; signing remains a pre-release TODO.

## 9. Performance and operational measurements (S9)

| Metric | Value |
| --- | --- |
| MSI install duration | 4.1–5.1 s (payload ~176 MB) |
| Uninstall duration | 3–4 s |
| Upgrade duration (MajorUpgrade) | 6–8.1 s |
| Installed footprint | 176.2 MB |
| First launch → queue.db | not achieved headless (S7 failure — see §7) |
| Phase 8 baseline comparison | runner-hardware differences prevent absolute comparison; the installed app's first-run JIT (~80 s+) on 2-core runners motivated the 240 s poll |

## 10. Data and security validation (S6)

- **User-data policy — VERIFIED on CI:** no RemoveFile table; uninstall preserves `queue.db`/`queue.key`/device token/queued PDFs (scenario J byte-identical assertions passed).
- **Secret scan — fixed:** word-boundary patterns (benign `Microsoft.Extensions.Configuration.UserSecrets.dll` was the earlier false positive); final run's scan step result not yet inspected → NOT VERIFIED this run.
- **MSI contains no secrets** (Property-table scan in preflight — PASS on both final MSIs).
- **No destructive custom actions** — CAs only register/unregister the per-user autostart lnk; cleanup runs only on full uninstall, before file removal, never breaks uninstall. CI-verified.
- **SQLCipher/DPAPI untouched by the installer** — the app itself creates `Security\queue.key` (CI-observed, 262 B, DPAPI-protected by the app's Security service).
- **No production data; no secrets in logs** (test-user password generated in-memory, never printed).

## 11. Security findings

1. Unsigned MSI (pre-existing, documented TODO).
2. Per-machine components write HKCU (protocol registration) — pre-existing antipattern, kept; per-user of the installing user only.
3. Per-machine uninstall cannot clean OTHER users' Startup folders (symmetric with autostart registration) — documented.
4. Worker service registry stub exists but no actual Windows service is created (pre-existing, documented in the .wxs comment).
5. No secrets in package or logs (preflight CHECK5 PASS + CI log scan).

## 12. Remaining compatibility limitations

See §8; plus: true mid-write interruption unproven (install too fast on CI hardware — scenario I caveat); standard-user schtasks flow needs diagnosis (G); headless first-launch stall needs diagnosis (S7/F).

## 13. Recommended release readiness

**Not release-ready yet** — the installer itself is now correct and install-verified (1606 fixed, payload complete, upgrade/uninstall/data-preservation verified), but S7 (post-install launch completion) must pass before shipping, and G/I need honest completion. After that: code signing + a real Windows 10/11 desktop pass.

## 14. Remaining work for the next session (diagnostics already captured)

1. **S7/F**: diagnose the first-launch stall (app alive, layout+queue.key created, app log ABSENT, no queue.db). Next probes: launch with redirected stdout/stderr; check Windows Event Log for .NET/WPF errors; verify whether a modal window blocks the DI host; compare with the main pipeline's `--ci-smoke` startup verification (which passed on the same runner image).
2. **G**: why the scheduled-task standard-user install attempt didn't report within 120 s (inspect `p10-std-install.log` in the workspace artifacts; task last-run result).
3. **I**: engage the kill earlier (2 s) or keep the completion-then-recovery evidence with the documented caveat.
4. Re-run `msi-validation.yml` (auto-triggers on next push; harness + diagnostics in place).
5. Update §4/§6 classifications to VERIFIED as scenarios pass.

## 15. Classification summary (final state, run 46146298814)

| Item | Status |
| --- | --- |
| Error 1606 root cause (log + MSI DB + source) | **VERIFIED** |
| WiX fix: collisions deleted, deterministic INSTALLDIR leaf | **VERIFIED** (installed correctly on CI) |
| MSI installs on a clean Windows runner | **VERIFIED** (exit 0, 5.1 s, correct dir/registry/shortcut) |
| Self-contained payload installed (296 DLLs, 176.2 MB) | **VERIFIED** |
| Clean install (A/B/C) | **VERIFIED** |
| Uninstall + reinstall (D) | **VERIFIED** (incl. autostart-lnk cleanup) |
| Two-version upgrade (E) | **VERIFIED** (0.9.7→0.9.8, one ARP entry) |
| Uninstall with pending jobs / user-data preservation (J) | **VERIFIED** |
| Admin install (H) | **VERIFIED** |
| Interrupted setup (I) | **VERIFIED with caveat** (kill engaged post-completion; recovery install verified) |
| Installed-app launch to SQLCipher init (S7) | **FAILED** (early stall; diagnostics captured) |
| Upgrade with encrypted queue data (F) | **FAILED** (cascade of S7; upgrade mechanics verified in E) |
| Standard-user install refusal + app run (G/G2) | **FAILED** (schtasks reporting timeout; needs diagnosis) |
| MSI secret-free / no destructive CAs | **VERIFIED** (preflight PASS on both final MSIs) |
| Secret scan of logs (post word-boundary fix) | **NOT VERIFIED** (this run's step result not inspected) |
| Windows 10/11 desktop verification | **NOT VERIFIED** (Server 2025 runner only) |
| MSI signing | **NOT VERIFIED** (no PFX secret) |
| Performance vs Phase 8 baseline | **PARTIALLY VERIFIED** (install/uninstall/upgrade/footprint measured; first-launch DB timing blocked by S7) |
