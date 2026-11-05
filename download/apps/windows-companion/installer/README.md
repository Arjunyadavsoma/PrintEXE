# WiX Installer Notes

The WiX source file `PrintShopCompanion.Installer.wxs` produces `PrintShopCompanion.msi`.

## What the installer does

1. Copies the published WPF app + worker + mock backend to `C:\Program Files\PrintShop\Companion\`.
2. Bundles `SumatraPDF.exe` (placed in `installer/redist/SumatraPDF.exe` by `install-on-windows.ps1` or the GitHub Actions workflow). SumatraPDF is the primary PDF renderer — it prints PDFs via `SumatraPDF.exe -silent -print-to <printer> <pdf>` and reports the spooler job ID via stdout. (The previous PdfiumViewer native binding was removed because it pulled in vulnerable legacy dependencies and targeted .NET Framework.)
3. Registers the `printshopcompanion://` URL scheme **per-user** in `HKCU\Software\Classes\printshopcompanion`. This is the magic-link handler; the OS routes clicks on `printshopcompanion://pair?…` to `PrintShopCompanion.exe "%1"`.
4. Adds a Start Menu shortcut to `PrintShop Companion` under `PrintShop\Companion.lnk`.
5. Optionally installs the worker service (under `HKLM\SYSTEM\CurrentControlSet\Services\PrintShopCompanionWorker` — this requires admin elevation, which the MSI prompts for as a deferred action).
6. After `InstallFinalize`, runs `PrintShopCompanion.exe --register-autostart` to drop a per-user shortcut into `%AppData%\Microsoft\Windows\Start Menu\Programs\Startup\PrintShop Companion.lnk`.

## What the installer does NOT do

- Does not change anything under `HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Run` (admin-only Run keys are noisy; per-user Startup shortcut is sufficient and needs no elevation).
- Does not bundle the .NET 10 runtime — the MSI declares a `Bundle` prerequisite for the .NET 10 Desktop Runtime. WiX v5 supports this via the `wix burn` toolchain; see `Bundle.wxs` (to be added).
- Does not embed a code-signing certificate — signing the MSI is a CI step (see `.github/workflows/build-windows.yml`).

## Build it locally

```powershell
# 1. Install WiX v5
dotnet tool install -g wix

# 2. Publish the WPF app
dotnet publish src\PrintShopCompanion.App\PrintShopCompanion.App.csproj `
  -c Release -r win-x64 --self-contained false `
  -p:PublishSingleFile=true -p:PublishReadyToRun=true `
  -o .\artifacts\app

# 3. Place SumatraPDF.exe in installer/redist/
mkdir installer\redist
curl -L -o installer\redist\SumatraPDF.exe https://www.sumatrapdfreader.org/download/sumatra-prerel-64.zip
# Unzip and move SumatraPDF.exe into installer\redist\.

# 4. Build the .msi
installer\BuildInstaller.bat
```

The MSI will appear at `installer\build\PrintShopCompanion.msi`.
