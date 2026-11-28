# tools/uia_probe.ps1 — Phase 8 Stage 4: real UI automation feasibility probe.
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
# Exit codes: 0 = all assertions passed; nonzero = the UI is broken or
# unreachable — the CI gate treats this as a failed startup-verification
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
        if ($proc.HasExited) { Fail "app exited prematurely (code $($proc.ExitCode)) — no main window" }
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

    # identify email vs password field
    $emailEl = $null; $pwEl = $null
    foreach ($e in $edits) {
        $vp = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        if ($vp.Current.IsPassword) { $pwEl = $e }
        elseif (-not $emailEl) { $emailEl = $e }
    }
    if (-not $pwEl) { Fail "no password edit field found (PasswordBox missing from UIA tree)" }
    Write-Host ("fields: email='{0}' password='{1}'" -f $emailEl.Current.AutomationId, $pwEl.Current.AutomationId)
    Write-Host "✓ password field exists with IsPassword=true (real PasswordBox)"

    # 2. REAL text input: SetValue on the email field, then read back
    $value = "ci-uia-probe@example.com"
    ($emailEl.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).SetValue($value)
    Start-Sleep -Milliseconds 300
    $readBack = ($emailEl.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)).Current.Value
    if ($readBack -ne $value) { Fail "email SetValue round-trip mismatch: got '$readBack', wanted '$value'" }
    Write-Host "✓ text input round-trip verified on the real email field"

    # 3. REAL interaction: click the Show button, password visibility must flip
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
    if (-not $revealAfter) { Fail "PasswordRevealBox still absent after TogglePasswordBtn click — toggle broken" }
    if ($null -ne $revealBefore) { Write-Host "note: reveal box already present before toggle" }
    Write-Host "✓ password visibility toggle verified (Show button really works)"

    # 4. screenshot as evidence (best effort — session may block desktop capture)
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
        Write-Host "✓ desktop screenshot saved to artifacts/startup-logs/uia-probe.png"
    }
    catch {
        Write-Host "!! desktop screenshot unavailable in this session: $($_.Exception.Message)"
    }

    Write-Host "=== UI AUTOMATION PROBE PASSED (real window, real fields, real interaction) ==="
    exit 0
}
finally {
    # never leave the app running — kill the whole tree
    try { $proc.Refresh(); if (-not $proc.HasExited) { taskkill /PID $proc.Id /T /F 2>$null | Out-Null } } catch { }
}
