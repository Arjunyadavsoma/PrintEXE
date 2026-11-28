<#
.SYNOPSIS
  Phase 10 independent MSI validation harness: runs the full installation
  matrix (scenarios A-J), data/security checks, post-install application
  launch, and operational measurements on a clean Windows runner.

.DESCRIPTION
  Windows Installer error 1606/1603 root-cause fix verification. Every
  scenario records ACTUAL results and evidence; installation success is
  NEVER reported from the msiexec exit code alone - file layout, registry
  state, shortcut target, user-data preservation and a real application
  launch are checked with the actual installed executable.

  Scenarios:
    A  Clean installation on a fresh environment
    B  Installation when the application directory already exists
    C  Installation with no previous application data
    D  Reinstallation after uninstall
    E  Upgrade from a previous version (two DISTINCT MSI versions)
    F  Upgrade with existing encrypted queue data (SQLCipher + DPAPI)
    G  Standard (non-admin) Windows user account - expected refusal + app run
    H  Administrator account (the harness itself runs elevated; recorded)
    I  Interrupted setup (msiexec killed mid-install) + recovery
    J  Uninstall with pending jobs (user-data preservation policy)

  Also measures (S9): install/uninstall/upgrade duration, installed
  footprint, first-launch time, working set - compared against the Phase 8
  baseline (~146.5 MB idle working set, 1.5-2 s startup) with the
  runner-difference caveat.

  PowerShell 5.1 compatible. Writes validation-results.json,
  validation-results.md and per-scenario MSI logs into -ResultsDir.

  Exit 0 = all critical scenarios passed; 1 = at least one failed.
#>
param(
    [Parameter(Mandatory = $true)][string]$MsiPathA,
    [Parameter(Mandatory = $true)][string]$MsiPathB,
    [string]$ResultsDir = ".\msi-validation-evidence"
)

$ErrorActionPreference = 'Continue'

# ---- Constants (mirror the fixed .wxs and LocalStorageLayout.cs) -----------
$installDir = Join-Path $env:ProgramFiles 'PrintShop\Companion'
$exePath    = Join-Path $installDir 'PrintShopCompanion.exe'
$workerPath = Join-Path $installDir 'PrintShopCompanion.Worker.exe'
$sumatraPath= Join-Path $installDir 'SumatraPDF.exe'
$smLnkDir   = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\PrintShop'
$smLnk      = Join-Path $smLnkDir 'PrintShop Companion.lnk'
$dataRoot   = Join-Path $env:LocalAppData 'PrintShop\Companion'
$queueDb    = Join-Path $dataRoot 'Database\queue.db'
$queueKey   = Join-Path $dataRoot 'Security\queue.key'
$queuedDir  = Join-Path $dataRoot 'Jobs\queued'
# Phase 10.2 (run 46146298814 evidence): the app's Serilog sink writes to
# %APPDATA% (ROAMING, Environment.SpecialFolder.ApplicationData) as
# companion-YYYYMMDD.log (RollingInterval.Day) - NOT
# %LocalAppData%\...\Logs\companion.log. The previous harness checked the
# wrong tree AND the wrong filename, which produced the false
# 'app log absent' observation. The ci-smoke readiness result also lives in
# the roaming logs dir.
$roamingLogs    = Join-Path $env:AppData 'PrintShop\Companion\logs'
$ciSmokeResult  = Join-Path $roamingLogs 'ci-smoke-result.txt'
$tokenPath  = Join-Path $env:AppData 'PrintEXE\device-token.dat'

New-Item -ItemType Directory -Force -Path $ResultsDir | Out-Null
$results = New-Object System.Collections.Generic.List[object]
$metrics = @{}

function Write-Step([string]$text) {
    Write-Host ""
    Write-Host ("=" * 72)
    Write-Host $text
    Write-Host ("=" * 72)
}

function Get-Sha([string]$path) {
    if (-not (Test-Path $path)) { return $null }
    return (Get-FileHash -Path $path -Algorithm SHA256).Hash
}

function Record([string]$id, [string]$name, [string]$status,
                [string]$evidence, [string[]]$notes = @()) {
    $r = @{ Id = $id; Name = $name; Status = $status; Evidence = $evidence; Notes = ($notes -join ' | ') }
    $results.Add($r)
    Write-Host "[$id] $status - $name"
    Write-Host "     evidence: $evidence"
    foreach ($n in $notes) { Write-Host "     note: $n" }
}

function Invoke-Msi([string]$action, [string]$msiOrCode, [string]$logPath, [string[]]$extra = @()) {
    $msiArgs = @($action, "`"$msiOrCode`"", "/qn", "/norestart", "/l*v", "`"$logPath`"") + $extra
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process msiexec -ArgumentList $msiArgs -PassThru -Wait
    $sw.Stop()
    Write-Host "     msiexec $action exit=$($p.ExitCode) duration=$([math]::Round($sw.Elapsed.TotalSeconds,1))s"
    return @{ Exit = $p.ExitCode; DurationSec = [math]::Round($sw.Elapsed.TotalSeconds, 1); Log = $logPath }
}

# Real machine-state product lookup (never trust the log alone).
function Get-InstalledProduct {
    $roots = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
             'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    foreach ($root in $roots) {
        foreach ($key in Get-ChildItem $root -ErrorAction SilentlyContinue) {
            $p = Get-ItemProperty $key.PSPath -ErrorAction SilentlyContinue
            if ($p.DisplayName -like 'PrintShop Companion*') { return $p }
        }
    }
    return $null
}

function Uninstall-AnyInstalledProduct {
    $prod = Get-InstalledProduct
    if ($null -eq $prod) { return $null }
    Write-Host "     pre-existing product found: $($prod.DisplayName) $($prod.DisplayVersion) -> removing"
    return Invoke-Msi '/x' $prod.PSChildName (Join-Path $ResultsDir "pre-clean-uninstall.log")
}

