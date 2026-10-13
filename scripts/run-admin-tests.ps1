#Requires -Version 7.0
#Requires -RunAsAdministrator
$repo = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$result = Join-Path $repo 'artifacts/admin-1.0-test.txt'
try { & (Join-Path $PSScriptRoot 'test-admin.ps1') *> $result; if (!$?) { exit 1 }; Add-Content -LiteralPath $result -Value 'ALL ADMIN TESTS PASSED'; exit 0 }
catch { Add-Content -LiteralPath $result -Value $_.Exception.Message; exit 1 }
