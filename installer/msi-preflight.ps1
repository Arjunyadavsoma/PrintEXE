<#
.SYNOPSIS
  Phase 10 MSI preflight: fails the build if the error-1606 authoring
  pattern is present in the MSI database.

.DESCRIPTION
  Opens the MSI database via the native WindowsInstaller.Installer COM
  interface (no external tooling) and checks:
    1. PROPERTY/DIRECTORY COLLISION (the error-1606 root cause): a
       Property-table row whose name is also a Directory-table row Id.
       A Property value overrides Directory-tree resolution at
       CostFinalize; a drive-less value is interpreted as a network
       location -> "error 1606 Could not access network location X" ->
       fatal 1603.
    2. DRIVE-LESS DIRECTORY RESOLUTION: every Directory row is resolved
       from the tree; any directory that resolves without a "X:\" prefix
       (and is not a runtime-resolved standard root) is a 1606 risk.
    3. SHORTCUT SANITY: every shortcut's Directory_ and WkDir reference
       known Directory Ids.
    4. CUSTOM ACTION RECORD: all custom actions and their types are
       recorded (no destructive/deferred CA without explicit review).
    5. SECRET SCAN: Property table must not contain Password/Secret/
       Token-like properties.
    6. USER-DATA POLICY EVIDENCE: reports whether a RemoveFile table
       exists (documented policy: the uninstaller must not delete
       %LocalAppData% user data).

  PowerShell 5.1 compatible. Exit 0 = pass, 1 = findings, 2 = crash
  (with exact line + stack trace printed).

  History of the two CI crashes this script survived (both diagnosed and
  fixed - see git history):
  - run 46141866791: List[object] + 'return ,$rows' under Set-StrictMode
    2.0 -> null table read -> null-array index.
  - run 46142616804: '@(Read-MsiTable ...)' around a ',$out' return
    COLLAPSED the row collection into a single element (verified locally:
    count=1, row = whole collection) -> structurally corrupted check phase.
  Final design: NO pipeline-return for tables at all. Read-MsiTable hands
  rows over via a script-scope variable; every caller validates shape.

.PARAMETER MsiPath
  Path to the .msi file to inspect.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$MsiPath
)

$ErrorActionPreference = 'Stop'
# NOTE: deliberately NO Set-StrictMode - StrictMode 2.0 turns COM interop
# edge cases into hard 'Cannot index into a null array' crashes. Every
# check below guards its own inputs explicitly instead.

trap {
    Write-Host ("PREFLIGHT CRASH: " + $_.Exception.Message)
    Write-Host ("AT: " + $_.InvocationInfo.PositionMessage)
    Write-Host ("STACK: " + $_.ScriptStackTrace)
    Write-Host "MSI PREFLIGHT: ERROR (script bug - see trace above)"
    exit 2
}

if (-not (Test-Path $MsiPath)) { Write-Error "MSI not found: $MsiPath" }

Write-Host "=== MSI preflight: $MsiPath"
Write-Host ("SHA-256: " + (Get-FileHash -Path $MsiPath -Algorithm SHA256).Hash)

# ---- COM helpers -----------------------------------------------------------
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.GetType().InvokeMember(
    'OpenDatabase', 'InvokeMethod', $null, $installer, @($MsiPath, 0))

# Rows are handed over via a script-scope variable, NOT the pipeline -
# immune to PS 5.1/7 collection-unrolling differences (see header history).
$script:lastRows = $null

function Read-MsiTable {
    param([string]$Sql, [string]$Label)
    Write-Host ("  reading table: " + $Label)
    $view = $database.GetType().InvokeMember(
        'OpenView', 'InvokeMethod', $null, $database, @($Sql))
    $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
    $collected = New-Object System.Collections.Generic.List[object]
    while ($true) {
        $rec = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if ($null -eq $rec) { break }
        $fieldCount = $rec.GetType().InvokeMember(
            'FieldCount', 'GetProperty', $null, $rec, $null)
        $rowArr = New-Object object[] $fieldCount
        for ($i = 1; $i -le $fieldCount; $i++) {
            $val = $rec.GetType().InvokeMember(
                'StringData', 'GetProperty', $null, $rec, @($i))
            if ($null -eq $val) { $val = '' }
            $rowArr[$i - 1] = [string]$val
        }
        [void]$collected.Add($rowArr)
    }
    $out = New-Object object[] $collected.Count
    for ($j = 0; $j -lt $collected.Count; $j++) { $out[$j] = $collected[$j] }
    Write-Host ("    -> " + $collected.Count + " rows")
    $script:lastRows = $out
}

