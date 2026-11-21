# EdiaHelper installer
#
# - finds the EDIABAS folders of INPA and ISTA
# - uses EdiabasLib 8.0 from this package (EdiabasLib folder, checked against the known fingerprints)
#   and BMW's original api32.dll / api64.dll, found separately for each EDIABAS folder (INPA and ISTA
#   can have different EDIABAS versions) and kept in <folder>\EdiaHelper-original
# - backs up everything it changes into <EDIABAS folder>\EdiaHelper-backup-<date>
# - installs EdiaHelper to C:\Program Files\EdiaHelper (only administrators can change it)
#   with autostart and a Desktop shortcut; replaces an older installation under the name
#   "Adapter Switch" (C:\Program Files\Adapter Switch or C:\EDIABAS\AdapterSwitch)
# - gives your user account the right to change only the EDIABAS files EdiaHelper switches
#   (listed in grants.txt; Uninstall takes them back)
# - switches to "MHD WiFi - K-line / D-CAN"
#
# Run Install.cmd (it asks for administrator rights), not this file directly.

param([string]$UserProfile = $env:USERPROFILE, [string]$UserName = "$env:USERDOMAIN\$env:USERNAME")

$ErrorActionPreference = 'Stop'
$pkg = Split-Path -Parent $MyInvocation.MyCommand.Path
$programFiles = $env:ProgramW6432; if (-not $programFiles) { $programFiles = $env:ProgramFiles }
$target = Join-Path $programFiles 'EdiaHelper'
# Earlier versions were called "Adapter Switch"
$oldTargets = @((Join-Path $programFiles 'Adapter Switch'), 'C:\EDIABAS\AdapterSwitch')
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
# The EdiabasLib files in this package (release binaries_20260607, version 8.0.0.407, unmodified)
$knownLib = @{
    'Api32.dll' = 'C3F1947DA56A6358D581288062CE3AE1F4323C946D69123E4DE7B0F68F7DA76D'
    'Api64.dll' = '14D5D6E2065EED0BAE6B944AE88608C60D70B892358A777353E20C727BCA8FBC'
}

function Say($t) { Write-Host $t }
function Step($t) { Write-Host ''; Write-Host "== $t" -ForegroundColor Cyan }
function Fail($t) { Write-Host ''; Write-Host $t -ForegroundColor Red; Write-Host ''; Read-Host 'Press Enter to close'; exit 1 }

function Test-EdiabasDir($d) { return $d -and (Test-Path (Join-Path $d 'EDIABAS.INI')) }

# Started from the source code (GitHub's green "Code -> Download ZIP" button) instead of the package?
if (-not (Test-Path (Join-Path $pkg 'EdiaHelper.exe'))) {
    Fail ("This is the source code, not the installable package.`n`n" +
          "Download EdiaHelper-<version>.zip from`n  https://github.com/Nahkasikari/EdiaHelper/releases/latest`n" +
          "(under Assets, not 'Source code'), unzip it and run Install.cmd from there.")
}

# ---------------------------------------------------------------- folders
Step 'Looking for the EDIABAS folders'
$inpaCandidates = @('C:\EDIABAS\Bin', 'C:\EDIABAS\BIN', 'C:\EC-APPS\EDIABAS\BIN', 'D:\EDIABAS\Bin')
$istaCandidates = @('C:\EC-APPS\ISTA\Ediabas\BIN', 'C:\ISTA\Ediabas\BIN', 'D:\EC-APPS\ISTA\Ediabas\BIN',
                    'C:\Program Files (x86)\BMW\ISPI\TRIC\ISTA\Ediabas\BIN', 'C:\Program Files\BMW\ISPI\TRIC\ISTA\Ediabas\BIN')
