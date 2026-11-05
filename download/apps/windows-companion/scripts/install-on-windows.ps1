# PrintShop Companion — one-click build + install
# ---------------------------------------------------------------------------
# Run this on a Windows 11 PC (or Windows 10 22H2+). Right-click the file,
# choose "Run with PowerShell". Or from a PowerShell window:
#
#     Set-ExecutionPolicy -Scope Process Bypass
#     .\install-on-windows.ps1
#
# What it does, in order:
#   1. Checks you are on Windows 10/11
#   2. Installs the .NET 10 SDK if you don't have it
#   3. Installs WiX v5 as a dotnet global tool
#   4. Downloads SumatraPDF.exe into installer\redist\
#   5. dotnet restore + build + test
#   6. dotnet publish the WPF app (single-file, ReadyToRun)
#   7. wix build the .msi
#   8. Copies the finished PrintShopCompanion.msi to your Downloads folder
#   9. Offers to launch it
#
# You only need internet access. No manual software installs.
# ---------------------------------------------------------------------------

#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$RepoDir = (Split-Path -Parent $MyInvocation.MyCommand.Path),
    [string]$OutDir  = (Join-Path $env:USERPROFILE 'Downloads'),
    [switch]$SkipTests,
    [switch]$LaunchInstaller
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "  OK  $msg" -ForegroundColor Green }
function Write-Err($msg)  { Write-Host "  ERR $msg" -ForegroundColor Red }

# ── 1. OS check ────────────────────────────────────────────────────────────
Write-Step '1 / 9  Checking Windows version'
if (-not [Environment]::Is64BitOperatingSystem) {
    Write-Err 'A 64-bit Windows 10/11 PC is required.'
    exit 1
}
$win = [Environment]::OSVersion.Version
Write-Ok "Windows build $win"

# ── 2. .NET 10 SDK ─────────────────────────────────────────────────────────
Write-Step '2 / 9  Checking .NET 10 SDK'
$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
$needInstall = $true
if ($dotnetCmd) {
    $sdks = dotnet --list-sdks 2>$null
    if ($sdks -match '^10\.') {
        Write-Ok ".NET 10 SDK already installed"
        $needInstall = $false
    }
}
if ($needInstall) {
    Write-Host '  Installing .NET 10 SDK via winget...' -ForegroundColor Yellow
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if (-not $winget) {
        Write-Err 'winget not found. Install "App Installer" from the Microsoft Store, then re-run this script.'
        Write-Host '  Or install the .NET 10 SDK manually from https://dotnet.microsoft.com/download/dotnet/10.0'
        exit 1
    }
    winget install --id Microsoft.DotNet.SDK.10 --accept-package-agreements --accept-source-agreements --silent
    $env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')
    $dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnetCmd) {
        Write-Err '.NET 10 install failed. Install manually from https://dotnet.microsoft.com/download/dotnet/10.0'
        exit 1
    }
    Write-Ok ".NET 10 SDK installed"
}

# ── 3. WiX v5 ───────────────────────────────────────────────────────────────
Write-Step '3 / 9  Checking WiX v5'
$toolsDir = Join-Path $env:USERPROFILE '.dotnet\tools'
$env:Path = "$toolsDir;$env:Path"
$wixCmd = Get-Command wix -ErrorAction SilentlyContinue
if (-not $wixCmd) {
    Write-Host '  Installing WiX v5 as a dotnet tool...' -ForegroundColor Yellow
    dotnet tool install -g wix | Out-Null
    $wixCmd = Get-Command wix -ErrorAction SilentlyContinue
    if (-not $wixCmd) {
        Write-Err 'WiX install failed.'
        exit 1
    }
}
Write-Ok "wix at $($wixCmd.Source)"

