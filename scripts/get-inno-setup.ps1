[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
# Keep the compiler project-local, with no shortcuts, associations or uninstall entry.
$toolsDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\tools'
$compilerDirectory = Join-Path $toolsDirectory 'inno-6.7.3'
$compiler = Join-Path $compilerDirectory 'ISCC.exe'
if (Test-Path -LiteralPath $compiler) { Write-Output $compiler; return }
New-Item -ItemType Directory -Force -Path $toolsDirectory | Out-Null
$setup = Join-Path $toolsDirectory 'innosetup-6.7.3.exe'
if (-not (Test-Path -LiteralPath $setup)) {
    Invoke-WebRequest 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe' -OutFile $setup
}
if ((Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash -ne '9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732') {
    throw 'Inno Setup compiler download checksum mismatch.'
}
$process = Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/CURRENTUSER','/PORTABLE=1',('/DIR="' + $compilerDirectory + '"')) -WindowStyle Hidden -PassThru -Wait
if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $compiler)) { throw 'Portable Inno Setup extraction failed.' }
Write-Output $compiler