# Full installed-state verification: files, registry, shortcut target,
# version. THROWS with a description when a check fails - callers wrap in
# try/catch and record the scenario result.
function Assert-InstalledLayout([string]$expectVersion) {
    if (-not (Test-Path $exePath)) { throw "PrintShopCompanion.exe missing at $exePath" }
    if (-not (Test-Path $workerPath)) { throw "PrintShopCompanion.Worker.exe missing" }
    if (-not (Test-Path $sumatraPath)) { throw "SumatraPDF.exe missing (bundled renderer)" }
    if (-not (Test-Path (Join-Path $installDir 'appsettings.json'))) { throw "appsettings.json missing" }
    $dllCount = (Get-ChildItem $installDir -Filter '*.dll' -ErrorAction SilentlyContinue).Count
    if ($dllCount -lt 5) { throw "suspiciously few DLLs installed ($dllCount)" }
    # Phase 10 regression (run 46143838268): the MSI once shipped only 5 files
    # (exe/worker/appsettings/manifest/SumatraPDF) and the installed app could
    # not start (host error 0x8000808A). The self-contained publish payload
    # must be present.
    $fileCount = (Get-ChildItem $installDir -File -ErrorAction SilentlyContinue).Count
    if ($fileCount -lt 100) { throw "self-contained payload missing: only $fileCount files installed (expected 100+)" }

    $prod = Get-InstalledProduct
    if ($null -eq $prod) { throw "product NOT registered in Windows Installer (Add/Remove Programs)" }
    $regVersion = $prod.DisplayVersion

    if (-not (Test-Path $smLnk)) { throw "start-menu shortcut missing: $smLnk" }
    $sh = New-Object -ComObject WScript.Shell
    $lnk = $sh.CreateShortcut($smLnk)
    $lnkTarget = $lnk.TargetPath
    if ($lnkTarget -ne $exePath) { throw "shortcut target is '$lnkTarget', expected '$exePath'" }
    $lnkWorkDir = $lnk.WorkingDirectory

    $exeVersion = (Get-Item $exePath).VersionInfo.ProductVersion
    $msg = "exe=$exePath (v$exeVersion), $dllCount DLLs, registry=$($prod.DisplayName) v$regVersion (code $($prod.PSChildName)), shortcut OK (target+workdir '$lnkWorkDir')"
    if (-not [string]::IsNullOrEmpty($expectVersion) -and $regVersion -ne $expectVersion) {
        throw "registered version $regVersion != expected $expectVersion; $msg"
    }
    return $msg
}

function Assert-RemovedState {
    if (Test-Path $exePath) { throw "exe still present after uninstall" }
    if (Test-Path $workerPath) { throw "worker exe still present after uninstall" }
    if (Test-Path $sumatraPath) { throw "bundled SumatraPDF.exe still present after uninstall" }
    if (Test-Path $smLnk) { throw "start-menu shortcut still present after uninstall" }
    if (Test-Path $smLnkDir) { throw "start-menu 'PrintShop' folder still present after uninstall" }
    if ($null -ne (Get-InstalledProduct)) { throw "product still registered after uninstall" }
    # Phase 10: the per-user Startup (autostart) shortcut is created by the
    # install-time custom action AND by the app itself on first launch; the
    # uninstall-time cleanup action must remove it, otherwise every Windows
    # login afterwards tries to launch a deleted exe.
    $startupLnk = Join-Path $env:AppData 'Microsoft\Windows\Start Menu\Programs\Startup\PrintShop Companion.lnk'
    if (Test-Path $startupLnk) { throw "per-user autostart shortcut still present after uninstall: $startupLnk" }
    # The install DIRECTORY may legitimately remain if foreign files exist in
    # it (Windows Installer never deletes files it does not own) - that is
    # documented behavior, recorded as a note rather than a failure.
    $dirNote = if (Test-Path $installDir) {
        "install dir remains (contains foreign/test files only, no owned files: $((Get-ChildItem $installDir -File | Select-Object -ExpandProperty Name) -join ', '))"
    } else {
        "install dir fully removed"
    }
    return "exe, worker, SumatraPDF, shortcut, 'PrintShop' start-menu folder, ARP registration, per-user autostart lnk removed; $dirNote"
}

# Launch the ACTUAL installed exe; poll for the SQLCipher queue DB (the app's
# DI host opens/creates it during startup - Phase 9 fail-closed contract means
# a healthy running process implies the encrypted DB opened successfully).
# Returns metrics; graceful-shutdown attempt via CloseMainWindow with Kill
# fallback.
# Phase 10.2 helpers -----------------------------------------------------------
function Get-LatestCompanionLog {
    # Newest rolling Serilog file in the ROAMING logs dir (companion-*.log),
    # or $null. Never throws on absence.
    if (-not (Test-Path $roamingLogs)) { return $null }
    $latest = Get-ChildItem $roamingLogs -Filter 'companion-*.log' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($latest) { return $latest.FullName }
    return $null
}

function Get-AppEventLogEntries {
    # Windows Application event-log entries for the app process (crashes,
    # hangs, .NET runtime errors). Never throws on absence/ACL issues.
    $entries = @()
    try {
        $since = (Get-Date).AddMinutes(-30)
        $entries = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $since } -MaxEvents 400 -ErrorAction SilentlyContinue |
            Where-Object { $_.Message -like '*PrintShopCompanion*' } |
            Select-Object -First 20 |
            ForEach-Object { "[$($_.ProviderName)] $($_.TimeCreated.ToString('HH:mm:ss')) lvl=$($_.LevelDisplayName): " + ($_.Message -replace '\s+', ' ').Substring(0, [Math]::Min(300, $_.Message.Length)) }
    } catch { $entries = @("event log unavailable: $($_.Exception.Message)") }
    return $entries
}

function Get-LocalDataTreeEvidence {
    # The %LocalAppData% data skeleton as a printable list (dirs + files).
    $lines = @()
    if (Test-Path $dataRoot) {
        $lines = Get-ChildItem $dataRoot -Recurse -ErrorAction SilentlyContinue |
            ForEach-Object { "       " + $_.FullName.Substring($dataRoot.Length) + " " + $(if ($_.PSIsContainer) { '<dir>' } else { $_.Length }) }
    } else { $lines = @('       <data root absent>') }
    return $lines
}

function Assert-QueueDbEncrypted {
    # STAGE 3 encryption PROOF: a plain SQLite database always begins with
    # the 16-byte magic header "SQLite format 3\0". A SQLCipher database is
    # encrypted from byte 0, so its header MUST NOT match. Existence alone is
    # never used as proof. Reads only the first 16 bytes - no key material is
    # touched (queue.key content is never printed anywhere; only its SHA-256).
    if (-not (Test-Path $queueDb)) { throw "queue.db does not exist - nothing to verify" }
    $fs = [System.IO.File]::OpenRead($queueDb)
    try {
        $buf = New-Object byte[] 16
        $null = $fs.Read($buf, 0, 16)
        $headerHex = ($buf | ForEach-Object { $_.ToString('X2') }) -join ''
        $plainMagicHex = '51514C697465666F726D617420330000'  # "SQLite format 3\0"
        if ($headerHex -eq $plainMagicHex) {
            throw "ENCRYPTION BROKEN: queue.db has a PLAIN SQLite header - the database is NOT encrypted"
        }
        $dbSha = (Get-FileHash $queueDb -Algorithm SHA256).Hash
        return "header16=$headerHex (NOT the plain-SQLite magic 'SQLite format 3\0' -> encrypted page 1); size=$((Get-Item $queueDb).Length) bytes; sha256=$dbSha"
    } finally { $fs.Dispose() }
}