# ── 4. SumatraPDF.exe ──────────────────────────────────────────────────────
Write-Step '4 / 9  Fetching SumatraPDF'
$redist = Join-Path $RepoDir 'installer\redist'
New-Item -ItemType Directory -Force -Path $redist | Out-Null
$sumatraExe = Join-Path $redist 'SumatraPDF.exe'
if (Test-Path $sumatraExe) {
    Write-Ok 'SumatraPDF.exe already present'
} else {
    $zip = Join-Path $env:TEMP 'sumatra.zip'
    Write-Host '  Downloading...' -ForegroundColor Yellow
    try {
        Invoke-WebRequest -Uri 'https://www.sumatrapdfreader.org/download/sumatra-prerel-64.zip' -OutFile $zip -UseBasicParsing
        Expand-Archive -Path $zip -DestinationPath (Join-Path $env:TEMP 'sumatra') -Force
        Copy-Item (Join-Path $env:TEMP 'sumatra\SumatraPDF.exe') $sumatraExe -Force
        Write-Ok 'SumatraPDF.exe downloaded'
    } catch {
        Write-Err "Could not download SumatraPDF: $_"
        Write-Host '  Manually place SumatraPDF.exe at: installer\redist\SumatraPDF.exe'
        exit 1
    }
}

# ── 5. Restore + build ────────────────────────────────────────────────────
Write-Step '5 / 9  Restore + build solution'
dotnet restore (Join-Path $RepoDir 'PrintShopCompanion.sln') 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) { Write-Err 'restore failed'; exit 1 }
dotnet build (Join-Path $RepoDir 'PrintShopCompanion.sln') -c Release --no-restore 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) { Write-Err 'build failed'; exit 1 }
Write-Ok 'build succeeded'

# ── 6. Tests (optional) ────────────────────────────────────────────────────
if (-not $SkipTests) {
    Write-Step '6 / 9  Running unit tests'
    dotnet test (Join-Path $RepoDir 'PrintShopCompanion.sln') -c Release --no-build --logger "console;verbosity=normal" 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Host '  (tests failed — re-run with -SkipTests to skip and still build the MSI)' -ForegroundColor Yellow
    } else {
        Write-Ok 'tests passed'
    }
} else {
    Write-Host '  Skipped (-SkipTests)' -ForegroundColor Yellow
}

# ── 7. Publish ─────────────────────────────────────────────────────────────
Write-Step '7 / 9  Publishing WPF app (single-file)'
$artifacts = Join-Path $RepoDir 'artifacts\app'
if (Test-Path $artifacts) { Remove-Item -Recurse -Force $artifacts }
dotnet publish (Join-Path $RepoDir 'src\PrintShopCompanion.App\PrintShopCompanion.App.csproj') `
    -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:PublishReadyToRun=true `
    -o $artifacts 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) { Write-Err 'publish failed'; exit 1 }
Write-Ok "app published to $artifacts"

# ── 8. Build MSI ──────────────────────────────────────────────────────────
Write-Step '8 / 9  Building PrintShopCompanion.msi'
$msiOut = Join-Path $RepoDir 'installer\build'
New-Item -ItemType Directory -Force -Path $msiOut | Out-Null
if (Test-Path (Join-Path $msiOut 'PrintShopCompanion.msi')) {
    Remove-Item -Force (Join-Path $msiOut 'PrintShopCompanion.msi')
}
wix build (Join-Path $RepoDir 'installer\PrintShopCompanion.Installer.wxs') `
    -d "PublishDir=$artifacts\" `
    -d "RepoRoot=$RepoDir" `
    -o (Join-Path $msiOut 'PrintShopCompanion.msi') 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) { Write-Err 'MSI build failed'; exit 1 }

$msi = Join-Path $msiOut 'PrintShopCompanion.msi'
if (-not (Test-Path $msi)) { Write-Err "MSI not found at $msi"; exit 1 }
Write-Ok "MSI built: $msi"

# Copy to Downloads
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }
$destMsi = Join-Path $OutDir 'PrintShopCompanion.msi'
Copy-Item -Force $msi $destMsi
Write-Ok "Copied to: $destMsi"

# ── 9. Launch installer (optional) ────────────────────────────────────────
if ($LaunchInstaller) {
    Write-Step '9 / 9  Launching installer'
    Start-Process $destMsi
} else {
    Write-Host "`nDone. Your installer is at:" -ForegroundColor Green
    Write-Host "  $destMsi" -ForegroundColor Green
    Write-Host "`nDouble-click it to install, or re-run this script with -LaunchInstaller to open it now." -ForegroundColor Yellow
    Write-Host "After install, find 'PrintShop Companion' in the Start menu.`n" -ForegroundColor Yellow
}
