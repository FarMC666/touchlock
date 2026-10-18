#Requires -Version 7.0
#Requires -RunAsAdministrator
param([string]$InstallDirectory, [switch]$UninstallOnly, [switch]$AuthenticationOnly, [switch]$VerifySettingsUi)
$repo = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$mode = if ($UninstallOnly) { 'uninstall' } elseif ($AuthenticationOnly) { 'authentication' } else { '1.0' }
$result = Join-Path $repo "artifacts/admin-$mode-test.txt"
try {
 $parameters = @{}
 if ($InstallDirectory) { $parameters.InstallDirectory = $InstallDirectory }
 if ($UninstallOnly) { $parameters.UninstallOnly = $true }
 if ($AuthenticationOnly) { $parameters.AuthenticationOnly = $true }
 if ($VerifySettingsUi) { $parameters.VerifySettingsUi = $true }
 "Mode=$mode; VerifySettingsUi=$VerifySettingsUi; InstallDirectory=$InstallDirectory" | Set-Content -LiteralPath $result
 & (Join-Path $PSScriptRoot 'test-admin.ps1') @parameters *>> $result
 if (!$?) { exit 1 }
 Add-Content -LiteralPath $result -Value 'ALL REQUESTED ADMIN TESTS PASSED'; exit 0
}
catch { Add-Content -LiteralPath $result -Value $_.Exception.Message; exit 1 }
