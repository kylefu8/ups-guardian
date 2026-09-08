# Compatibility entry point; artifacts are produced outside the running application.
& (Join-Path $PSScriptRoot '..\build.ps1') @args