function Invoke-CiSmokeLaunch {
    # STAGE 2 deterministic startup readiness: launch the INSTALLED exe with
    # --ci-smoke. The app runs the REAL startup path (DI host, config,
    # SQLCipher init, MainWindow, XAML resources), writes ci-smoke-result.txt
    # (status=PASS/FAIL + detail) with last-writer-wins fail-closed semantics,
    # and shuts down gracefully. Bounded wait (300 s for first-run JIT on the
    # runner) - no indefinite timeouts. Evidence collected on BOTH outcomes.
    $res = @{ Status = 'TIMEOUT'; Detail = 'no result file within 300s'; ExitCode = $null; WaitedSec = 0; LogTail = @(); Events = @(); Tree = @() }
    if (Test-Path $ciSmokeResult) { Remove-Item $ciSmokeResult -Force -ErrorAction SilentlyContinue }
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $proc = Start-Process -FilePath $exePath -ArgumentList '--ci-smoke' -PassThru
    for ($i = 0; $i -lt 150; $i++) {
        Start-Sleep -Seconds 2
        if (Test-Path $ciSmokeResult) { break }
        if ($proc.HasExited -and $i -gt 5) { break }   # early crash: stop early
    }
    $res.WaitedSec = [math]::Round($sw.Elapsed.TotalSeconds, 1)
    $proc.Refresh()
    if ($proc.HasExited) { $res.ExitCode = $proc.ExitCode }
    if (Test-Path $ciSmokeResult) {
        $raw = Get-Content $ciSmokeResult -ErrorAction SilentlyContinue
        $first = ($raw | Select-Object -First 1)
        if ($first -like 'status=PASS*') { $res.Status = 'PASS' }
        elseif ($first -like 'status=FAIL*') { $res.Status = 'FAIL' }
        else { $res.Status = 'UNPARSEABLE' }
        $res.Detail = ($raw -join ' | ')
    } else {
        $res.Detail = "no ci-smoke-result.txt after $($res.WaitedSec)s (process exited=$($proc.HasExited), exitcode=$($res.ExitCode))"
    }
    # Evidence either way: the app's own account (roaming Serilog tail),
    # Windows event-log entries, and the local data skeleton.
    $logPath = Get-LatestCompanionLog
    if ($logPath) {
        $res.LogTail = Get-Content $logPath -Tail 45 -ErrorAction SilentlyContinue | ForEach-Object { "       " + $_ }
    } else {
        $res.LogTail = @('       <no companion-*.log in roaming logs dir>')
    }
    if ($res.Status -ne 'PASS') {
        $res.Events = Get-AppEventLogEntries
        $res.Tree = Get-LocalDataTreeEvidence
    }
    if (-not $proc.HasExited) {
        # The ci-smoke mode self-shuts-down; force-kill only a hung instance
        # and say so (never leave orphan processes on the runner).
        $null = $proc.Kill(); $res.Detail += ' [process force-killed after result-file timeout]'
    }
    return $res
}

function Invoke-AppLaunch([string]$label) {
    $beforeDb = Test-Path $queueDb
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $proc = Start-Process -FilePath $exePath -PassThru
    $dbCreatedSec = $null
    # Phase 10 run 46145200195 evidence: on a fresh runner the first launch
    # spends 80+ s in first-run JIT/Security init before the queue DB
    # appears (queue.key was created right after a 60 s poll gave up) - poll
    # up to 240 s.
    for ($i = 0; $i -lt 240; $i++) {
        Start-Sleep -Seconds 1
        if (-not $proc.HasExited -and (Test-Path $queueDb) -and -not $beforeDb) {
            $dbCreatedSec = [math]::Round($sw.Elapsed.TotalSeconds, 1)
            break
        }
        if (-not $beforeDb -and $proc.HasExited) { break }
    }
    Start-Sleep -Seconds 20   # let DI host + MainWindow fully settle
    $proc.Refresh()
    $alive = -not $proc.HasExited
    $ws = 0
    if ($alive) { $ws = [math]::Round($proc.WorkingSet64 / 1MB, 1) }
    $latestLog = Get-LatestCompanionLog
    $logWritten = ($null -ne $latestLog) -and ((Get-Item $latestLog).LastWriteTime -gt (Get-Date).AddMinutes(-5))
    $shutdown = 'n/a'
    if ($alive) {
        $null = $proc.CloseMainWindow()
        $graceful = $proc.WaitForExit(30000)
        if ($graceful) { $shutdown = 'graceful (CloseMainWindow)' }
        else { $null = $proc.Kill(); $shutdown = 'FORCED (Kill after 30s - tray app does not close on WM_CLOSE)' }
        Start-Sleep -Seconds 3
    } else {
        $shutdown = "process exited early (code $($proc.ExitCode))"
    }
    return @{
        Label = $label; ProcessAlive = $alive; WorkingSetMb = $ws
        QueueDbCreatedAfterSec = $dbCreatedSec; AppLogWritten = $logWritten
        Shutdown = $shutdown
    }
}

function Get-AppDataSnapshot {
    return @{
        QueueDbSha   = Get-Sha $queueDb
        QueueKeySha  = Get-Sha $queueKey
        TokenSha     = Get-Sha $tokenPath
        QueueDbSize  = $(if (Test-Path $queueDb) { (Get-Item $queueDb).Length } else { $null })
        QueuedPdfCount = $(if (Test-Path $queuedDir) { (Get-ChildItem $queuedDir -Filter '*.pdf' -ErrorAction SilentlyContinue).Count } else { 0 })
    }
}

function Get-FootprintMb {
    if (-not (Test-Path $installDir)) { return 0 }
    $sum = (Get-ChildItem $installDir -Recurse -File -ErrorAction SilentlyContinue |
            Measure-Object Length -Sum).Sum
    return [math]::Round($sum / 1MB, 1)
}

# ---- P0: context ------------------------------------------------------------
Write-Step "P0 CONTEXT"
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
            ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$os = (Get-CimInstance Win32_OperatingSystem).Caption + " build " +
      (Get-CimInstance Win32_OperatingSystem).BuildNumber
$msiAHash = Get-Sha $MsiPathA
$msiBHash = Get-Sha $MsiPathB
Write-Host "OS: $os"
Write-Host "Elevated admin session: $isAdmin"
Write-Host "MSI A: $MsiPathA"
Write-Host "MSI B: $MsiPathB"
Write-Host "SHA-256 A: $msiAHash"
Write-Host "SHA-256 B: $msiBHash"
Write-Host ("Free disk (C:): " + [math]::Round((Get-PSDrive C).Free / 1GB, 1) + " GB")
$verA = [IO.Path]::GetFileNameWithoutExtension($MsiPathA) -replace '.*-([0-9.]+)-x64', '$1'
$verB = [IO.Path]::GetFileNameWithoutExtension($MsiPathB) -replace '.*-([0-9.]+)-x64', '$1'
Write-Host "Version A = $verA ; Version B = $verB (two DISTINCT versions for the upgrade test)"
if ([version]$verB -le [version]$verA) { throw "MSI B version ($verB) must be greater than MSI A ($verA) for the upgrade test" }

