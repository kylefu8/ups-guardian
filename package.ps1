[CmdletBinding()]
param([string]$InnoCompiler)
$ErrorActionPreference = 'Stop'
if (-not $InnoCompiler) { $InnoCompiler = & (Join-Path $PSScriptRoot 'scripts\get-inno-setup.ps1') }
if (-not (Test-Path -LiteralPath $InnoCompiler)) { throw 'Inno Setup compiler not found. Use -InnoCompiler with the path to ISCC.exe.' }
& (Join-Path $PSScriptRoot 'build.ps1')
$release = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'version.json') -Raw | ConvertFrom-Json
$runtime = Join-Path $PSScriptRoot 'artifacts\windows-x64'
$distribution = Join-Path $PSScriptRoot 'artifacts\release'
New-Item -ItemType Directory -Force -Path $distribution | Out-Null
$archive = Join-Path $distribution "UPSGuardian-$($release.version)-windows-x64.zip"
# Explicit allowlist keeps local settings, recovery records and logs out of releases.
$files = @('UPSGuardian.exe','UPSGuardian.Updater.exe','README.md','LICENSE','CHANGELOG.md') | ForEach-Object { Join-Path $runtime $_ }
Compress-Archive -LiteralPath $files -DestinationPath $archive -Force
$notes = Join-Path $PSScriptRoot "docs\releases\$($release.version).md"
if (-not (Test-Path -LiteralPath $notes)) { throw "Missing release notes: $notes" }
$noteText = Get-Content -LiteralPath $notes -Raw
if ($noteText -notmatch '(?m)^## 中文' -or $noteText -notmatch '(?m)^## English') { throw 'Release notes must include Chinese and English sections.' }
Copy-Item -LiteralPath $notes -Destination (Join-Path $distribution 'RELEASE_NOTES.md')
& $InnoCompiler '/Qp' "/DAppVersion=$($release.version)" "/DAssemblyVersion=$($release.assemblyVersion)" "/DRuntimeDirectory=$runtime" "/DOutputDirectory=$distribution" (Join-Path $PSScriptRoot 'installer\UPSGuardian.iss')
if ($LASTEXITCODE -ne 0) { throw 'Installer build failed.' }
$setup = Join-Path $distribution "UPSGuardian-$($release.version)-windows-x64-setup.exe"
$checksums = @($archive, $setup) | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $([IO.Path]::GetFileName($_))"
}
Set-Content -LiteralPath (Join-Path $distribution 'SHA256SUMS.txt') -Value $checksums -Encoding ascii
Write-Output $archive
Write-Output $setup
