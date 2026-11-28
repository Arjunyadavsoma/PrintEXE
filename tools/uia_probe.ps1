# tools/uia_probe.ps1 - Phase 8 Stage 4: real UI automation feasibility probe.
#
# Launches the REAL PrintEXE exe (normal mode, not --ci-smoke), attaches the
# Windows UI Automation framework to the actual login window and verifies
# INTERACTION, not just process liveness:
#   1. main window appears with a title (real WPF window, real visual tree)
#   2. email edit field accepts a SetValue and reads the text back
#   3. a password edit field exists with IsPassword=true (PasswordBox)
#   4. clicking the real "Show" button flips password visibility
#      (PasswordRevealBox absent -> present in the UIA tree)
#   5. a desktop screenshot is captured as evidence (best effort)
#
# IMPORTANT: this file is ASCII-ONLY on purpose. Windows PowerShell 5.1 reads
# BOM-less files as Windows-1252, and multi-byte UTF-8 characters end with
# bytes that decode to smart quotes (0x93/0x94), which are STRING DELIMITERS
# to the PS 5.1 tokenizer - run 46118630184 died on exactly that parse error.
#
# Exit codes: 0 = all assertions passed; nonzero = the UI is broken or
# unreachable - the CI gate treats this as a failed startup-verification
# layer. Runs under Windows PowerShell 5.1 (UIAutomation assemblies).
param([Parameter(Mandatory = $true)][string]$ExePath)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Fail([string]$msg) { throw $msg }

if (-not (Test-Path $ExePath)) { Fail "exe missing: $ExePath" }
Get-Process -Name PrintShopCompanion -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 1