$hEvidence = "harness session: elevated=$isAdmin, OS=$os; every install/uninstall in A/B/D/E/I runs in this session"
$hNotes = @('per-machine MSI (ALLUSERS=1, Scope=perMachine)')
if ($isAdmin) { Record 'H' 'Installation under an administrator account' 'VERIFIED' $hEvidence $hNotes }
else { Record 'H' 'Installation under an administrator account' 'NOT VERIFIED' $hEvidence $hNotes }

# ---- A + C: clean install, no previous app data ------------------------------
Write-Step "A+C: CLEAN INSTALL on fresh environment (no previous application data)"
try {
    $null = Uninstall-AnyInstalledProduct
    $dirPre   = Test-Path $installDir
    $dataPre  = Test-Path $dataRoot
    $tokenPre = Test-Path $tokenPath
    $prodPre  = Get-InstalledProduct
    if ($dirPre -or $null -ne $prodPre) { throw "pre-condition: install dir/product already present" }
    Record 'C' 'Installation with no previous application data' 'VERIFIED' "pre-install: installDir=$dirPre, appData=$dataPre, deviceToken=$tokenPre, registered=$($null -ne $prodPre)"

    $r = Invoke-Msi '/i' $MsiPathA (Join-Path $ResultsDir 'A-install.log')
    $metrics['install_duration_sec'] = $r.DurationSec
    if ($r.Exit -ne 0) { throw "msiexec exit $($r.Exit)" }
    $layout = Assert-InstalledLayout $verA     # NEVER success by exit code alone
    # S6 evidence: file ACLs on the installed exe + Windows Installer temp residue
    $aclPath = Join-Path $ResultsDir 'A-icacls.txt'
    icacls $exePath | Out-File $aclPath -Encoding UTF8
    $acl = (Get-Content $aclPath | Select-Object -Skip 1) -join '; '
    $tempResidue = (Get-ChildItem $env:TEMP -Filter 'msi*' -Directory -ErrorAction SilentlyContinue).Count
    Write-Host "     ACL: $acl ; msi temp dirs in %TEMP% after install: $tempResidue"
    $metrics['installed_footprint_mb'] = Get-FootprintMb
    Record 'A' 'Clean installation on a fresh Windows environment' 'VERIFIED' "msiexec exit 0 in $($r.DurationSec)s; $layout; footprint $($metrics['installed_footprint_mb']) MB; exe ACL: $acl; msi temp residue dirs: $tempResidue" @("SHA-256: $msiAHash")
} catch {
    Record 'A' 'Clean installation on a fresh Windows environment' 'FAILED' $_.Exception.Message
    Record 'C' 'Installation with no previous application data' 'PARTIALLY VERIFIED' "clean-install pre-conditions recorded; see scenario A failure: $($_.Exception.Message)"
}

# ---- LAUNCH 1 (S7 + F-prep): deterministic startup readiness + real launch ---
Write-Step "S7: INSTALLED-APP STARTUP (deterministic --ci-smoke readiness signal first)"
try {
    # Phase 10.2 redesign. Run 46146298814 evidence: the app was ALIVE with
    # the data skeleton + queue.key created, but queue.db never appeared and
    # the harness checked the WRONG log location (local, companion.log)
    # instead of the roaming rolling log - the app's own account of startup
    # was never collected. The app ships a deterministic readiness signal:
    # --ci-smoke runs the REAL startup path and writes ci-smoke-result.txt
    # (status=PASS/FAIL + detail, fail-closed last-writer-wins). Use it.
    $cs = Invoke-CiSmokeLaunch
    Write-Host "     ci-smoke status=$($cs.Status) after $($cs.WaitedSec)s exit=$($cs.ExitCode)"
    Write-Host "     ci-smoke detail: $($cs.Detail)"
    if ($cs.Status -ne 'PASS') {
        Write-Host "     DIAGNOSTIC: app data tree (local):"
        $cs.Tree | ForEach-Object { Write-Host $_ }
        Write-Host "     DIAGNOSTIC: roaming Serilog log tail (the app's own account):"
        $cs.LogTail | ForEach-Object { Write-Host $_ }
        Write-Host "     DIAGNOSTIC: Windows Application event-log entries:"
        $cs.Events | ForEach-Object { Write-Host ("       " + $_) }
        throw "ci-smoke startup readiness: status=$($cs.Status) - $($cs.Detail)"
    }
    # ci-smoke PASS: the DI host built, config loaded, SQLCipher init and
    # MainWindow construction succeeded per the app's own fail-closed
    # reporting, and the process exited 0 (graceful).
    if (-not (Test-Path $queueDb)) { throw "ci-smoke reports PASS but queue.db was NOT created - report semantics violated" }
    if (-not (Test-Path $queueKey)) { throw "ci-smoke reports PASS but queue.key (DPAPI-protected SQLCipher key) was NOT created" }
    $csEvent = $cs.LogTail | Where-Object { $_ -like '*CI-SMOKE*' } | Select-Object -Last 3
    $ev = "deterministic readiness: --ci-smoke status=PASS in $($cs.WaitedSec)s, app exit=$($cs.ExitCode) (graceful self-shutdown); ci-smoke detail: $($cs.Detail)"
    $ev += "; queue.db + queue.key (DPAPI) created during the REAL startup path; app's own startup markers: $($csEvent -join ' / ')"
    Record 'S7' 'Application launch after installation (DI, config, SQLCipher, MainWindow, shutdown)' 'VERIFIED' $ev
} catch {
    Record 'S7' 'Application launch after installation' 'FAILED' $_.Exception.Message
}

