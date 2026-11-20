# EdiaHelper uninstaller
#
# - stops EdiaHelper
# - puts BMW's own Api32.dll / Api64.dll back into each EDIABAS folder (from the copy EdiaHelper kept for
#   that folder; never an EdiabasLib DLL, even if an old backup contains one)
# - puts back the settings files (EDIABAS.INI, RPLUS.INI, EdiabasLib.config) from the oldest
#   EdiaHelper-backup-<date> / AdapterSwitch-backup-<date> folder, i.e. from before the first installation
# - takes back the file permissions the installer gave
# - removes the shortcuts and C:\Program Files\EdiaHelper (and an older "Adapter Switch" installation)
#
# Run Uninstall.cmd (it asks for administrator rights), not this file directly.

param([string]$UserProfile = $env:USERPROFILE, [string]$UserName = "$env:USERDOMAIN\$env:USERNAME")

$ErrorActionPreference = 'Stop'
$programFiles = $env:ProgramW6432; if (-not $programFiles) { $programFiles = $env:ProgramFiles }
$target = Join-Path $programFiles 'EdiaHelper'
$oldTargets = @((Join-Path $programFiles 'Adapter Switch'), 'C:\EDIABAS\AdapterSwitch')
Set-Location $env:TEMP

function Say($t) { Write-Host $t }
function Step($t) { Write-Host ''; Write-Host "== $t" -ForegroundColor Cyan }
function Warn($t) { Write-Host $t -ForegroundColor Yellow }

# BMW's own DLL: small (well under 1 MB) and not EdiabasLib
function Test-Bmw($f) {
    if (-not $f -or -not (Test-Path $f -PathType Leaf)) { return $false }
    $i = Get-Item $f
    return $i.Length -lt 1MB -and ("$($i.VersionInfo.FileDescription) $($i.VersionInfo.ProductName) $($i.VersionInfo.CompanyName)" -notmatch 'EdiabasLib')
}

# folders from the installer's settings
$inpa = $null; $ista = $null
$ini = Join-Path $target 'EdiaHelper.ini'
foreach ($o in $oldTargets) { if (-not (Test-Path $ini)) { $ini = Join-Path $o 'AdapterSwitch.ini' } }
if (Test-Path $ini) {
    foreach ($l in Get-Content $ini) {
        if ($l -match '^EdiabasBin=(.+)$' -and (Test-Path $Matches[1].Trim())) { $inpa = $Matches[1].Trim() }
        if ($l -match '^IstaBin=(.+)$' -and (Test-Path $Matches[1].Trim())) { $ista = $Matches[1].Trim() }
    }
}
if (-not $inpa -and (Test-Path 'C:\EDIABAS\Bin')) { $inpa = 'C:\EDIABAS\Bin' }
if (-not $ista -and (Test-Path 'C:\EC-APPS\ISTA\Ediabas\BIN')) { $ista = 'C:\EC-APPS\ISTA\Ediabas\BIN' }
$folders = @()
if ($inpa) { $folders += [pscustomobject]@{ Bin = $inpa; Role = 'INPA' } }
if ($ista) { $folders += [pscustomobject]@{ Bin = $ista; Role = 'ISTA' } }

Step 'Checking that INPA, NCS Expert and ISTA are closed'
$diag = 'INPALOAD','inpa','NCSEXPER','NFS','tool32','ISTAGUI','IstaOperation','WinKFP'
while ($p = Get-Process -Name $diag -ErrorAction SilentlyContinue | Select-Object -First 1) {
    Read-Host "$($p.ProcessName) is running. Close it, then press Enter"
}
Get-Process -Name 'EdiaHelper','AdapterSwitch' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Say 'OK'