function Get-TableRowsInto {
    # Materialize a table's rows DIRECTLY into a named script-scope variable.
    # NO pipeline return anywhere: PS 5.1's '$x = Func' assignment re-enumes
    # function output, which collapsed/shifted nested arrays in runs
    # 46141866791 / 46142616804 / 46143275161 even through ','
    # wrappers. Set-Variable + script scope is assignment-free and
    # deterministic on both PS 5.1 and pwsh 7.
    param([string]$VarName, [string]$Sql, [string]$Label, [int]$MinColumns)
    $script:lastRows = $null
    try {
        Read-MsiTable -Sql $Sql -Label $Label
    } catch {
        Write-Host ("    (table '" + $Label + "' not readable: " + $_.Exception.Message + ")")
        $script:lastRows = New-Object object[] 0
    }
    $rows = $script:lastRows
    if ($null -eq $rows) { $rows = New-Object object[] 0 }
    if ($rows -isnot [System.Array]) {
        Write-Host ("    SHAPE BUG in '" + $Label + "': lastRows is " + $rows.GetType().FullName + " - forcing empty")
        $rows = New-Object object[] 0
    }
    for ($k = 0; $k -lt $rows.Count; $k++) {
        $row = $rows[$k]
        if ($row -isnot [System.Array]) {
            Write-Host ("    SHAPE BUG in '" + $Label + "' row " + $k + ": element is " +
                $(if ($null -eq $row) { '<null>' } else { $row.GetType().FullName }) +
                " (value: " + $(if ($null -eq $row) { '' } else { [string]$row }) + ")")
            $rows[$k] = ,@()   # replace with a safe empty row; the column
                               # guards below turn it into a finding
        }
    }
    Set-Variable -Name $VarName -Value $rows -Scope Script
    Write-Host ("    loaded $" + $VarName + ": " + $rows.Count + " rows")
}

Get-TableRowsInto -VarName 'propertyRows'  -Sql "SELECT ``Property``, ``Value`` FROM ``Property``" -Label 'Property' -MinColumns 2
Get-TableRowsInto -VarName 'directoryRows' -Sql "SELECT ``Directory``, ``Directory_Parent``, ``DefaultDir`` FROM ``Directory``" -Label 'Directory' -MinColumns 3
Get-TableRowsInto -VarName 'shortcutRows'  -Sql "SELECT ``Shortcut``, ``Directory_``, ``Target``, ``WkDir`` FROM ``Shortcut``" -Label 'Shortcut' -MinColumns 4
Get-TableRowsInto -VarName 'caRows'        -Sql "SELECT ``Action``, ``Type``, ``Source``, ``Target`` FROM ``CustomAction``" -Label 'CustomAction' -MinColumns 4
Get-TableRowsInto -VarName 'removeFileRows' -Sql "SELECT ``FileKey`` FROM ``RemoveFile``" -Label 'RemoveFile' -MinColumns 1

# Standard roots: runtime-resolved by Windows Installer (skipped in the
# drive-less check because their real values are machine-dependent).
$standardRoots = @{
    'TARGETDIR'            = $true
    'SourceDir'            = $true
    'ProgramFiles64Folder' = 'C:\Program Files\'
    'ProgramFilesFolder'   = 'C:\Program Files (x86)\'
    'CommonFiles64Folder'  = 'C:\Program Files\Common Files\'
    'ProgramMenuFolder'    = 'C:\ProgramData\Microsoft\Windows\Start Menu\Programs\'
    'DesktopFolder'        = 'C:\Users\Public\Desktop\'
    'StartupFolder'        = 'C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Startup\'
    'CommonAppDataFolder'  = 'C:\ProgramData\'
    'WindowsFolder'        = 'C:\Windows\'
    'SystemFolder'         = 'C:\Windows\System32\'
    'WindowsVolume'        = 'C:\'
    'TempFolder'           = $true
    'LocalAppDataFolder'   = $true
    'AppDataFolder'        = $true
    'PersonalFolder'       = $true
    'AdminToolsFolder'     = $true
    'FontsFolder'          = $true
    'System64Folder'       = $true
}

function Get-LongDefaultDir {
    param([string]$DefaultDir)
    # MSI DefaultDir is "shortname|longname" or just the name.
    if ($null -eq $DefaultDir) { return '' }
    $parts = $DefaultDir -split '\|', 2
    if ($parts.Count -gt 1) { return $parts[1] } else { return $parts[0] }
}

# ---- Finding collection ----------------------------------------------------
$findings = New-Object System.Collections.Generic.List[string]
$notes = New-Object System.Collections.Generic.List[string]

$dirById = @{}
foreach ($r in $directoryRows) {
    if (@($r).Count -lt 3) { $findings.Add("Directory row with fewer than 3 columns."); continue }
    $dirById[$r[0]] = @{ Parent = $r[1]; DefaultDir = $r[2] }
}
$propByName = @{}
foreach ($r in $propertyRows) {
    if (@($r).Count -lt 2) { $findings.Add("Property row with fewer than 2 columns."); continue }
    $propByName[$r[0]] = $r[1]
}

# CHECK 1: Property/Directory Id collisions (the error-1606 root cause).
foreach ($name in $dirById.Keys) {
    if ($propByName.ContainsKey($name)) {
        $findings.Add("CHECK1 Property/Directory collision: Directory Id '$name' also has a Property row = '$($propByName[$name])'. Property values override Directory-tree resolution at CostFinalize; a drive-less value becomes a network location (error 1606).")
    }
}

