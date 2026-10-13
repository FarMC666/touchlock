#Requires -Version 7.0
#Requires -RunAsAdministrator
param([switch]$KeepInstalled, [string]$InstallDirectory, [switch]$UninstallOnly)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$defaultInstall = Join-Path $env:ProgramFiles 'TouchLock'
$installed = if ($InstallDirectory) { [IO.Path]::GetFullPath($InstallDirectory) } else { $defaultInstall }
$setup = Join-Path $repo 'artifacts/TouchLock-1.0.0-Setup-x64-debug.exe'
if ((Get-Service TouchLock -ErrorAction SilentlyContinue) -or (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\TouchLock') -or (Test-Path -LiteralPath $defaultInstall) -or (Test-Path -LiteralPath $installed) -or (Test-Path -LiteralPath (Join-Path $env:ProgramData 'TouchLock'))) { throw 'An existing installation or configuration must be preserved; test refused.' }
$secret = [Text.Encoding]::UTF8.GetBytes([Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)))
function Run-Child([string]$Path, [string]$Argument, [byte[]]$SecretBytes, [int]$Timeout = 120000, [string[]]$AdditionalArguments = @(), [int]$ExpectedExit = 0) {
 $info = [Diagnostics.ProcessStartInfo]::new($Path)
 $info.UseShellExecute = $false; $info.CreateNoWindow = $true; $info.RedirectStandardInput = $true; $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
 $info.ArgumentList.Add($Argument)
 foreach ($argumentValue in $AdditionalArguments) { $info.ArgumentList.Add($argumentValue) }
 $process = [Diagnostics.Process]::Start($info)
 try {
  if ($SecretBytes.Length -or $Argument -in @('--test-install','--test-reset','--test-uninstall','--test-uninstall-keep')) { $size = [BitConverter]::GetBytes($SecretBytes.Length); $process.StandardInput.BaseStream.Write($size); $process.StandardInput.BaseStream.Write($SecretBytes); $process.StandardInput.BaseStream.Flush() }
  $process.StandardInput.Close()
  if (!$process.WaitForExit($Timeout)) { $process.Kill(); throw 'Child timed out' }
  $output = $process.StandardOutput.ReadToEnd(); $errorOutput = $process.StandardError.ReadToEnd()
  if ($output) { Write-Output $output.Trim() }
  if ($process.ExitCode -ne $ExpectedExit) { throw "Child exit $($process.ExitCode), expected $ExpectedExit : $errorOutput" }
 } finally { $process.Dispose() }
}
function Check([bool]$Passed, [string]$Label) { if (!$Passed) { throw $Label }; Write-Output "PASS $Label" }
try {
 Run-Child $setup '--test-install' $secret -AdditionalArguments @($installed)
 Start-Sleep -Seconds 4
 Check ((Get-Service TouchLock).Status -eq 'Running') 'actual SCM install and start'
 Check ((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\TouchLock').DisplayVersion -eq '1.0.0') 'installed application display version 1.0.0'
 Check ((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\TouchLock').InstallLocation -eq $installed) 'selected installation path persisted'
 $service = Get-CimInstance Win32_Service -Filter "Name='TouchLock'"
 Check ($service.StartName -eq 'LocalSystem' -and $service.StartMode -eq 'Manual') 'LocalSystem manual service'
 Check ($service.PathName -eq ('"' + (Join-Path $installed 'TouchLock.Service.exe') + '"')) 'SCM executable uses selected path with correct quoting'
 $installationAcl = Get-Acl -LiteralPath $installed
 Check ($installationAcl.AreAccessRulesProtected -and $installationAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value -eq 'S-1-5-32-544') 'installation ACL protected and owned by Administrators'
 $shell = New-Object -ComObject WScript.Shell
 $shortcut = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'TouchLock/TouchLock.lnk'))
 Check ($shortcut.TargetPath -eq (Join-Path $installed 'TouchLock.exe')) 'start menu shortcut uses selected path'
 [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) | Out-Null
 [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) | Out-Null
 $guard = @(Get-Process TouchLock.Recovery -ErrorAction SilentlyContinue)
 Check ($guard.Count -eq 1 -and $guard[0].SessionId -eq 0) 'independent SYSTEM recovery process'
 Run-Child (Join-Path $installed 'TouchLock.exe') '--status' @()
 $acl = Get-Acl -LiteralPath (Join-Path $env:ProgramData 'TouchLock/password.json')
 Check ($acl.AreAccessRulesProtected) 'password ACL protected'
 $tray = @(Get-Process TouchLock -ErrorAction SilentlyContinue | Where-Object SessionId -ne 0)
 Check ($tray.Count -ge 1) 'tray launched in interactive session'
 $originalPid = $service.ProcessId
 $passwordFile = Join-Path $env:ProgramData 'TouchLock/password.json'
 $originalPasswordHash = (Get-FileHash -LiteralPath $passwordFile).Hash
 Run-Child $setup '--test-uninstall' @() -ExpectedExit 10
 Check ((Get-Service TouchLock).Status -eq 'Running' -and (Get-CimInstance Win32_Service -Filter "Name='TouchLock'").ProcessId -eq $originalPid -and (Test-Path -LiteralPath $installed)) 'wrong uninstall password leaves service and installation intact'
 Run-Child $setup '--test-uninstall-cancel' @()
 Check ((Get-Service TouchLock).Status -eq 'Running' -and (Get-CimInstance Win32_Service -Filter "Name='TouchLock'").ProcessId -eq $originalPid -and (Get-FileHash -LiteralPath $passwordFile).Hash -eq $originalPasswordHash) 'cancelled uninstall dialog leaves service and password intact'
 $missing = $passwordFile + '.uninstall-test'
 if (Test-Path -LiteralPath $missing) { throw 'Password test backup already exists' }
 [IO.File]::Move($passwordFile, $missing)
 try { Run-Child $setup '--test-uninstall' $secret -ExpectedExit 11 } finally { [IO.File]::Move($missing, $passwordFile) }
 Check ((Get-Service TouchLock).Status -eq 'Running' -and (Get-CimInstance Win32_Service -Filter "Name='TouchLock'").ProcessId -eq $originalPid) 'missing password configuration cannot stop service or uninstall'
 $record = [IO.File]::ReadAllBytes($passwordFile)
 try { [IO.File]::WriteAllText($passwordFile, '{}'); Run-Child $setup '--test-uninstall' $secret -ExpectedExit 11 }
 finally { [IO.File]::WriteAllBytes($passwordFile, $record); [Security.Cryptography.CryptographicOperations]::ZeroMemory($record) }
 Check ((Get-Service TouchLock).Status -eq 'Running' -and (Get-CimInstance Win32_Service -Filter "Name='TouchLock'").ProcessId -eq $originalPid -and (Get-FileHash -LiteralPath $passwordFile).Hash -eq $originalPasswordHash) 'corrupt password configuration cannot stop service or uninstall'
 if (!$UninstallOnly) {
 $oldTray = $tray[0].Id; Stop-Process -Id $oldTray -Force
 Start-Sleep -Seconds 12
 Check (@(Get-Process TouchLock -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -ne 0 -and $_.Id -ne $oldTray }).Count -ge 1) 'tray relaunched without service exit'
 $oldService = [int]$service.ProcessId; $oldGuard = $guard[0].Id
 Stop-Process -Id $oldService -Force
 Start-Sleep -Seconds 14
 $service = Get-CimInstance Win32_Service -Filter "Name='TouchLock'"
 Check ($service.State -eq 'Running' -and $service.ProcessId -ne $oldService) 'SCM restarts failed service'
 Check (!(Get-Process -Id $oldGuard -ErrorAction SilentlyContinue)) 'guardian exits after recovering service crash'
 $guard = @(Get-Process TouchLock.Recovery -ErrorAction SilentlyContinue)
 Stop-Process -Id $guard[0].Id -Force
 Start-Sleep -Seconds 3
 $statusOutput = Run-Child (Join-Path $installed 'TouchLock.exe') '--status' @()
 $status = $statusOutput | ConvertFrom-Json
 Check (!$status.Status.Available) 'guardian death disables new locks'
 Stop-Service TouchLock; Start-Service TouchLock; Start-Sleep -Seconds 4
 $oldService = [int](Get-CimInstance Win32_Service -Filter "Name='TouchLock'").ProcessId
 Run-Child (Join-Path $installed 'TouchLock.exe') '--test-pause' @()
 Start-Sleep -Seconds 35
 $service = Get-CimInstance Win32_Service -Filter "Name='TouchLock'"
 Check ($service.State -eq 'Running' -and $service.ProcessId -ne $oldService) '30 second heartbeat expiry terminates old service and SCM restarts'
 Run-Child $setup '--test-reset' $secret
 Start-Sleep -Seconds 4
 Check ((Get-Service TouchLock).Status -eq 'Running') 'administrator maintenance resets password after recovery'
 Run-Child (Join-Path $installed 'TouchLock.exe') '--test-authentication' $secret
 Start-Sleep -Seconds 3
 Check ((Get-Service TouchLock).Status -eq 'Stopped') 'authenticated exit stops service'
 Check (@(Get-Process TouchLock.Recovery -ErrorAction SilentlyContinue).Count -eq 0) 'recovery process stops after orderly exit'
 }
 if (!$KeepInstalled) {
  Run-Child $setup '--test-uninstall-keep' $secret
  Check (!(Get-Service TouchLock -ErrorAction SilentlyContinue) -and !(Test-Path -LiteralPath $installed)) 'actual uninstall removes service and files'
  Check (Test-Path -LiteralPath (Join-Path $env:ProgramData 'TouchLock/password.json')) 'default uninstall retains password configuration'
  Run-Child $setup '--test-install' $secret -AdditionalArguments @($installed)
  Start-Sleep -Seconds 3
  Run-Child $setup '--test-uninstall' $secret
  Check (!(Test-Path -LiteralPath (Join-Path $env:ProgramData 'TouchLock'))) 'explicit configuration deletion'
 }
} finally {
 try {
  if (!$KeepInstalled -and ((Get-Service TouchLock -ErrorAction SilentlyContinue) -or (Test-Path -LiteralPath $installed))) {
   try { Run-Child $setup '--test-uninstall' $secret }
   catch { Run-Child $setup '--test-reset' $secret; Run-Child $setup '--test-uninstall' $secret }
  }
 } finally { [Security.Cryptography.CryptographicOperations]::ZeroMemory($secret) }
}