$inpa = $inpaCandidates | Where-Object { Test-EdiabasDir $_ } | Select-Object -First 1
$ista = $istaCandidates | Where-Object { Test-EdiabasDir $_ } | Select-Object -First 1
if (-not $ista) {
    # ISTA may be installed elsewhere: look for <x>\<ISTA>\Ediabas\BIN next to ISTA's TesterGUI folder
    foreach ($root in @('C:\', 'D:\')) {
        if (-not (Test-Path $root)) { continue }
        $ista = Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
            ForEach-Object { Get-ChildItem $_.FullName -Directory -ErrorAction SilentlyContinue } |
            Where-Object { Test-Path (Join-Path $_.FullName 'TesterGUI') } |
            ForEach-Object { Join-Path $_.FullName 'Ediabas\BIN' } |
            Where-Object { Test-EdiabasDir $_ } | Select-Object -First 1
        if ($ista) { break }
    }
}
if (-not $inpa) { Fail "Could not find the INPA EDIABAS folder (C:\EDIABAS\Bin with EDIABAS.INI). Is INPA installed?" }
Say "INPA / NCS Expert: $inpa"
if ($ista) { Say "ISTA:              $ista" } else { Say 'ISTA:              not found (only INPA will be set up; you can set the folder later in EdiaHelper)' }
$bins = @($inpa); if ($ista) { $bins += $ista }

# ---------------------------------------------------------------- runtimes needed by EdiabasLib 8.0
# INPA, NCS Expert and ISTA's EDIABAS are 32-bit, so the 32-bit (x86) runtimes are the ones needed,
# also on ARM computers (they run x86 programs under emulation).
Step 'Checking the runtimes EdiabasLib needs'
$missing = @()
$ndp = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue).Release
if ($ndp -ge 533320) { Say '.NET Framework 4.8.1: OK' }
else { $missing += [pscustomobject]@{ Name = '.NET Framework 4.8.1'; Url = 'https://dotnet.microsoft.com/download/dotnet-framework/net481' } }
$desk = Get-ChildItem "${env:ProgramFiles(x86)}\dotnet\shared\Microsoft.WindowsDesktop.App" -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^(\d+)\.' -and [int]$Matches[1] -ge 10 }
if ($desk) { Say ".NET Desktop Runtime (x86): $($desk[-1].Name)" }
else { $missing += [pscustomobject]@{ Name = '.NET 10 Desktop Runtime, x86 (32-bit)'; Url = 'https://dotnet.microsoft.com/download/dotnet/10.0' } }
$vc = Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x86' -ErrorAction SilentlyContinue
if (-not $vc) { $vc = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x86' -ErrorAction SilentlyContinue }
if ($vc -and $vc.Installed -eq 1) { Say "Visual C++ Runtime (x86): $($vc.Version)" }
else { $missing += [pscustomobject]@{ Name = 'Visual C++ Redistributable 2015-2022, x86 (32-bit)'; Url = 'https://aka.ms/vs/17/release/vc_redist.x86.exe' } }
if ($missing) {
    Write-Host ''
    Write-Host 'Missing (needed for the MHD WiFi choices; the K+DCAN cable works without them):' -ForegroundColor Yellow
    foreach ($m in $missing) { Write-Host "  - $($m.Name): $($m.Url)" }
    Write-Host '  For the .NET 10 Desktop Runtime choose "Desktop Runtime" -> "Windows x86".'
    $a = Read-Host 'Open these download pages now? (Y/n)'
    if ($a -ne 'n') { foreach ($m in $missing) { Start-Process $m.Url } }
    Write-Host 'Installation continues. Install the missing runtimes afterwards; nothing needs to be reinstalled.'
}

# ---------------------------------------------------------------- programs running?
Step 'Checking that INPA, NCS Expert and ISTA are closed'
$diag = 'INPALOAD','inpa','NCSEXPER','NFS','tool32','ISTAGUI','IstaOperation','WinKFP'
while ($p = Get-Process -Name $diag -ErrorAction SilentlyContinue | Select-Object -First 1) {
    Read-Host "$($p.ProcessName) is running. Close it, then press Enter"
}
Get-Process -Name 'EdiaHelper','AdapterSwitch','MhdRelay' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Say 'OK'

# ---------------------------------------------------------------- EdiabasLib 8.0
Step 'Checking the EdiabasLib files in this package'
$libDir = Join-Path $pkg 'EdiabasLib'
foreach ($name in $knownLib.Keys) {
    $f = Join-Path $libDir $name
    if (-not (Test-Path $f)) { Fail "$name is missing from the package (EdiabasLib folder). Copy the whole package folder again." }
    if ((Get-FileHash $f -Algorithm SHA256).Hash -ne $knownLib[$name]) { Fail "$name in the package has been changed or damaged. Copy the whole package folder again." }
}
Say "EdiabasLib $((Get-Item (Join-Path $libDir 'Api32.dll')).VersionInfo.FileVersion): OK"

# ---------------------------------------------------------------- BMW original DLLs
# Each EDIABAS folder keeps its own originals: INPA and ISTA can have different EDIABAS versions.
Step "Looking for BMW's original EDIABAS DLLs (separately for each EDIABAS folder)"
function Test-Bmw($f) {
    if (-not $f -or -not (Test-Path $f -PathType Leaf)) { return $false }
    $i = Get-Item $f
    return $i.Length -lt 1MB -and ("$($i.VersionInfo.FileDescription) $($i.VersionInfo.ProductName) $($i.VersionInfo.CompanyName)" -notmatch 'EdiabasLib')
}
function Find-Original($bin, $role, $dll) {
    $base = [IO.Path]::GetFileNameWithoutExtension($dll)
    $cands = @((Join-Path $bin $dll), (Join-Path $bin "EdiaHelper-original\$dll"))
    $cands += @(Get-ChildItem $bin -Filter "$base*.dll" -File -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object FullName)   # e.g. api32-Original.dll
    $cands += @(Get-ChildItem $bin -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^(EdiaHelper|AdapterSwitch)-backup-' } |
                Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName $dll })
    foreach ($t in @($target) + $oldTargets) { $cands += Join-Path $t "profiles\BmwOriginal\$role\$dll"; $cands += Join-Path $t "profiles\BmwOriginal\$dll" }
    foreach ($c in $cands) { if (Test-Bmw $c) { return (Get-Item $c) } }
    return $null
}
$folders = @([pscustomobject]@{ Bin = $inpa; Role = 'INPA' })
if ($ista) { $folders += [pscustomobject]@{ Bin = $ista; Role = 'ISTA' } }
$work = Join-Path $env:TEMP "EdiaHelper-$stamp"
foreach ($fo in $folders) {
    $dir = Join-Path $work "BmwOriginal\$($fo.Role)"
    New-Item -ItemType Directory -Force $dir | Out-Null
    foreach ($dll in 'Api32.dll', 'Api64.dll') {
        $o = Find-Original $fo.Bin $fo.Role $dll
        if (-not $o -and $fo.Role -eq 'ISTA') {
            $o = Find-Original $inpa 'INPA' $dll
            if ($o) { Say "$($fo.Role) $dll : ISTA's own original not found, using INPA's ($($o.VersionInfo.FileVersion))" }
        }
        if (-not $o) {
            if ($dll -eq 'Api32.dll') { Fail "BMW's original api32.dll was not found for $($fo.Bin). Reinstall INPA/EDIABAS (or ISTA), or copy BMW's api32.dll into that folder." }
            Say "$($fo.Role) $dll : not found (not needed for INPA)"
            continue
        }
        Copy-Item $o.FullName (Join-Path $dir $dll)
        Say "$($fo.Role) $dll : $($o.FullName) (version $($o.VersionInfo.FileVersion))"
    }
}