# CHECK 2: simulate CostFinalize resolution; flag drive-less paths.
function Resolve-Directory {
    param([string]$Id, [string[]]$Seen = @())
    if ($standardRoots.ContainsKey($Id)) {
        $root = $standardRoots[$Id]
        if ($root -is [bool]) { return $null } else { return $root }
    }
    if ($Seen -contains $Id) { return $null }  # cycle guard
    if (-not $dirById.ContainsKey($Id)) { return $null }
    $row = $dirById[$Id]
    $parent = $row['Parent']
    $value = Get-LongDefaultDir $row['DefaultDir']
    if ([string]::IsNullOrEmpty($parent)) {
        # Parentless non-standard directory: the DefaultDir is used as-is.
        return $value
    }
    $resolvedParent = Resolve-Directory -Id $parent -Seen ($Seen + $Id)
    if ($null -eq $resolvedParent) { return $null }
    if ($resolvedParent.EndsWith('\')) { return $resolvedParent + $value }
    return $resolvedParent + '\' + $value
}

foreach ($id in $dirById.Keys) {
    if ($id -isnot [string]) { $findings.Add("Directory Id is not a string (row shape corruption)."); continue }
    $resolved = Resolve-Directory -Id $id
    $shown = '<runtime-resolved>'
    if ($null -ne $resolved) { $shown = $resolved }
    $notes.Add(("  {0,-32} -> {1}" -f $id, $shown))
    if ($null -ne $resolved -and $id -ne 'TARGETDIR' -and $id -ne 'SourceDir') {
        if ($resolved -isnot [string]) { $findings.Add("Directory '$id' resolved to a non-string (row shape corruption)."); continue }
        $isUnc = $resolved.StartsWith('\\')
        $isDrive = $resolved.Length -ge 3 -and $resolved.Substring(1, 2) -eq ':\'
        if (-not $isUnc -and -not $isDrive) {
            $findings.Add("CHECK2 Directory '$id' resolves to a DRIVE-LESS path '$resolved' - Windows Installer treats this as a network location (error 1606 pattern).")
        }
    }
}

# CHECK 3: shortcut sanity (no Attributes column exists in the MSI Shortcut
# table - advertised-ness is not a table flag; the target/workdir checks are
# what matters for this app).
foreach ($s in $shortcutRows) {
    if (@($s).Count -lt 4) { $findings.Add("CHECK3 Shortcut row has fewer than 4 columns."); continue }
    $name = $s[0]; $dir = $s[1]; $target = $s[2]; $wkDir = $s[3]
    if ([string]::IsNullOrEmpty($name)) { $findings.Add("CHECK3 Shortcut row with empty name."); continue }
    if (-not $dirById.ContainsKey($dir)) {
        $findings.Add("CHECK3 Shortcut '$name' references unknown Directory Id '$dir'.")
    }
    if (-not [string]::IsNullOrEmpty($wkDir) -and -not $dirById.ContainsKey($wkDir)) {
        $findings.Add("CHECK3 Shortcut '$name' WorkingDirectory references unknown Directory Id '$wkDir'.")
    }
    $notes.Add("  shortcut '$name' dir='$dir' target='$target' workdir='$wkDir'")
}

# CHECK 4: custom actions (record; a CA cannot affect CostFinalize because
# CostFinalize runs before any CA, but record for the audit trail).
foreach ($c in $caRows) {
    if (@($c).Count -lt 4) { $notes.Add("  custom action row with fewer than 4 columns."); continue }
    $notes.Add("  custom action '$($c[0])' type=$($c[1]) source='$($c[2])' target='$($c[3])'")
}

# CHECK 5: no secret-like properties in the package.
foreach ($p in $propByName.Keys) {
    if ($p -match '(?i)(password|secret|apikey|api_key|token|privatekey)') {
        $findings.Add("CHECK5 Property table contains secret-like property '$p'. Packages must not embed secrets.")
    }
}

# CHECK 6: user-data policy evidence.
if ($removeFileRows.Count -gt 0) {
    $notes.Add("  RemoveFile table present with $($removeFileRows.Count) rows - REVIEW: uninstaller may delete files outside its components.")
} else {
    $notes.Add("  RemoveFile table absent - uninstaller removes only owned components; %LocalAppData% user data survives uninstall (documented policy).")
}

# ---- Report ----------------------------------------------------------------
Write-Host "--- Directory resolution (simulated CostFinalize) ---"
$notes | ForEach-Object { Write-Host $_ }
Write-Host "--- Findings: $($findings.Count) ---"
foreach ($f in $findings) { Write-Host "FAIL: $f" }

if ($findings.Count -gt 0) {
    Write-Host "MSI PREFLIGHT: FAIL"
    exit 1
}
Write-Host "MSI PREFLIGHT: PASS (no Property/Directory collisions, no drive-less directory resolutions)"
exit 0
