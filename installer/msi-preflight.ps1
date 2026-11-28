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
       known Directory Ids; advertised shortcuts are reported.
    4. CUSTOM ACTION RECORD: all custom actions and their types are
       recorded (no destructive/deferred CA without explicit review).
    5. SECRET SCAN: Property table must not contain Password/Secret/
       Token-like properties.
    6. USER-DATA POLICY EVIDENCE: reports whether a RemoveFile table
       exists (documented policy: the uninstaller must not delete
       %LocalAppData% user data).

  PowerShell 5.1 compatible. Exit 0 = pass, 1 = findings.

.PARAMETER MsiPath
  Path to the .msi file to inspect.
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$MsiPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

if (-not (Test-Path $MsiPath)) { Write-Error "MSI not found: $MsiPath" }

Write-Host "=== MSI preflight: $MsiPath"
Write-Host ("SHA-256: " + (Get-FileHash -Path $MsiPath -Algorithm SHA256).Hash)

# ---- COM helpers -----------------------------------------------------------
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.GetType().InvokeMember(
    'OpenDatabase', 'InvokeMethod', $null, $installer, @($MsiPath, 0))

function Read-MsiTable {
    param([string]$Sql)
    $view = $database.GetType().InvokeMember(
        'OpenView', 'InvokeMethod', $null, $database, @($Sql))
    $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
    $rows = New-Object System.Collections.Generic.List[object]
    while ($true) {
        $rec = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
        if ($null -eq $rec) { break }
        $fieldCount = $rec.GetType().InvokeMember(
            'FieldCount', 'GetProperty', $null, $rec, $null)
        $fields = @()
        for ($i = 1; $i -le $fieldCount; $i++) {
            $fields += $rec.GetType().InvokeMember(
                'StringData', 'GetProperty', $null, $rec, @($i))
        }
        $rows.Add($fields)
    }
    return ,$rows
}

# ---- Read the tables -------------------------------------------------------
$propertyRows = Read-MsiTable "SELECT ``Property``, ``Value`` FROM ``Property``"
$directoryRows = Read-MsiTable "SELECT ``Directory``, ``Directory_Parent``, ``DefaultDir`` FROM ``Directory``"
$shortcutRows = @()
try { $shortcutRows = Read-MsiTable "SELECT ``Shortcut``, ``Directory_``, ``Target``, ``WkDir``, ``Attributes`` FROM ``Shortcut``" } catch {}
$caRows = @()
try { $caRows = Read-MsiTable "SELECT ``Action``, ``Type``, ``Source``, ``Target`` FROM ``CustomAction``" } catch {}
$removeFileRows = @()
try { $removeFileRows = Read-MsiTable "SELECT ``FileKey`` FROM ``RemoveFile``" } catch { }

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
    'TempFolder'           = $true   # runtime-resolved
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
    $parts = $DefaultDir -split '\|', 2
    if ($parts.Count -gt 1) { return $parts[1] } else { return $parts[0] }
}

# ---- Finding collection ----------------------------------------------------
$findings = New-Object System.Collections.Generic.List[string]
$notes = New-Object System.Collections.Generic.List[string]

$dirById = @{}
foreach ($r in $directoryRows) {
    $dirById[$r[0]] = @{ Parent = $r[1]; DefaultDir = $r[2] }
}
$propByName = @{}
foreach ($r in $propertyRows) {
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
    $resolved = Resolve-Directory -Id $id
    $note = "  {0,-32} -> {1}" -f $id, $(if ($null -eq $resolved) { '<runtime-resolved>' } else { $resolved })
    $notes.Add($note)
    if ($null -ne $resolved -and $id -ne 'TARGETDIR' -and $id -ne 'SourceDir') {
        $isUnc = $resolved.StartsWith('\\')
        $isDrive = $resolved.Length -ge 3 -and $resolved.Substring(1, 2) -eq ':\'
        if (-not $isUnc -and -not $isDrive) {
            $findings.Add("CHECK2 Directory '$id' resolves to a DRIVE-LESS path '$resolved' - Windows Installer treats this as a network location (error 1606 pattern).")
        }
    }
}

# CHECK 3: shortcut sanity.
foreach ($s in $shortcutRows) {
    $name = $s[0]; $dir = $s[1]; $target = $s[2]; $wkDir = $s[3]; $attr = $s[4]
    if (-not $dirById.ContainsKey($dir)) {
        $findings.Add("CHECK3 Shortcut '$name' references unknown Directory Id '$dir'.")
    }
    if (-not [string]::IsNullOrEmpty($wkDir) -and -not $dirById.ContainsKey($wkDir)) {
        $findings.Add("CHECK3 Shortcut '$name' WorkingDirectory references unknown Directory Id '$wkDir'.")
    }
    $advertised = (([int]$attr) -band 1) -ne 0
    if ($advertised) {
        $notes.Add("  shortcut '$name' is ADVERTISED (source-resolving; not used by this app)")
    }
    $notes.Add("  shortcut '$name' dir='$dir' target='$target' workdir='$wkDir'")
}

# CHECK 4: custom actions (record; a CA cannot affect CostFinalize because
# CostFinalize runs before any CA, but record for the audit trail).
foreach ($c in $caRows) {
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
$notes | Where-Object { $_ -like '  *' } | ForEach-Object { Write-Host $_ }
Write-Host "--- Findings: $($findings.Count) ---"
foreach ($f in $findings) { Write-Host "FAIL: $f" }

if ($findings.Count -gt 0) {
    Write-Host "MSI PREFLIGHT: FAIL"
    exit 1
}
Write-Host "MSI PREFLIGHT: PASS (no Property/Directory collisions, no drive-less directory resolutions)"
exit 0