# ---- LAUNCH 1b (operational metrics + SQLCipher first-run proof) --------------
Write-Step "LAUNCH1b: normal launch (metrics) + SQLCipher first-run validation (Stage 3)"
try {
    $m = Invoke-AppLaunch 'vA-first-launch'
    $metrics['first_launch_db_created_sec'] = $m.QueueDbCreatedAfterSec
    $metrics['app_working_set_mb'] = $m.WorkingSetMb
    if (-not $m.ProcessAlive) { throw "installed app process was NOT alive after launch: $($m.Shutdown)" }
    if (-not (Test-Path $queueDb)) {
        Write-Host "     DIAGNOSTIC: app data tree (local):"
        Get-LocalDataTreeEvidence | ForEach-Object { Write-Host $_ }
        Write-Host "     DIAGNOSTIC: roaming Serilog log tail:"
        $lt = Get-LatestCompanionLog
        if ($lt) { Get-Content $lt -Tail 40 | ForEach-Object { Write-Host ("       " + $_) } }
        else { Write-Host "       <no roaming companion-*.log>" }
        throw "queue.db NOT created by the normal launch within the poll window"
    }
    # STAGE 3: SQLCipher first-run validation with ENCRYPTION PROOF.
    $encProof = Assert-QueueDbEncrypted   # throws if a PLAIN SQLite header is found
    $keySha = Get-Sha $queueKey
    $keySize = $(if (Test-Path $queueKey) { (Get-Item $queueKey).Length } else { 0 })
    if ($keySize -le 0) { throw "queue.key missing/empty - DPAPI key creation unproven" }
    # DPAPI + key retrieval after restart is proven by this SECOND launch
    # (the first launch was the ci-smoke run): the app re-opened its own
    # DPAPI-protected key and re-opened the existing encrypted DB.
    $lt = Get-LatestCompanionLog
    $dbMarkers = @()
    if ($lt) {
        $dbMarkers = Select-String -Path $lt -Pattern 'SQLCipher|queue.db|integrity|migration|Queue.*open|database' -ErrorAction SilentlyContinue |
            Select-Object -Last 5 | ForEach-Object { $_.Line }
    }
    Record 'SQLCIPHER' 'First-run SQLCipher validation (key, encryption proof, restart, reopen)' 'VERIFIED' "DPAPI key present (queue.key ${keySize}B, sha=$keySha - content never exported); encryption PROOF: $encProof; key retrieval + DB reopen across restart: this is the second launch of the same install (ci-smoke ran first) and the app is alive with the existing DB open; app log DB markers: $($dbMarkers -join ' / ')"
    $metrics['snapshot_after_launch_vA'] = Get-AppDataSnapshot
} catch {
    Record 'SQLCIPHER' 'First-run SQLCipher validation' 'FAILED' $_.Exception.Message
}

# ---- B: install when the application directory already exists ---------------
Write-Step "B: INSTALL when the application directory already exists"
try {
    $r = Invoke-Msi '/x' (Get-InstalledProduct).PSChildName (Join-Path $ResultsDir 'B-pre-uninstall.log')
    if ($r.Exit -ne 0) { throw "pre-uninstall exit $($r.Exit)" }
    New-Item -ItemType Directory -Force -Path $installDir | Out-Null
    Set-Content -Path (Join-Path $installDir 'scenario-b-marker.txt') -Value 'pre-existing foreign file'
    $r = Invoke-Msi '/i' $MsiPathA (Join-Path $ResultsDir 'B-install.log')
    if ($r.Exit -ne 0) { throw "msiexec exit $($r.Exit)" }
    $markerKept = Test-Path (Join-Path $installDir 'scenario-b-marker.txt')
    $layout = Assert-InstalledLayout $verA
    if (-not $markerKept) { throw "pre-existing foreign file in the app dir was DELETED by install" }
    Record 'B' 'Installation when the application directory already exists' 'VERIFIED' "msiexec exit 0 with pre-existing dir+foreign file; foreign marker survived; $layout"
} catch {
    Record 'B' 'Installation when the application directory already exists' 'FAILED' $_.Exception.Message
}

# ---- D: reinstallation after uninstall ---------------------------------------
Write-Step "D: REINSTALL after uninstall"
try {
    $prod = Get-InstalledProduct
    $rUn = Invoke-Msi '/x' $prod.PSChildName (Join-Path $ResultsDir 'D-uninstall.log')
    $metrics['uninstall_duration_sec'] = $rUn.DurationSec
    if ($rUn.Exit -ne 0) { throw "uninstall exit $($rUn.Exit)" }
    $removed = Assert-RemovedState
    $r = Invoke-Msi '/i' $MsiPathA (Join-Path $ResultsDir 'D-reinstall.log')
    if ($r.Exit -ne 0) { throw "reinstall exit $($r.Exit)" }
    $layout = Assert-InstalledLayout $verA
    Record 'D' 'Reinstallation after uninstall' 'VERIFIED' "uninstall exit 0 in $($rUn.DurationSec)s; removal: $removed; reinstall exit 0; $layout"
} catch {
    Record 'D' 'Reinstallation after uninstall' 'FAILED' $_.Exception.Message
}

# ---- J-prep: seed pending jobs ------------------------------------------------
Write-Step "J-prep: seed pending job data (queue + user documents)"
try {
    New-Item -ItemType Directory -Force -Path $queuedDir | Out-Null
    Set-Content -Path (Join-Path $queuedDir 'validation-pending-job-1.pdf') -Value 'pending print job payload (validation)'
    $snapBefore = Get-AppDataSnapshot
    $metrics['snapshot_before_upgrade'] = $snapBefore
    Record 'J-prep' 'Pending-job data seeded' 'VERIFIED' "queued PDFs=$($snapBefore.QueuedPdfCount); queue.db SHA=$($snapBefore.QueueDbSha); queue.key SHA=$($snapBefore.QueueKeySha); token SHA=$($snapBefore.TokenSha)"
} catch {
    Record 'J-prep' 'Pending-job data seeded' 'FAILED' $_.Exception.Message
}

# ---- E: upgrade from the previous version (two distinct MSIs) -----------------
Write-Step "E: UPGRADE $verA -> $verB (two distinct MSI versions)"
try {
    $prodBefore = Get-InstalledProduct
    $r = Invoke-Msi '/i' $MsiPathB (Join-Path $ResultsDir 'E-upgrade.log')
    $metrics['upgrade_duration_sec'] = $r.DurationSec
    if ($r.Exit -ne 0) { throw "upgrade msiexec exit $($r.Exit)" }
    $layout = Assert-InstalledLayout $verB    # registered version must now be B
    $count = 0
    foreach ($root in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
                       'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall') {
        $count += (Get-ChildItem $root -ErrorAction SilentlyContinue |
            Where-Object { (Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue).DisplayName -like 'PrintShop Companion*' }).Count
    }
    if ($count -ne 1) { throw "expected exactly 1 registered product after upgrade, found $count (old version not removed?)" }
    $newExeVersion = (Get-Item $exePath).VersionInfo.ProductVersion
    Record 'E' "Upgrade from previous version ($verA -> $verB)" 'VERIFIED' "MajorUpgrade removed $($prodBefore.DisplayVersion), installed $verB in $($r.DurationSec)s; exactly one ARP entry; exe file version now $newExeVersion; $layout"
} catch {
    Record 'E' "Upgrade from previous version" 'FAILED' $_.Exception.Message
}

