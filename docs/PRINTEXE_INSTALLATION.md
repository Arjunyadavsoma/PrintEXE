# PrintEXE — Installation & Upgrade Guide

**Audience:** print-shop owners/technicians. **Date:** 2026-11-26.

## 1. Requirements

| Item | Minimum |
|---|---|
| OS | Windows 10 21H2+ / Windows 11 / Server 2019+ (x64) |
| Runtime | Bundled (self-contained ZIP) — or .NET 10 Desktop Runtime when using the MSI |
| Disk | 300 MB free (min 256 MB enforced for print jobs; queue refuses downloads below it) |
| Printer | Any printer installed in Windows (USB or network). PrintEXE uses the installed Windows drivers — no vendor-specific companion drivers needed |
| Network | Outbound HTTPS to the Printelyx cloud (port 443) |

## 2. Install — portable ZIP (recommended, and what CI produces today)

1. Download `PrintEXE-vX.Y.Z-win-x64.zip` from the GitHub release (verify the `SHA256SUMS.txt` entry matches the file — PowerShell: `Get-FileHash PrintEXE-...zip`).
2. Extract to a per-machine folder such as `C:\PrintEXE\` (avoid `Program Files` for the portable build: it needs to write next to the exe for auto-update file swaps; per-user data goes to `%AppData%`/`%LocalAppData%\PrintShop\Companion\` regardless).
3. Run `PrintShopCompanion.exe`. First run shows the login screen; sign in with the shop's Printelyx account, then pair the device (Settings → pairing) and select a printer.
4. Optional autostart: Settings → "Start with Windows" (per-user Startup shortcut; no admin needed).

**Data layout (never copy between machines):** `%LocalAppData%\PrintShop\Companion\` holds the DPAPI-encrypted device token, the SQLCipher queue key, and the queue database. Copying this folder to another PC corrupts identity and can cause duplicate printing (documented PrintNode lesson; our DPAPI keys are per-user, so the copied DB is unreadable on the new machine — fail-closed).

## 3. Install — MSI / bootstrapper (WiX sources; built by CI when enabled)

- `PrintShopCompanion.msi` — per-machine install to `C:\Program Files\PrintShop\Companion\`; registers the `printshopcompanion://` URL scheme per-user (HKCU); optional Worker service feature (admin).
- `PrintShopCompanion.Setup.exe` — bundle that chains the .NET 10 Desktop Runtime + the MSI.

**Silent install flags** (Inno/Wix-standard, documented here for fleet deployments — a doc gap the research found in PrintNode):
```powershell
msiexec /i PrintShopCompanion.msi /qn /norestart ADDLOCAL=AppFeature
msiexec /i PrintShopCompanion.msi /qn /norestart ADDLOCAL=AppFeature,WorkerFeature
```
Silent uninstall: `msiexec /x PrintShopCompanion.msi /qn`. Portable uninstall: delete the folder + `%AppData%\PrintShop` + `%LocalAppData%\PrintShop` (removes credentials — do this only when decommissioning the device, then revoke the device in the shop dashboard).

## 4. First-run setup checklist (the shopkeeper flow)

1. Sign in (email + password).
2. Pair this PC as the shop's print device (Settings → Device pairing; paste the token from the dashboard or use the pairing ticket flow).
3. Printers screen: confirm discovery, select the default printer, click **Test print** and confirm paper comes out.
4. Diagnostics screen: **Run check** — every row should be green (cloud reachable, token valid, device registered, DB healthy, queue worker running).
5. Place a test order on the Printelyx website; watch it land in Print Queue → Spooled → Completed, and confirm the physical page.

## 5. Upgrade

- **Portable:** stop the app (tray → Quit), extract the new ZIP over the folder, start again. All credentials/queue/logs survive (they live under AppData). Version confirms in Settings (read from the assembly, never hardcoded).
- **MSI:** major-upgrade tables keep per-user data; the Worker service, when installed, is stopped/started by the installer.
- Auto-update: the app checks GitHub releases (semver + SHA-256), downloads, stages a `.bat` swap, and restarts. If the check is disabled (offline shop), upgrade manually — the update never fires while the queue is processing (the swap runs only after the app exits).

## 6. Uninstall / decommission

1. Settings → sign out; revoke the device in the Printelyx dashboard (kills the token server-side).
2. Uninstall/remove the app folder.
3. Optionally delete `%AppData%\PrintShop` and `%LocalAppData%\PrintShop` (credentials + queue). The queue DB is SQLCipher-encrypted; deleting the key file (queue.key) without the DB makes the DB unreadable — delete both together.