# ---------------------------------------------------------------- backup
Step 'Backing up the current files'
foreach ($b in $bins) {
    $bk = Join-Path $b "EdiaHelper-backup-$stamp"
    New-Item -ItemType Directory -Force $bk | Out-Null
    Get-ChildItem $b -File | Where-Object { $_.Name -match '^(api(32|64).*\.dll|EdiabasLib.*\.config|EDIABAS\.INI|RPLUS\.INI)$' } |
        ForEach-Object { Copy-Item $_.FullName $bk }
    Say "$b -> $bk"
}

# ---------------------------------------------------------------- program files
Step "Installing EdiaHelper to $target"
# the single original set of earlier versions is replaced by one set per folder
Remove-Item "$target\profiles\BmwOriginal" -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$target\profiles\EdiabasLib8", "$target\profiles\BmwOriginal" | Out-Null
Copy-Item (Join-Path $pkg 'EdiaHelper.exe') $target -Force
Copy-Item (Join-Path $libDir 'Api*.dll') "$target\profiles\EdiabasLib8" -Force
Copy-Item (Join-Path $pkg 'EdiabasLib-INPA.config'), (Join-Path $pkg 'EdiabasLib-ISTA.config') "$target\profiles\EdiabasLib8" -Force
Copy-Item (Join-Path $work 'BmwOriginal\*') "$target\profiles\BmwOriginal" -Recurse -Force
# each EDIABAS folder also keeps its own copy, which EdiaHelper uses when it switches back
foreach ($fo in $folders) {
    $own = Join-Path $fo.Bin 'EdiaHelper-original'
    New-Item -ItemType Directory -Force $own | Out-Null
    foreach ($f in Get-ChildItem (Join-Path $work "BmwOriginal\$($fo.Role)") -File) {
        $dest = Join-Path $own $f.Name
        if (-not (Test-Bmw $dest)) { Copy-Item $f.FullName $dest -Force }   # an existing saved original is kept
    }
}
Copy-Item (Join-Path $libDir 'LICENSE (EdiabasLib).txt') "$target\profiles\EdiabasLib8" -Force -ErrorAction SilentlyContinue
foreach ($f in 'Uninstall.cmd','Uninstall.ps1','Elevate.ps1','README.txt','LICENSE.txt','THIRD-PARTY-NOTICES.txt') { if (Test-Path (Join-Path $pkg $f)) { Copy-Item (Join-Path $pkg $f) $target -Force } }
$iniLines = @("EdiabasBin=$inpa")
if ($ista) { $iniLines += "IstaBin=$ista" } else { $iniLines += "IstaBin=" }
Set-Content -Path "$target\EdiaHelper.ini" -Value $iniLines -Encoding UTF8
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
Say 'OK'

