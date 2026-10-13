#Requires -Version 7.0
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$local = Join-Path $repo '.tools/dotnet/dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $local) { $local } else { (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $repo '.tools/dotnet-home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
function Run-Dotnet { & $dotnet @args; if ($LASTEXITCODE) { throw "dotnet failed ($LASTEXITCODE)" } }
Run-Dotnet build (Join-Path $repo 'TouchLock.sln') -c $Configuration
Run-Dotnet run --project (Join-Path $repo 'tests/TouchLock.Core.Tests') -c $Configuration --no-build
$root = Join-Path $repo "artifacts/1.0.0-$($Configuration.ToLowerInvariant())"
$payload = Join-Path $root 'payload'
if (Test-Path -LiteralPath $root) {
 if (!( [IO.Path]::GetFullPath($root).StartsWith($repo + '\', [StringComparison]::OrdinalIgnoreCase))) { throw 'Invalid artifact directory' }
 Remove-Item -LiteralPath $root -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $payload | Out-Null
foreach ($name in @('App','Service','Recovery')) {
 $output = Join-Path $root "publish-$name"
 Run-Dotnet publish (Join-Path $repo "src/TouchLock.$name/TouchLock.$name.csproj") -c $Configuration -r win-x64 --self-contained true -o $output '-p:DebugType=None'
 foreach ($file in (Get-ChildItem -LiteralPath $output -File)) {
  $target = Join-Path $payload $file.Name
  if ((Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw "Conflicting runtime file: $($file.Name)" }
  Copy-Item -LiteralPath $file.FullName -Destination $target -Force
 }
}
Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination $payload
Copy-Item -LiteralPath (Join-Path $repo 'docs') -Destination $payload -Recurse
$archive = Join-Path $root 'payload.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($payload, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
$setup = Join-Path $root 'setup'
Run-Dotnet publish (Join-Path $repo 'src/TouchLock.Setup/TouchLock.Setup.csproj') -c $Configuration -r win-x64 --self-contained true -o $setup '-p:DebugType=None' "-p:TouchLockPayload=$archive"
$suffix = if ($Configuration -eq 'Debug') { '-debug' } else { '' }
$installer = Join-Path $repo "artifacts/TouchLock-1.0.0-Setup-x64$suffix.exe"
Copy-Item -LiteralPath (Join-Path $setup 'TouchLock.Setup.exe') -Destination $installer -Force
Write-Output "Installer: $installer"
if ($Configuration -eq 'Release') { & (Join-Path $PSScriptRoot 'bundle-source.ps1') }
