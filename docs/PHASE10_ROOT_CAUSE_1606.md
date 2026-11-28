# Phase 10 Root-Cause Report — MSI error 1606 "Could not access network location PrintShop"

**Status:** ROOT-CAUSED (pre-fix, as required before applying the fix)
**Package under analysis:** `PrintShopCompanion-0.9.254-x64.msi` (GitHub Actions run 140947475220, job `msi`)
**SHA-256:** `e5c67969d1222695a27bf6bade24461a7b27934a56c74235cdbb1e9acdb9c99c`
**Evidence:** `download/phase10_evidence/install.log` (verbose install log, UTF-8 converted), `full_dump.txt` (MSI database dump), `msi_inspect_result.txt` (table inspection)

## 1. Symptom

Silent install (`msiexec /i … /qn /norestart /l*v install-smoke-install.log`) fails with
**exit code 1603** after ~49 seconds. The verbose log shows:

```
Action start 10:22:32: CostFinalize.
MSI (s) (F0:94) [10:22:32:865]: PROPERTY CHANGE: Adding TARGETDIR property. Its value is 'D:\'.
MSI (s) (F0:94) [10:23:21:922]: Note: 1: 1314 2: PrintShop
MSI (s) (F0:94) [10:23:21:922]: Note: 1: 1606 2: PrintShop
MSI (s) (F0:94) [10:23:21:922]: Product: PrintShop Companion -- Error 1606. Could not access network location PrintShop.
Action ended 10:23:21: CostFinalize. Return value 3.        <- fatal
Action ended 10:23:21: INSTALL. Return value 3.             <- translated to exit 1603
```

The 49-second gap (10:22:32 → 10:23:21) is Windows Installer timing out trying to
resolve `PrintShop` **as a network location** before failing.

## 2. Root cause — Property table rows override Directory-table resolution

The MSI database (inspected directly, not from source) contains **two Property-table
rows whose names are also Directory-table row Ids**:

| Property row (Value)          | Also a Directory Id | Directory tree would resolve to |
|-------------------------------|---------------------|--------------------------------|
| `STARTMENUFOLDER = PrintShop` | YES                 | `C:\ProgramData\Microsoft\Windows\Start Menu\Programs\PrintShop` |
| `INSTALLDIR = C:\Program Files\PrintShop\Companion` | YES | `C:\Program Files\PrintShop` (with an orphan generated-id child `…\Companion`) |

Verbose-log confirmation of the Property-table values winning over tree resolution:

```
Property(S): INSTALLDIR = C:\Program Files\PrintShop\Companion    <- from Property table, NOT the tree
Property(S): STARTMENUFOLDER = PrintShop                          <- from Property table, drive-less!
Property(S): ProgramMenuFolder = C:\ProgramData\Microsoft\Windows\Start Menu\Programs\
```

**Mechanism.** Every Directory-table row is a property at install time. When a
Property-table row sets the same name, the Property value **takes precedence over
the Directory-tree resolution at CostFinalize**. The WiX source authored both as
`<Property>` elements (`installer/PrintShopCompanion.Installer.wxs:55-56` in
baseline ce14622):

```xml
<Property Id="INSTALLDIR" Value="C:\Program Files\PrintShop\Companion" />
<Property Id="STARTMENUFOLDER" Value="PrintShop" />
```

`STARTMENUFOLDER`'s value `PrintShop` is a **drive-less relative path**. Windows
Installer treats a path without a drive letter as a **network location** (UNC host
`PrintShop`), attempts to access it, times out (~49 s), and fails with error
**1606 "Could not access network location PrintShop"** — the error string contains
exactly the failing property value. CostFinalize returns 3 (fatal) → INSTALL
returns 3 → `msiexec` exit code **1603**.

The `INSTALLDIR` collision has the same defect but survived *by accident*: its
Property value happens to be an absolute path, so it resolves instead of failing —
while silently contradicting the Directory tree (`INSTALLDIR` in the tree is
`C:\Program Files\PrintShop`, making the authored `<Directory Name="Companion"/>`
child an orphan that no component references).

## 3. Why the Phase 9 fix (ce14622) did not work

Phase 9 hypothesized that **advertised shortcuts** made CostFinalize resolve the
package source location, and switched the shortcut to `Advertise="no"`. The run-12
package confirms the shortcut is already **non-advertised** (Shortcut table,
`advertised=False`, `Target=[INSTALLDIR]PrintShopCompanion.exe`) and the failure
is identical — because the real trigger is the Property/Directory collision, not
shortcut advertisement. The source-resolution theory is disproven: the package
has an embedded cabinet (`Media Id=1, Cabinet=PrintShopCompanion.cab, EmbedCab=yes`),
and no AppSearch/RegLocator tables exist in the MSI at all.

## 4. Ruled out

- **No AppSearch / RegLocator / IniLocator / DrLocator tables** in the MSI — no registry search sets any path property.
- **No custom action runs before CostFinalize** — the only CA (`SetPerUserStartup`, Type 226, directory-targeted exe) is sequenced `After="InstallFinalize"`, so it cannot cause a CostFinalize failure.
- **Not a source-list problem** — embedded cabinet; FileCost succeeded; the failure is in directory resolution, not source resolution.
- **Not per-user vs per-machine** — `ALLUSERS = 1`, `Scope="perMachine"` resolve consistently (`ProgramMenuFolder = C:\ProgramData\…` per-machine Start Menu).
- **UpgradeCode/ProductCode** — `UpgradeCode {A6F0B7D2-…-7C0F3B6A7D2E}` is stable; two clean `Upgrade` rows (detect newer/older). Not implicated.

## 5. Fix applied (Stage 3) — smallest correct architectural change

1. **Delete both Property-table collisions** (`<Property Id="INSTALLDIR">` and `<Property Id="STARTMENUFOLDER">`). Directory properties must be resolved from the Directory tree, never re-declared as Property-table rows.
2. **Make the Directory tree deterministic and self-consistent** — `INSTALLDIR` moves to the leaf so it equals the documented install path `C:\Program Files\PrintShop\Companion`:

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

   All existing `[INSTALLDIR]` references (shortcut target/working directory, URL-protocol registry values, autostart custom action, worker service `ImagePath`) now expand to the real leaf path; the orphan directory disappears.
3. **Regression guard** — a preflight validator (native `WindowsInstaller.Installer` COM, PowerShell 5.1) added to CI fails the build if any Property name collides with a Directory Id or any directory resolves to a drive-less path (the 1606 pattern). The same check exists offline in `scripts/msi_inspect.py` (pymsi).

## 6. Post-fix expected resolution (to be verified by CI)

| Directory | Post-fix resolution |
|-----------|--------------------|
| `INSTALLDIR` | `C:\Program Files\PrintShop\Companion` (deterministic, from tree) |
| `STARTMENUFOLDER` | `C:\ProgramData\Microsoft\Windows\Start Menu\Programs\PrintShop` |