$allOk = $true
foreach ($fo in $folders) {
    $b = $fo.Bin
    Step "Restoring $b"
    $backups = @(Get-ChildItem $b -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '^(EdiaHelper|AdapterSwitch)-backup-(\d{8}-\d{6})$' } |
        Sort-Object { ($_.Name -split '-backup-')[1] })

    # 1. BMW's own DLLs
    $dllsOk = $true
    foreach ($dll in 'Api32.dll', 'Api64.dll') {
        $cands = @((Join-Path $b "EdiaHelper-original\$dll"))
        foreach ($t in @($target) + $oldTargets) { $cands += Join-Path $t "profiles\BmwOriginal\$($fo.Role)\$dll"; $cands += Join-Path $t "profiles\BmwOriginal\$dll" }
        foreach ($bk in $backups) { $cands += Join-Path $bk.FullName $dll }
        $from = $cands | Where-Object { Test-Bmw $_ } | Select-Object -First 1
        $dest = Join-Path $b $dll
        if ($from) {
            Copy-Item $from $dest -Force
            Say "$dll : BMW original put back ($((Get-Item $dest).VersionInfo.FileVersion))"
        } elseif (Test-Bmw $dest) {
            Say "$dll : BMW original already in place"
        } elseif (Test-Path $dest) {
            Warn "$dll : no copy of BMW's original found - the file in the folder was left as it is. The K+DCAN cable may not work until BMW's $dll is put back (it comes with INPA / ISTA)."
            $dllsOk = $false; $allOk = $false
        }
    }

    # 2. settings files from before the first installation
    if ($backups) {
        $oldest = $backups[0]
        foreach ($f in Get-ChildItem $oldest.FullName -File | Where-Object { $_.Name -match '^(EDIABAS\.INI|RPLUS\.INI|EdiabasLib.*\.config)$' }) {
            Copy-Item $f.FullName $b -Force
        }
        if (-not (Test-Path (Join-Path $oldest.FullName 'EdiabasLib.config'))) { Remove-Item (Join-Path $b 'EdiabasLib.config') -ErrorAction SilentlyContinue }
        Say "Settings files put back from $($oldest.Name)"
    } else { Say 'No backup folder found - settings files left as they are' }

    # 3. the copies EdiaHelper kept for this folder are no longer needed
    if ($dllsOk) { Remove-Item (Join-Path $b 'EdiaHelper-original') -Recurse -Force -ErrorAction SilentlyContinue }
}

Step 'Taking back the file permissions the installer gave'
$grants = Join-Path $target 'grants.txt'
if (Test-Path $grants) {
    foreach ($p in Get-Content $grants) { if ($p -and (Test-Path $p)) { & icacls $p /remove:g "$UserName" /Q | Out-Null } }
}
foreach ($fo in $folders) { & icacls $fo.Bin /remove:g "$UserName" /Q | Out-Null }   # folder-wide grant of older versions
Say 'OK'

Step 'Removing shortcuts and program files'
$startup = Join-Path $UserProfile 'AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup'
foreach ($l in 'EdiaHelper.lnk', 'Adapter Switch.lnk') { Remove-Item (Join-Path $startup $l) -ErrorAction SilentlyContinue }
Remove-Item (Join-Path $UserProfile 'Desktop\EdiaHelper.lnk') -ErrorAction SilentlyContinue
Remove-Item ([Environment]::GetFolderPath('Desktop') + '\EdiaHelper.lnk') -ErrorAction SilentlyContinue
Remove-Item (@($target) + $oldTargets) -Recurse -Force -ErrorAction SilentlyContinue
Say 'OK'

Write-Host ''
if ($allOk) { Write-Host "EdiaHelper is removed. BMW's own EDIABAS DLLs and the settings from before the first installation are back." -ForegroundColor Green }
else { Write-Host 'EdiaHelper is removed, but some BMW DLLs could not be put back (see the yellow lines above).' -ForegroundColor Yellow }
Write-Host 'The backup folders (EdiaHelper-backup-... / AdapterSwitch-backup-...) were left in place; you can delete them.'
Write-Host "Your settings, log and `"last seen`" records in $(Join-Path $UserProfile 'AppData\Local\EdiaHelper') and your NCS backups were left in place."
Write-Host ''
Read-Host 'Press Enter to close'
