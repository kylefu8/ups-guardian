[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectDirectory = $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectDirectory 'artifacts\windows-x64' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$release = Get-Content -LiteralPath (Join-Path $projectDirectory 'version.json') -Raw | ConvertFrom-Json
if ($release.version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$' -or $release.assemblyVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'Invalid version.json' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Windows .NET Framework 4.8 compiler is required.' }
$generatedDirectory = Join-Path $projectDirectory 'source\Generated'
New-Item -ItemType Directory -Force -Path $OutputDirectory,$generatedDirectory | Out-Null
$versionSource = Join-Path $generatedDirectory 'BuildInfo.g.cs'
@"
using System.Reflection;
using System.Runtime.Versioning;
[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName=".NET Framework 4.8")]
[assembly: AssemblyTitle("UPS Guardian")]
[assembly: AssemblyProduct("UPS Guardian")]
[assembly: AssemblyCompany("kylefu8")]
[assembly: AssemblyCopyright("Copyright (c) 2026 kylefu8")]
[assembly: AssemblyVersion("$($release.assemblyVersion)")]
[assembly: AssemblyFileVersion("$($release.assemblyVersion)")]
[assembly: AssemblyInformationalVersion("$($release.version)")]
namespace UpsGuardian { public static class BuildInfo { public const string Version = "$($release.version)"; public const string Repository = "kylefu8/ups-guardian"; } }
"@ | Set-Content -LiteralPath $versionSource -Encoding utf8
$common = @('/nologo','/platform:x64','/optimize+','/reference:System.Core.dll','/reference:System.Xml.dll','/reference:System.Runtime.Serialization.dll','/reference:System.IO.Compression.dll','/reference:System.IO.Compression.FileSystem.dll')
$resources = @()
foreach ($language in @('zh-CN','en','ja','ko','fr','de','es')) {
    $localePath = Join-Path $projectDirectory "locales\$language.json"
    if (-not (Test-Path -LiteralPath $localePath)) { throw "Missing locale: $language" }
    $resources += "/resource:$localePath,Guardian.Locale.$language.json"
}
$assetsDirectory = Join-Path $projectDirectory 'assets'
$resources += "/resource:$assetsDirectory\app.ico,Guardian.Icon"
$resources += "/resource:$assetsDirectory\app-icon.png,Guardian.Image"
$resources += "/resource:$projectDirectory\CHANGELOG.md,Guardian.Changelog"
foreach ($provider in @('wechat','alipay')) {
    $donationImage = Join-Path $assetsDirectory "donations\$provider.png"
    if (Test-Path -LiteralPath $donationImage) {
        if ((Get-Item -LiteralPath $donationImage).Length -gt 2MB) { throw "Donation image too large: $provider" }
        $resources += "/resource:$donationImage,Guardian.Donation.$provider"
    }
}
$coreFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectDirectory 'source\Core') -Filter '*.cs' | Sort-Object Name | ForEach-Object FullName)
$windowsFiles = @(Get-ChildItem -LiteralPath (Join-Path $projectDirectory 'source\Windows') -Filter '*.cs' | Sort-Object Name | ForEach-Object FullName)
$guiArguments = $common + @('/target:winexe','/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll','/reference:Microsoft.CSharp.dll',"/win32manifest:$projectDirectory\source\app.manifest","/win32icon:$assetsDirectory\app.ico","/out:$OutputDirectory\UPSGuardian.exe") + $resources + $coreFiles + $windowsFiles + @($versionSource)
& $compiler @guiArguments
if ($LASTEXITCODE -ne 0) { throw "GUI build failed: $LASTEXITCODE" }
$helperArguments = $common + @('/target:winexe',"/out:$OutputDirectory\UPSGuardian.Updater.exe",(Join-Path $projectDirectory 'source\Core\Updates.cs'),(Join-Path $projectDirectory 'source\Windows\UpdateInstaller.cs'),(Join-Path $projectDirectory 'source\Updater\Program.cs'),$versionSource)
& $compiler @helperArguments
if ($LASTEXITCODE -ne 0) { throw "Updater build failed: $LASTEXITCODE" }
foreach ($document in @('README.md','LICENSE','CHANGELOG.md')) { Copy-Item -LiteralPath (Join-Path $projectDirectory $document) -Destination (Join-Path $OutputDirectory $document) }
Write-Output "Built $($release.version): $OutputDirectory"
