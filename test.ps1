$ErrorActionPreference = 'Stop'
$projectDirectory = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testDirectory = Join-Path $projectDirectory 'artifacts\tests'
New-Item -ItemType Directory -Force -Path $testDirectory | Out-Null
$core = @(Get-ChildItem -LiteralPath (Join-Path $projectDirectory 'source\Core') -Filter '*.cs' | Sort-Object Name | ForEach-Object FullName)
$platform = @((Join-Path $projectDirectory 'source\Windows\PowerActions.cs'),(Join-Path $projectDirectory 'source\Windows\UpdateInstaller.cs'))
$references = @('/nologo','/target:exe','/reference:System.Core.dll','/reference:System.Xml.dll','/reference:System.Runtime.Serialization.dll','/reference:System.IO.Compression.dll','/reference:System.IO.Compression.FileSystem.dll')
$resources = @(Get-ChildItem -LiteralPath (Join-Path $projectDirectory 'locales') -Filter '*.json' | ForEach-Object { "/resource:$($_.FullName),Guardian.Locale.$($_.Name)" })
foreach ($testSource in (Get-ChildItem -LiteralPath (Join-Path $projectDirectory 'tests') -Filter '*Tests.cs' | Sort-Object Name)) {
    $testExecutable = Join-Path $testDirectory ($testSource.BaseName + '.exe')
    & $compiler @references @resources "/out:$testExecutable" @core @platform $testSource.FullName
    if ($LASTEXITCODE -ne 0) { throw "Compile failed: $($testSource.Name)" }
    $logPath = Join-Path $testDirectory ($testSource.BaseName + '.log')
    & $testExecutable *> $logPath
    if ($LASTEXITCODE -ne 0) { Get-Content -LiteralPath $logPath -Tail 60; throw "Test failed: $($testSource.Name)" }
    Write-Output "$($testSource.BaseName): $(Get-Content -LiteralPath $logPath -Tail 1)"
}
Write-Output 'All simulated tests passed. No power limits or sleep operations were invoked.'
