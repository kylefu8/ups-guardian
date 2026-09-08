$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1')
$release = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'version.json') -Raw | ConvertFrom-Json
$runtime = Join-Path $PSScriptRoot 'artifacts\windows-x64'
$distribution = Join-Path $PSScriptRoot 'artifacts\release'
New-Item -ItemType Directory -Force -Path $distribution | Out-Null
$archive = Join-Path $distribution "UPSGuardian-$($release.version)-windows-x64.zip"
# Explicit allowlist keeps local settings, recovery records and logs out of releases.
$files = @('UPSGuardian.exe','UPSGuardian.Updater.exe','README.md','LICENSE','CHANGELOG.md') | ForEach-Object { Join-Path $runtime $_ }
Compress-Archive -LiteralPath $files -DestinationPath $archive -Force
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath (Join-Path $distribution 'SHA256SUMS.txt') -Value "$hash  $([IO.Path]::GetFileName($archive))" -Encoding ascii
Write-Output $archive
