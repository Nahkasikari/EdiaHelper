# Builds the installable package folder "EdiaHelper package" on the Desktop.
#
# The package contains everything the installer needs, including EdiabasLib's Api32.dll / Api64.dll
# (GPLv3, unmodified) with its license. They are taken from EdiabasLib's release zip
# (Binaries-20260607.zip, in Downloads or in third-party\EdiabasLib) and checked against the known
# fingerprints, so only the tested EdiabasLib version can end up in a package.
# Run make-package.cmd.

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

# EdiabasLib release used and tested (https://github.com/uholeschak/ediabaslib/releases/tag/binaries_20260607)
$zipName = 'Binaries-20260607.zip'
$known = @{
    'Api32.dll' = 'C3F1947DA56A6358D581288062CE3AE1F4323C946D69123E4DE7B0F68F7DA76D'
    'Api64.dll' = '14D5D6E2065EED0BAE6B944AE88608C60D70B892358A777353E20C727BCA8FBC'
}

$zip = @((Join-Path $root "third-party\EdiabasLib\$zipName"),
         (Join-Path ([Environment]::GetFolderPath('UserProfile')) "Downloads\$zipName")) |
       Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $zip) { throw "$zipName not found. Download it from https://github.com/uholeschak/ediabaslib/releases/tag/binaries_20260607 into Downloads." }

$pkg = Join-Path ([Environment]::GetFolderPath('Desktop')) 'EdiaHelper package'
if (Test-Path $pkg) { Remove-Item $pkg -Recurse -Force }
New-Item -ItemType Directory "$pkg\EdiabasLib" | Out-Null

Add-Type -AssemblyName System.IO.Compression.FileSystem
$z = [IO.Compression.ZipFile]::OpenRead($zip)
try {
    foreach ($name in $known.Keys) {
        $e = $z.Entries | Where-Object { $_.FullName -ieq "EdiabasLibConfigTool/Api32/$name" } | Select-Object -First 1
        if (-not $e) { throw "$name not found in $zip" }
        $out = Join-Path "$pkg\EdiabasLib" $name
        [IO.Compression.ZipFileExtensions]::ExtractToFile($e, $out, $true)
        if ((Get-FileHash $out -Algorithm SHA256).Hash -ne $known[$name]) { throw "$name in $zip is not the tested EdiabasLib version" }
    }
} finally { $z.Dispose() }

Copy-Item 'third-party\EdiabasLib\license.md' "$pkg\EdiabasLib\LICENSE (EdiabasLib).txt"
Copy-Item 'build\EdiaHelper.exe', 'config\EdiabasLib-INPA.config', 'config\EdiabasLib-ISTA.config',
          'install\Install.cmd', 'install\Install.ps1', 'install\Uninstall.cmd', 'install\Uninstall.ps1', 'install\Elevate.ps1', 'install\README.txt' $pkg
Copy-Item 'LICENSE' "$pkg\LICENSE.txt"
Copy-Item 'THIRD-PARTY-NOTICES.txt' $pkg
Write-Host "Package ready: $pkg"
