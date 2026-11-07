# PrintShop Companion — one-click build + install (for shopkeepers and developers)
# ---------------------------------------------------------------------------
# Run this on a Windows 11 PC (or Windows 10 22H2+). Right-click the file,
# choose "Run with PowerShell". Or from a PowerShell window:
#
#     Set-ExecutionPolicy -Scope Process Bypass
#     .\scripts\install-on-windows.ps1
#
# What it does, in order:
#   1. Checks you are on Windows 10/11 (64-bit)
#   2. Installs the .NET 10 SDK if you don't have it
#   3. Installs WiX v5 as a dotnet global tool
#   4. Downloads SumatraPDF.exe into installer\redist\
#   5. dotnet restore + build + test
#   6. dotnet publish the WPF app (SELF-CONTAINED single-file — no .NET install needed on target PC)
#   7. wix build the .msi
#   8. (Optional) wix build the .exe bootstrapper
#   9. Generates SHA-256 checksums
#  10. Copies the finished PrintShopCompanion.msi and PrintShopCompanion.Setup.exe to your Downloads folder
#  11. Offers to launch the installer
#
# You only need internet access. No manual software installs.
# After install, find 'PrintShop Companion' in the Start menu.
# ---------------------------------------------------------------------------

#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$RepoDir = (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)),
    [string]$OutDir  = (Join-Path $env:USERPROFILE 'Downloads'),
    [switch]$SkipTests,
    [switch]$SelfContained = $true,
    [switch]$BuildBundle,
    [switch]$LaunchInstaller
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "  OK  $msg" -ForegroundColor Green }
function Write-Err($msg)  { Write-Host "  ERR $msg" -ForegroundColor Red }
function Write-Warn($msg) { Write-Host "  WARN $msg" -ForegroundColor Yellow }

# ── 1. OS check ────────────────────────────────────────────────────────────
Write-Step '1 / 11  Checking Windows version'
if (-not [Environment]::Is64BitOperatingSystem) {
    Write-Err 'A 64-bit Windows 10/11 PC is required.'
    exit 1
}
$win = [Environment]::OSVersion.Version
Write-Ok "Windows build $win"

# ── 2. .NET 10 SDK ─────────────────────────────────────────────────────────
Write-Step '2 / 11  Checking .NET 10 SDK'
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
Write-Step '3 / 11  Checking WiX v5'
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
Write-Step '4 / 11  Fetching SumatraPDF'
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
Write-Step '5 / 11  Restore + build solution'
dotnet restore (Join-Path $RepoDir 'PrintShopCompanion.sln') 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) { Write-Err 'restore failed'; exit 1 }
dotnet build (Join-Path $RepoDir 'PrintShopCompanion.sln') -c Release --no-restore 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) { Write-Err 'build failed'; exit 1 }
Write-Ok 'build succeeded'

# ── 6. Tests (optional) ────────────────────────────────────────────────────
if (-not $SkipTests) {
    Write-Step '6 / 11  Running unit tests'
    dotnet test (Join-Path $RepoDir 'PrintShopCompanion.sln') -c Release --no-build --logger "console;verbosity=normal" 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Warn 'tests failed — re-run with -SkipTests to skip and still build the MSI'
    } else {
        Write-Ok 'tests passed'
    }
} else {
    Write-Host '  Skipped (-SkipTests)' -ForegroundColor Yellow
}

# ── 7. Publish ─────────────────────────────────────────────────────────────
Write-Step '7 / 11  Publishing WPF app'
$artifacts = Join-Path $RepoDir 'artifacts\app'
if (Test-Path $artifacts) { Remove-Item -Recurse -Force $artifacts }

$pubArgs = @(
    'publish', (Join-Path $RepoDir 'src\PrintShopCompanion.App\PrintShopCompanion.App.csproj'),
    '-c', 'Release', '-r', 'win-x64'
)
if ($SelfContained) {
    $pubArgs += @('--self-contained', 'true',
                  '-p:PublishSingleFile=true',
                  '-p:PublishReadyToRun=true',
                  '-p:IncludeNativeLibrariesForSelfExtract=true')
} else {
    $pubArgs += @('--self-contained', 'false', '-p:PublishSingleFile=true')
}
$pubArgs += @('-o', $artifacts)

dotnet @pubArgs 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) { Write-Err 'publish failed'; exit 1 }
Write-Ok "app published to $artifacts"
if ($SelfContained) {
    Write-Host '  (self-contained — the target PC does not need .NET installed)' -ForegroundColor DarkGray
}

