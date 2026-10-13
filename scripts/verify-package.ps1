#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$root = Join-Path $repo 'artifacts/1.0.0-release'
function Check([bool]$Passed, [string]$Label) { if (!$Passed) { throw $Label }; Write-Output "PASS $Label" }
$published = @(Get-ChildItem -LiteralPath (Join-Path $root 'setup') -File)
Check ($published.Count -eq 1 -and $published[0].Name -eq 'TouchLock.Setup.exe') 'standalone installer has no external runtime files'
foreach ($path in @($published[0].FullName) + @('TouchLock','TouchLock.Service','TouchLock.Recovery' | ForEach-Object { Join-Path $root "payload/$_.exe" })) {
 $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
 Check ($version.FileVersion -eq '1.0.0.0' -and $version.ProductVersion.Split('+')[0] -eq '1.0.0') "1.0.0 file and product version $([IO.Path]::GetFileName($path))"
}
foreach ($name in @('TouchLock','TouchLock.Service','TouchLock.Recovery')) {
 $path = Join-Path $root "payload/$name.runtimeconfig.json"
 $config = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
 Check ($config.runtimeOptions.includedFrameworks.Count -eq 2 -and !$config.runtimeOptions.frameworks) "$name runtime self contained"
}
Check (@(Get-ChildItem -LiteralPath (Join-Path $root 'payload') -Recurse -File | Where-Object Extension -in @('.sys','.inf','.cat')).Count -eq 0) 'no custom driver payload'
foreach ($name in @('TouchLock','TouchLock.Service')) {
 $bytes = [IO.File]::ReadAllBytes((Join-Path $root "payload/$name.dll"))
 $text = [Text.Encoding]::Unicode.GetString($bytes)
 Check (!$text.Contains('--test-authentication') -and !$text.Contains('test-pause') -and !$text.Contains('--preview-ui') -and !$text.Contains('--probe-input')) "$name release excludes test controls"
}
$archive = Join-Path $repo 'artifacts/TouchLock-1.0.0-source.zip'
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
 $entry = $zip.GetEntry('TouchLock-1.0.0-source/SHA256SUMS.txt')
 $reader = [IO.StreamReader]::new($entry.Open())
 try { $manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
 $verified = 0
 foreach ($line in ($manifest -split "`n" | Where-Object { $_.Trim() })) {
  $parts = $line.Trim() -split '  ',2
  $source = $zip.GetEntry('TouchLock-1.0.0-source/' + $parts[1])
  if (!$source) { throw "Missing source $($parts[1])" }
  $data = $source.Open()
  try { $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($data)).ToLowerInvariant() } finally { $data.Dispose() }
  if ($digest -ne $parts[0]) { throw "Source checksum mismatch $($parts[1])" }; $verified++
 }
 Check ($verified -gt 30) 'all source entries match SHA256 manifest'
 Check (@($zip.Entries | Where-Object { $_.FullName -match '(TouchLock.Driver|TouchLock.AuthHost|/bin/|/obj/|\.tools|password.json)' }).Count -eq 0) 'source excludes obsolete code, dependencies and password configuration'
} finally { $zip.Dispose() }
foreach ($line in (Get-Content -LiteralPath (Join-Path $repo 'artifacts/TouchLock-1.0.0-SHA256SUMS.txt'))) {
 $parts = $line -split '  ',2
 Check ((Get-FileHash -LiteralPath (Join-Path $repo ('artifacts/' + $parts[1]))).Hash.ToLowerInvariant() -eq $parts[0]) "delivery checksum $($parts[1])"
}