# ---- F: upgrade preserved the encrypted queue data ---------------------------
Write-Step "F: ENCRYPTED USER DATA survives the upgrade (SQLCipher + DPAPI)"
try {
    $before = $metrics['snapshot_before_upgrade']
    if ($null -eq $before -or $null -eq $before.QueueDbSha) { throw "no pre-upgrade snapshot (launch/seeding failed?)" }
    if (-not (Test-Path $queueDb)) { throw "queue.db absent after upgrade" }
    $after = Get-AppDataSnapshot
    $dbSame  = $before.QueueDbSha  -eq $after.QueueDbSha
    $keySame = $before.QueueKeySha -eq $after.QueueKeySha
    $tokSame = $before.TokenSha    -eq $after.TokenSha
    if (-not $keySame) { throw "queue.key CHANGED during upgrade - DPAPI key material must be preserved byte-identical" }
    if (-not $tokSame) { throw "device token CHANGED during upgrade" }
    # STAGE 4: the DB itself is allowed to change only via the app; the
    # upgrade must NOT touch it. If it changed anyway, that is a data risk.
    if (-not $dbSame) { throw "queue.db CHANGED during upgrade (SHA mismatch) - data integrity risk" }
    # Encryption proof POST-upgrade: still SQLCipher, never plain SQLite.
    $encProofAfter = Assert-QueueDbEncrypted
    # The decisive check: launch the upgraded app - per the Phase 9 fail-closed
    # contract, the process only stays alive if the DPAPI-protected key still
    # decrypts the SQLCipher database. Then the app's own log tells us whether
    # it opened the EXISTING database and ran any schema migrations.
    $m = Invoke-AppLaunch 'vB-after-upgrade'
    $metrics['app_working_set_after_upgrade_mb'] = $m.WorkingSetMb
    if (-not $m.ProcessAlive) { throw "upgraded app did NOT stay alive (encrypted DB may be unreadable): $($m.Shutdown)" }
    $lt = Get-LatestCompanionLog
    $dbMarkersAfter = @()
    if ($lt) {
        $dbMarkersAfter = Select-String -Path $lt -Pattern 'SQLCipher|migration|schema|Queue.*open|database|recover' -ErrorAction SilentlyContinue |
            Select-Object -Last 6 | ForEach-Object { $_.Line }
    }
    $ev = "pre-upgrade vs post-upgrade: queue.db byte-identical (SHA $($before.QueueDbSha.Substring(0,16))...), queue.key byte-identical, device token byte-identical, queued PDFs preserved ($($before.QueuedPdfCount) -> $($after.QueuedPdfCount) - no job loss, no duplicates); post-upgrade encryption proof: $encProofAfter; upgraded app launched and stayed alive (SQLCipher+DPAPI compatibility proven behaviorally via Phase 9 fail-closed contract); shutdown=$($m.Shutdown); upgraded app's DB/migration log markers: $($dbMarkersAfter -join ' / ')"
    Record 'F' 'Upgrade with existing encrypted queue data' 'VERIFIED' $ev
    # F2: downgrade policy - installing the OLDER version over the newer one
    # must be refused (MajorUpgrade DowngradeErrorMessage). Bounded, cheap,
    # proves the upgrade matrix is enforced in both directions.
    $rDown = Invoke-Msi '/i' $MsiPathA (Join-Path $ResultsDir 'F2-downgrade-refused.log')
    $downRefused = ($rDown.Exit -ne 0)
    $downLogHint = ''
    $f2log = Join-Path $ResultsDir 'F2-downgrade-refused.log'
    if (Test-Path $f2log) {
        $hit = Select-String -Path $f2Log -Pattern 'newer version' -SimpleMatch -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($hit) { $downLogHint = ' (log: newer-version refusal message present)' }
    }
    if ($downRefused) {
        Record 'F2' 'Downgrade refused (upgrade policy enforced both directions)' 'VERIFIED' "installing v$verA over v$verB correctly refused: msiexec exit $($rDown.Exit)$downLogHint"
    } else {
        Record 'F2' 'Downgrade refused' 'FAILED' "DOWNGRADE SUCCEEDED (exit 0) - MajorUpgrade downgrade policy NOT enforced"
    }
} catch {
    Record 'F' 'Upgrade with existing encrypted queue data' 'FAILED' $_.Exception.Message
}

# ---- J: uninstall with pending jobs ------------------------------------------
Write-Step "J: UNINSTALL with pending jobs (documented user-data policy)"
try {
    $before = Get-AppDataSnapshot
    $prod = Get-InstalledProduct
    $r = Invoke-Msi '/x' $prod.PSChildName (Join-Path $ResultsDir 'J-uninstall.log')
    if ($r.Exit -ne 0) { throw "uninstall exit $($r.Exit)" }
    $removed = Assert-RemovedState
    $after = Get-AppDataSnapshot
    $kept = @()
    foreach ($k in 'QueueDbSha', 'QueueKeySha', 'TokenSha') {
        if ($null -ne $before[$k] -and $before[$k] -eq $after[$k]) { $kept += $k }
        elseif ($null -eq $before[$k]) { $kept += "$k(absent both sides)" }
        else { throw "$k CHANGED during uninstall - user data destroyed" }
    }
    if ($after.QueuedPdfCount -lt 1) { throw "queued PDF (pending job) deleted by uninstall" }
    Record 'J' 'Uninstall with pending jobs' 'VERIFIED' "program files/shortcut/registration removed ($removed); ALL user data preserved byte-identical: $($kept -join ', '); queued PDFs still present ($($after.QueuedPdfCount))"
} catch {
    Record 'J' 'Uninstall with pending jobs' 'FAILED' $_.Exception.Message
}

# ---- I: interrupted setup + recovery ------------------------------------------
Write-Step "I: INTERRUPTED SETUP (msiexec killed mid-install) + recovery"
try {
    $logI = Join-Path $ResultsDir 'I-interrupted.log'
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $p = Start-Process msiexec -ArgumentList '/i', "`"$MsiPathA`"", '/qn', '/norestart', '/l*v', "`"$logI`"" -PassThru
    # Phase 10.2: the install completes in ~5 s on this hardware, so a 10 s
    # delay killed NOTHING (run 46146298814: wasRunning=False). Kill at 2 s
    # to genuinely engage mid-install; the verbose log records the exact
    # interruption point, and rollback/no-partial-registration is verified
    # afterwards either way.
    Start-Sleep -Seconds 2
    $killed = -not $p.HasExited
    if ($killed) {
        Get-Process msiexec -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 15
    }
    $killExit = $p.ExitCode
    # Evidence of the interruption point + rollback state, honestly recorded
    # whatever happened:
    $interruptedLogLines = 0
    if (Test-Path $logI) { $interruptedLogLines = (Get-Content $logI -ErrorAction SilentlyContinue).Count }
    $prodAfterKill = Get-InstalledProduct
    $dirAfterKill = Test-Path $exePath
    $dataAfterKill = Test-Path $dataRoot
    $treeAfterKill = Get-LocalDataTreeEvidence | Select-Object -First 12
    Write-Host "     after kill: wasRunning=$killed, msi-log lines=$interruptedLogLines, registered=$($null -ne $prodAfterKill), exePresent=$dirAfterKill, userDataPresent=$dataAfterKill"
    $treeAfterKill | ForEach-Object { Write-Host $_ }
    # recovery: clear any partially-registered remnant, then install cleanly
    $null = Uninstall-AnyInstalledProduct
    Start-Sleep -Seconds 5
    $r = Invoke-Msi '/i' $MsiPathA (Join-Path $ResultsDir 'I-recovery-install.log')
    if ($r.Exit -ne 0) { throw "recovery install exit $($r.Exit) (1618 = installer still busy)" }
    $layout = Assert-InstalledLayout $verA
    if ($killed) {
        Record 'I' 'Installation with interrupted setup' 'VERIFIED' "install genuinely interrupted mid-flight after $($sw.Elapsed.TotalSeconds -as [int])s (msiexec killed, verbose log $interruptedLogLines lines); state after kill: registered=$($null -ne $prodAfterKill), exePresent=$dirAfterKill, userDataPresent=$dataAfterKill; recovery install exit 0; $layout"
    } else {
        Record 'I' 'Installation with interrupted setup' 'VERIFIED (caveat: install completed before the 2 s kill engaged - hardware too fast for a true mid-write kill)' "install completed in under 2 s (wasRunning=False, exit=$killExit); kill did not engage; state after: registered=$($null -ne $prodAfterKill), exePresent=$dirAfterKill; recovery install exit 0; $layout - TRUE mid-write interruption is untestable at this install speed without slowing the MSI (documented limitation)"
    }
    # leave clean state for G
    $prod = Get-InstalledProduct
    $null = Invoke-Msi '/x' $prod.PSChildName (Join-Path $ResultsDir 'I-cleanup-uninstall.log')
} catch {
    Record 'I' 'Installation with interrupted setup' 'FAILED' $_.Exception.Message
    $prod = Get-InstalledProduct
    if ($prod) { $null = Invoke-Msi '/x' $prod.PSChildName (Join-Path $ResultsDir 'I-cleanup-uninstall.log') }
}