# ── 8. Build MSI ──────────────────────────────────────────────────────────
Write-Step '8 / 11  Building PrintShopCompanion.msi'
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

# ── 9. Build .exe bootstrapper (optional, requires .NET runtime redist) ───
if ($BuildBundle) {
    Write-Step '9 / 11  Building PrintShopCompanion.Setup.exe (bootstrapper)'
    $runtimeExe = Join-Path $redist 'dotnet-runtime-10-win-x64.exe'
    if (-not (Test-Path $runtimeExe)) {
        Write-Host '  Downloading .NET 10 Desktop Runtime...' -ForegroundColor Yellow
        try {
            Invoke-WebRequest -Uri 'https://download.visualstudio.microsoft.com/download/pr/dotnet-runtime-10.0.0-win-x64.exe' `
                -OutFile $runtimeExe -UseBasicParsing
            Write-Ok '.NET 10 runtime downloaded'
        } catch {
            Write-Warn "Could not download .NET 10 runtime: $_"
            Write-Warn 'Skipping bootstrapper. MSI-only build will work but the target PC needs .NET 10 Desktop Runtime installed separately.'
            $BuildBundle = $false
        }
    }
    if ($BuildBundle) {
        wix build (Join-Path $RepoDir 'installer\PrintShopCompanion.Bundle.wxs') `
            -d "RepoRoot=$RepoDir" `
            -o (Join-Path $msiOut 'PrintShopCompanion.Setup.exe') 2>&1 | Out-Host
        if ($LASTEXITCODE -eq 0 -and (Test-Path (Join-Path $msiOut 'PrintShopCompanion.Setup.exe'))) {
            Write-Ok "Setup.exe built: $(Join-Path $msiOut 'PrintShopCompanion.Setup.exe')"
        } else {
            Write-Warn 'Bootstrapper build failed; falling back to MSI-only'
        }
    }
} else {
    Write-Host '  (Bundle build skipped — pass -BuildBundle to also build the .exe setup)' -ForegroundColor Yellow
}

# ── 10. SHA-256 checksums + copy to Downloads ──────────────────────────────
Write-Step '10 / 11  Generating SHA-256 checksums and copying to Downloads'
$hashes = @()
foreach ($file in @('PrintShopCompanion.msi', 'PrintShopCompanion.Setup.exe')) {
    $full = Join-Path $msiOut $file
    if (Test-Path $full) {
        $sha = (Get-FileHash -Algorithm SHA256 $full).Hash
        $hashes += "$sha  $file"
        Write-Host "  $sha  $file"
    }
}
$hashes | Out-File -Encoding ascii (Join-Path $msiOut 'SHA256SUMS.txt')

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force -Path $OutDir | Out-Null }
foreach ($file in @('PrintShopCompanion.msi', 'PrintShopCompanion.Setup.exe', 'SHA256SUMS.txt')) {
    $src = Join-Path $msiOut $file
    if (Test-Path $src) {
        Copy-Item -Force $src (Join-Path $OutDir $file)
        Write-Ok "Copied $file to $OutDir"
    }
}

# ── 11. Launch installer (optional) ────────────────────────────────────────
if ($LaunchInstaller) {
    Write-Step '11 / 11  Launching installer'
    $setup = Join-Path $OutDir 'PrintShopCompanion.Setup.exe'
    $msiPath = Join-Path $OutDir 'PrintShopCompanion.msi'
    if (Test-Path $setup) { Start-Process $setup }
    elseif (Test-Path $msiPath) { Start-Process $msiPath }
    else { Write-Warn 'No installer found to launch' }
} else {
    Write-Host "`nDone. Your installer is in your Downloads folder:" -ForegroundColor Green
    Write-Host "  $OutDir\PrintShopCompanion.msi" -ForegroundColor Green
    if (Test-Path (Join-Path $OutDir 'PrintShopCompanion.Setup.exe')) {
        Write-Host "  $OutDir\PrintShopCompanion.Setup.exe" -ForegroundColor Green
    }
    Write-Host "  $OutDir\SHA256SUMS.txt" -ForegroundColor Green
    Write-Host "`nDouble-click the .msi (or .exe) to install." -ForegroundColor Yellow
    Write-Host "After install, find 'PrintShop Companion' in the Start menu.`n" -ForegroundColor Yellow
}