# ---------------------------------------------------------------- permissions
# Only the files EdiaHelper switches (and its own EdiaHelper-original folder), not the whole EDIABAS
# folder: a folder your account can write to could be used to plant DLLs that other programs load.
# The program folder stays administrator-only (Program Files default). Uninstall takes the rights back.
Step 'Letting your user account change the EDIABAS files EdiaHelper switches (so switching needs no administrator prompt)'
$granted = @()
foreach ($fo in $folders) {
    $b = $fo.Bin
    & icacls $b /remove:g "$UserName" /Q | Out-Null   # earlier versions gave the whole folder
    # files EdiaHelper may write are created now, so no right to create files in the folder is needed
    if (-not (Test-Path (Join-Path $b 'EdiabasLib.config'))) { Copy-Item (Join-Path $pkg "EdiabasLib-$($fo.Role).config") (Join-Path $b 'EdiabasLib.config') }
    if (-not (Test-Path (Join-Path $b 'obd.ini')) -and (Test-Path (Join-Path $inpa 'obd.ini'))) { Copy-Item (Join-Path $inpa 'obd.ini') (Join-Path $b 'obd.ini') }
    foreach ($n in 'Api32.dll', 'Api64.dll', 'EdiabasLib.config', 'EDIABAS.INI', 'RPLUS.INI', 'obd.ini') {
        $p = Join-Path $b $n
        if (Test-Path $p) { & icacls $p /grant "${UserName}:M" /Q | Out-Null; $granted += $p }
    }
    $own = Join-Path $b 'EdiaHelper-original'
    & icacls $own /grant "${UserName}:(OI)(CI)M" /Q | Out-Null; $granted += $own
    Say "$b : $UserName can change the files EdiaHelper switches"
}
Set-Content -Path "$target\grants.txt" -Value $granted -Encoding UTF8

# ---------------------------------------------------------------- older installation
foreach ($old in $oldTargets) {
    if (-not (Test-Path $old)) { continue }
    Step "Removing the older installation in $old"
    Remove-Item $old -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path $old) { Say "Could not remove all of $old; you can delete it by hand." } else { Say 'OK' }
}

# ---------------------------------------------------------------- shortcuts
Step 'Creating shortcuts'
$ws = New-Object -ComObject WScript.Shell
$startup = Join-Path $UserProfile 'AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup'
$desktop = [Environment]::GetFolderPath('Desktop')
if ($UserProfile -ne $env:USERPROFILE) { $desktop = Join-Path $UserProfile 'Desktop' }
# replace the earlier separate relay program, if present
foreach ($l in 'MHD Relay.lnk', 'Adapter Switch.lnk') {
    Remove-Item (Join-Path $startup $l), (Join-Path $desktop $l) -ErrorAction SilentlyContinue
}
$auto = $ws.CreateShortcut((Join-Path $startup 'EdiaHelper.lnk'))
$auto.TargetPath = "$target\EdiaHelper.exe"; $auto.Arguments = '/tray'; $auto.WorkingDirectory = $target
$auto.Description = 'EdiaHelper (tray icon and MHD relay)'; $auto.Save()
$desk = $ws.CreateShortcut((Join-Path $desktop 'EdiaHelper.lnk'))
$desk.TargetPath = "$target\EdiaHelper.exe"; $desk.WorkingDirectory = $target
$desk.Description = 'Choose the adapter for INPA, NCS Expert and ISTA'; $desk.Save()
Say "Autostart: $startup\EdiaHelper.lnk"
Say "Desktop:   $desktop\EdiaHelper.lnk"

# ---------------------------------------------------------------- first switch and start
Step 'Switching to "MHD WiFi - K-line / D-CAN"'
$p = Start-Process "$target\EdiaHelper.exe" -ArgumentList '/apply:0' -Wait -PassThru
if ($p.ExitCode -ne 0) { Fail 'Switching failed. Open EdiaHelper and check the folders.' }
Say 'OK'

# start the tray program as the normal user (not as administrator)
Start-Process explorer.exe -ArgumentList "`"$(Join-Path $startup 'EdiaHelper.lnk')`""

Write-Host ''
Write-Host 'EdiaHelper is installed and running (tray icon, bottom right).' -ForegroundColor Green
Write-Host 'Use the "EdiaHelper" shortcut on the Desktop to choose the adapter.'
Write-Host "Backups of the changed files are in the EdiaHelper-backup-$stamp folders."
Write-Host ''
Read-Host 'Press Enter to close'
