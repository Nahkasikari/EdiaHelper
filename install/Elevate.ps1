# Starts Install.ps1 or Uninstall.ps1 with administrator rights.
# The user's own profile and account are passed on, so shortcuts and file permissions go to the right
# user even if a different administrator account approves the prompt. Values are passed as properly
# quoted arguments, so folder or user names with apostrophes work too.
param([Parameter(Mandatory = $true)][ValidateSet('Install.ps1', 'Uninstall.ps1')][string]$Script)

$path = Join-Path $PSScriptRoot $Script
$userName = "$env:USERDOMAIN\$env:USERNAME"
function Q([string]$s) { '"' + $s.Replace('"', '') + '"' }
$arguments = '-NoProfile -ExecutionPolicy Bypass -File ' + (Q $path) + ' -UserProfile ' + (Q $env:USERPROFILE) + ' -UserName ' + (Q $userName)
Start-Process powershell -Verb RunAs -ArgumentList $arguments