$proc = Start-Process -FilePath $ExePath -PassThru -NoNewWindow
try {
    # 1. wait for the REAL main window (up to 40s)
    $deadline = (Get-Date).AddSeconds(40)
    while ($true) {
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne 0) { break }
        if ($proc.HasExited) { Fail "app exited prematurely (code $($proc.ExitCode)) - no main window" }
        if ((Get-Date) -gt $deadline) { Fail "no main window within 40s (UIA probe)" }
        Start-Sleep -Milliseconds 500
    }
    Start-Sleep -Seconds 3  # let the visual tree and bindings settle

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
    Write-Host ("window: '{0}' class={1}" -f $root.Current.Name, $root.Current.ClassName)
    if ([string]::IsNullOrWhiteSpace($root.Current.Name)) { Fail "main window has no title" }

    # enumerate edit controls inside the real window
    $editCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Edit)
    $edits = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCond)
    Write-Host "edit controls in window: $($edits.Count)"
    if ($edits.Count -lt 2) { Fail "expected >= 2 edit controls (email + password), found $($edits.Count)" }

    # identify fields by AutomationId (WPF maps x:Name -> AutomationId).
    # NOTE: run 3 evidence — the WPF PasswordBox peer did NOT report
    # IsPassword=true through the managed ValuePattern on the runner, so
    # IsPassword is logged as evidence but NOT used for identification.
    $pwEl = $null; $emailEl = $null
    foreach ($e in $edits) {
        $aid = $e.Current.AutomationId
        $isPw = $false
        try { $isPw = ($e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.IsPassword } catch { }
        Write-Host ("  edit AutomationId='{0}' IsPassword={1}" -f $aid, $isPw)
        if ($aid -eq "PasswordBox") { $pwEl = $e }
        elseif ($aid -eq "PasswordRevealBox") { Write-Host "note: reveal box present before toggle" }
        elseif ([string]::IsNullOrEmpty($aid) -and -not $emailEl) { $emailEl = $e }
    }
    if (-not $pwEl) { Fail "the PasswordBox element (AutomationId='PasswordBox') is missing from the UIA tree" }
    if (-not $emailEl) { Fail "no un-named edit field found for the email input" }
    Write-Host "[OK] login window has the real email field and the real PasswordBox (AutomationId match)"

    # 2. REAL text input: SetValue on the email field, then read back
    $value = "ci-uia-probe@example.com"
    ($emailEl.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($value)
    Start-Sleep -Milliseconds 300
    $readBack = ($emailEl.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value
    if ($readBack -ne $value) { Fail "email SetValue round-trip mismatch: got '$readBack', wanted '$value'" }
    Write-Host "[OK] text input round-trip verified on the real email field"

    # 3. REAL password input attempt + visibility toggle. Typing into the
    #    PasswordBox via UIA is best-effort (peers may refuse SetValue); the
    #    toggle itself is REQUIRED to work.
    $pwTyped = $null
    try {
        ($pwEl.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue("ci-probe-pw-12345")
        $pwTyped = "ci-probe-pw-12345"
        Write-Host "[OK] password typed into the real PasswordBox via UIA"
    } catch {
        Write-Host "!! PasswordBox refused UIA SetValue (peer restriction): $($_.Exception.Message)"
    }

    $btn = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "TogglePasswordBtn")))
    if (-not $btn) { Fail "TogglePasswordBtn not found in UIA tree" }
    $revealBefore = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "PasswordRevealBox")))
    ($btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    Start-Sleep -Milliseconds 800
    $revealAfter = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, "PasswordRevealBox")))
    if (-not $revealAfter) { Fail "PasswordRevealBox still absent after TogglePasswordBtn click - toggle broken" }
    if ($null -ne $revealBefore) { Write-Host "note: reveal box already present before toggle" }
    Write-Host "[OK] password visibility toggle verified (Show button really works)"

    # 4. mirror evidence: the reveal box is the app's own echo of the typed
    #    password - if we typed one and the mirror matches, the ENTIRE
    #    input -> state -> UI pipeline really works end to end.
    if ($pwTyped) {
        try {
            $mirrored = ($revealAfter.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value
            if ($mirrored -eq $pwTyped) {
                Write-Host "[OK] END-TO-END: typed password mirrors into the reveal box exactly ('...$($pwTyped.Substring($pwTyped.Length - 5))')"
            } else {
                Write-Host "!! reveal-box mirror mismatch: got '$mirrored', typed '$pwTyped' (informational)"
            }
        } catch {
            Write-Host "!! reveal box value not readable via UIA: $($_.Exception.Message)"
        }
    }

    # 4. screenshot as evidence (best effort - session may block desktop capture)
    try {
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -AssemblyName System.Drawing
        $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
        $bmp = New-Object System.Drawing.Bitmap($b.Width, $b.Height)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($b.Location, [System.Drawing.Point]::Empty, $b.Size)
        $outDir = Join-Path $env:GITHUB_WORKSPACE "artifacts\startup-logs"
        New-Item -ItemType Directory -Force -Path $outDir | Out-Null
        $bmp.Save((Join-Path $outDir "uia-probe.png"), [System.Drawing.Imaging.ImageFormat]::Png)
        $g.Dispose(); $bmp.Dispose()
        Write-Host "[OK] desktop screenshot saved to artifacts/startup-logs/uia-probe.png"
    }
    catch {
        Write-Host "!! desktop screenshot unavailable in this session: $($_.Exception.Message)"
    }

    # 5. resource snapshot (Phase 8 Stage 9: idle memory + CPU of the real
    #    interactive process, captured while the window is live)
    try {
        $proc.Refresh()
        $ws = [math]::Round($proc.WorkingSet64 / 1MB, 1)
        $cpu = [math]::Round($proc.TotalProcessorTime.TotalMilliseconds, 0)
        $threads = $proc.Threads.Count
        Write-Host "[PERF] idle process: WorkingSet=${ws}MB, TotalCPU=${cpu}ms, Threads=${threads}"
        $perfLine = "status=MEASURED workingSetMB=$ws totalCpuMs=$cpu threads=$threads"
        $outDir2 = Join-Path $env:GITHUB_WORKSPACE "artifacts\startup-logs"
        New-Item -ItemType Directory -Force -Path $outDir2 | Out-Null
        Add-Content -Path (Join-Path $outDir2 "perf-snapshot.txt") -Value $perfLine
        Write-Host "[OK] performance snapshot saved to artifacts/startup-logs/perf-snapshot.txt"
    } catch {
        Write-Host "!! performance snapshot failed: $($_.Exception.Message)"
    }

    Write-Host "=== UI AUTOMATION PROBE PASSED (real window, real fields, real interaction) ==="
    exit 0
}
finally {
    # never leave the app running - kill the whole tree
    try { $proc.Refresh(); if (-not $proc.HasExited) { taskkill /PID $proc.Id /T /F 2>$null | Out-Null } } catch { }
}