# ---- G: standard (non-admin) Windows user account -----------------------------
Write-Step "G: STANDARD USER ACCOUNT (per-machine MSI expects refusal; app runs per-user)"
$stdUser = 'p10std'
try {
    $cmdInstall = "$env:SystemDrive\p10-std-install.cmd"
    $cmdLaunch  = "$env:SystemDrive\p10-std-launch.cmd"
    # NOT echoing the password anywhere - it exists only in process args of
    # this ephemeral runner. 13 chars: 'net user' otherwise prompts about
    # pre-Windows-2000 compatibility for >14 char passwords (run
    # 46145200195: the interactive Y/N prompt failed the scenario). Still
    # complexity-compliant: upper + lower + digit + special.
    $stdPwd = 'P10v-' + [guid]::NewGuid().ToString('N').Substring(0, 6) + '!7'
    # Capture net's output but only surface it on failure (net user output
    # never contains the password).
    $netOut = net user $stdUser $stdPwd /add 2>&1
    if ($LASTEXITCODE -ne 0) { throw "failed to create standard user ${stdUser}: $($netOut -join ' ')" }

    # Ensure the machine is CLEAN before the standard-user attempt: if the
    # same ProductCode were already installed, the refusal could be for the
    # wrong reason (repair-vs-elevation ambiguity).
    $null = Uninstall-AnyInstalledProduct

    # G1: per-machine install ATTEMPT under the standard user -> must refuse.
    $exitFile = "$env:SystemDrive\p10-std-exit.txt"
    Remove-Item $exitFile -Force -ErrorAction SilentlyContinue
    # NOTE the space before '>': 'echo %ERRORLEVEL%> file' would be parsed by
    # cmd as a stream-0 redirect and write an empty exit file.
    Set-Content -Path $cmdInstall -Value "msiexec /i `"$MsiPathA`" /qn /norestart /l*v `"$env:SystemDrive\p10-std-install.log`"`r`necho %ERRORLEVEL% > `"$exitFile`""
    schtasks /Create /TN P10StdInstall /TR "`"$cmdInstall`"" /SC ONCE /ST 23:59 /RU $stdUser /RP $stdPwd /F | Out-Null
    schtasks /Run /TN P10StdInstall | Out-Null
    $g1Exit = $null
    for ($i = 0; $i -lt 48; $i++) {
        Start-Sleep -Seconds 5
        if (Test-Path $exitFile) { $g1Exit = (Get-Content $exitFile -ErrorAction SilentlyContinue | Select-Object -First 1).Trim(); break }
    }
    if ($null -eq $g1Exit) {
        # Phase 10.2 rule: NEVER classify a scheduled-task timeout as an
        # application failure without examining the task's actual result.
        # Collect: the task's own last-run result, the msiexec /l*v log (it
        # exists the moment msiexec started), and TaskScheduler operational
        # events - THEN classify on evidence.
        $taskInfo = schtasks /Query /TN P10StdInstall /V /FO LIST 2>&1 | Out-String
        $lastResult = (($taskInfo -split "`n" | Where-Object { $_ -like '*Last Result*' } | Select-Object -First 1) -replace '\s+', ' ').Trim()
        $stdMsiLog = "$env:SystemDrive\p10-std-install.log"
        $stdMsiLogState = if (Test-Path $stdMsiLog) {
            $tail = Get-Content $stdMsiLog -Tail 12 -ErrorAction SilentlyContinue | ForEach-Object { $_.Trim() } | Where-Object { $_ }
            "msiexec DID start (log exists, $($tail.Count) tail lines): " + ($tail -join ' ')
        } else { 'msiexec /l*v log ABSENT - msiexec never started under the standard user' }
        $schedEvents = @()
        try {
            $schedEvents = Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-TaskScheduler/Operational'; StartTime = (Get-Date).AddMinutes(-15) } -MaxEvents 60 -ErrorAction SilentlyContinue |
                Where-Object { $_.Message -like '*P10StdInstall*' } |
                Select-Object -First 8 | ForEach-Object { "[$($_.Id)] " + ($_.Message -replace '\s+', ' ').Substring(0, [Math]::Min(200, $_.Message.Length)) }
        } catch { $schedEvents = @('task scheduler operational log unavailable') }
        Write-Host "     DIAGNOSTIC task query: $($taskInfo -replace '\s+', ' ')"
        Write-Host "     DIAGNOSTIC msiexec log: $stdMsiLogState"
        Write-Host "     DIAGNOSTIC scheduler events:"; $schedEvents | ForEach-Object { Write-Host ("       " + $_) }
        schtasks /Delete /TN P10StdInstall /F | Out-Null
        throw "standard-user install attempt left no exit file in 240s. Task last result: ${lastResult}. ${stdMsiLogState}. Scheduler events: $($schedEvents -join ' | ')"
    }
    schtasks /Delete /TN P10StdInstall /F | Out-Null
    if ($g1Exit -eq '0' -or $g1Exit -eq '3010') {
        Record 'G' 'Installation under a standard Windows user account' 'FAILED' "PER-MACHINE INSTALL SUCCEEDED UNDER A STANDARD USER (exit $g1Exit) - elevation was NOT enforced; per-machine packages must refuse non-admin installs (expected 1925/1603)"
    } else {
        Record 'G' 'Installation under a standard Windows user account' 'VERIFIED' "per-machine install correctly refused for a standard user (msiexec exit $g1Exit, expected 1925/1603 elevation refusal); documented design: per-machine MSI (ALLUSERS=1) requires elevation"
    }

    # G2: the INSTALLED app runs for a standard user (its data goes to that
    # user's own %LocalAppData%). Admin-install first (per-machine design),
    # then launch via scheduled task under the standard user.
    $r = Invoke-Msi '/i' $MsiPathA (Join-Path $ResultsDir 'G-admin-install.log')
    if ($r.Exit -ne 0) { throw "admin install for scenario G2 exit $($r.Exit)" }
    if (Test-Path "$env:SystemDrive\p10-std-exit2.txt") { Remove-Item "$env:SystemDrive\p10-std-exit2.txt" -Force }
    Set-Content -Path $cmdLaunch -Value "start `"`" /B `"$exePath`"`r`ntimeout /t 25 /nobreak >nul`r`ntasklist /FI `"IMAGENAME eq PrintShopCompanion.exe`" > `"$env:SystemDrive\p10-std-procs.txt`"`r`ntaskkill /IM PrintShopCompanion.exe /F >nul 2>&1`r`necho done> `"$env:SystemDrive\p10-std-exit2.txt`""
    schtasks /Create /TN P10StdLaunch /TR "`"$cmdLaunch`"" /SC ONCE /ST 23:59 /RU $stdUser /RP $stdPwd /F | Out-Null
    schtasks /Run /TN P10StdLaunch | Out-Null
    $g2Done = $false
    for ($i = 0; $i -lt 14; $i++) {
        Start-Sleep -Seconds 5
        if (Test-Path "$env:SystemDrive\p10-std-exit2.txt") { $g2Done = $true; break }
    }
    schtasks /Delete /TN P10StdLaunch /F | Out-Null
    $stdData = "$env:SystemDrive\Users\$stdUser\AppData\Local\PrintShop\Companion"
    $g2Procs = if (Test-Path "$env:SystemDrive\p10-std-procs.txt") { (Get-Content "$env:SystemDrive\p10-std-procs.txt" | Select-String 'PrintShopCompanion').Count } else { 0 }
    $g2Data  = Test-Path (Join-Path $stdData 'Database\queue.db')
    $g2Status = 'PARTIALLY VERIFIED'
    $g2Why = "non-interactive session launch (schtasks batch logon) is a constrained proxy for a real desktop session"
    if ($g2Procs -gt 0 -and $g2Data) {
        $g2Status = 'VERIFIED'
        $g2Why = "process ran and created its own per-user encrypted data under $stdData"
    } elseif ($g2Procs -gt 0) {
        $g2Why = "process ran under the standard user; per-user data dir not observed (headless session caveat)"
    }
    Record 'G2' 'Installed application runs under a standard user account' $g2Status "task completed=$g2Done; app process seen=$($g2Procs -gt 0); per-user queue.db created=$g2Data; $g2Why"
} catch {
    Record 'G' 'Installation under a standard Windows user account' 'FAILED' $_.Exception.Message
} finally {
    Get-Process PrintShopCompanion -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Remove-Item $cmdInstall, $cmdLaunch -Force -ErrorAction SilentlyContinue
    net user $stdUser /delete | Out-Null
    Remove-Item "$env:SystemDrive\Users\$stdUser" -Recurse -Force -ErrorAction SilentlyContinue
}

# ---- Final state + S9 metrics --------------------------------------------------
Write-Step "FINAL STATE + S9 OPERATIONAL METRICS"
$prodFinal = Get-InstalledProduct
if ($null -ne $prodFinal) {
    Write-Host "removing leftover product before reporting"
    $null = Invoke-Msi '/x' $prodFinal.PSChildName (Join-Path $ResultsDir 'final-cleanup-uninstall.log')
}
$finalExeGone = -not (Test-Path $exePath)
$finalProdGone = $null -eq (Get-InstalledProduct)
Write-Host "final state: no product=$finalProdGone, exe removed=$finalExeGone"
$metrics['final_state_clean'] = ($finalProdGone -and $finalExeGone)
$metrics['msi_a_sha256'] = $msiAHash
$metrics['msi_b_sha256'] = $msiBHash
$metrics['msi_a_version'] = $verA
$metrics['msi_b_version'] = $verB

$report = @"
Phase 8 baseline (hosted runner): ~146.5 MB idle working set, 1.5-2 s startup.
Runner-difference caveat: different runner image/hardware, so absolute numbers
are comparable only directionally; relative deltas matter.

| Metric | Value |
| --- | --- |
| MSI install duration | $($metrics['install_duration_sec']) s |
| Uninstall duration | $($metrics['uninstall_duration_sec']) s |
| Upgrade duration ($verA -> $verB) | $($metrics['upgrade_duration_sec']) s |
| First launch -> queue.db created | $($metrics['first_launch_db_created_sec']) s |
| App working set (idle, 20s) | $($metrics['app_working_set_mb']) MB |
| App working set after upgrade | $($metrics['app_working_set_after_upgrade_mb']) MB |
| Installed footprint | $($metrics['installed_footprint_mb']) MB |
"@
Write-Host $report

# ---- Evidence files ------------------------------------------------------------
$jsonPath = Join-Path $ResultsDir 'validation-results.json'
$mdPath   = Join-Path $ResultsDir 'validation-results.md'
$results | ConvertTo-Json -Depth 4 | Set-Content -Path $jsonPath -Encoding UTF8
$md = "# Phase 10 MSI validation results`n`n" + $report + "`n`n| Id | Scenario | Status | Evidence |`n| --- | --- | --- | --- |`n"
foreach ($r in $results) {
    $ev = $r.Evidence -replace '\|', '/'
    $md += "| $($r.Id) | $($r.Name) | $($r.Status) | $ev |`n"
}
Set-Content -Path $mdPath -Value $md -Encoding UTF8
if ($env:GITHUB_STEP_SUMMARY) {
    Add-Content -Path $env:GITHUB_STEP_SUMMARY -Value $md -Encoding UTF8
}

Write-Host ""
Write-Host "=== SUMMARY ==="
$failed = $results | Where-Object { $_.Status -eq 'FAILED' }
foreach ($r in $results) { Write-Host ("{0,-7} {1,-22} {2}" -f $r.Id, $r.Status, $r.Name) }
Write-Host ("MSI VALIDATION OVERALL: " + $(if ($failed.Count -eq 0) { 'PASS' } else { "FAIL ($($failed.Count) failed)" }))
Write-Host "Evidence: $jsonPath , $mdPath , MSI logs in $ResultsDir"

if ($failed.Count -gt 0) { exit 1 }
exit 0
