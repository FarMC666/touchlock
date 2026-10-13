#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$archive = Join-Path $repo 'artifacts/TouchLock-1.0.0-source.zip'
$files = [Collections.Generic.List[IO.FileInfo]]::new()
foreach ($name in @('README.md','.gitignore','Directory.Build.props','NuGet.Config','global.json','TouchLock.sln')) { $files.Add((Get-Item -LiteralPath (Join-Path $repo $name))) }
foreach ($folder in @('src','tests','scripts','docs')) {
 foreach ($file in (Get-ChildItem -LiteralPath (Join-Path $repo $folder) -Recurse -File)) {
  $relative = [IO.Path]::GetRelativePath($repo, $file.FullName).Replace('\','/')
  if ($relative -match '(^|/)(bin|obj)(/|$)' -or $file.Extension -notin @('.cs','.csproj','.ps1','.md','.manifest')) { continue }
  if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked source not allowed' }
  $files.Add($file)
 }
}
$stream = [IO.File]::Open($archive, [IO.FileMode]::Create, [IO.FileAccess]::Write)
$zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
 $checksums = [Collections.Generic.List[string]]::new()
 foreach ($file in ($files | Sort-Object FullName)) {
  $relative = [IO.Path]::GetRelativePath($repo, $file.FullName).Replace('\','/')
  [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, "TouchLock-1.0.0-source/$relative") | Out-Null
  $checksums.Add("$((Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant())  $relative")
 }
 $entry = $zip.CreateEntry('TouchLock-1.0.0-source/SHA256SUMS.txt'); $output = $entry.Open()
 try { $bytes = [Text.Encoding]::UTF8.GetBytes(($checksums -join "`n") + "`n"); $output.Write($bytes) } finally { $output.Dispose() }
} finally { $zip.Dispose(); $stream.Dispose() }
$lines = foreach ($name in @('TouchLock-1.0.0-Setup-x64.exe','TouchLock-1.0.0-source.zip')) { $file = Join-Path $repo "artifacts/$name"; if (Test-Path -LiteralPath $file) { "$((Get-FileHash -LiteralPath $file).Hash.ToLowerInvariant())  $name" } }
[IO.File]::WriteAllLines((Join-Path $repo 'artifacts/TouchLock-1.0.0-SHA256SUMS.txt'), $lines)
Write-Output "Source archive: $archive"
