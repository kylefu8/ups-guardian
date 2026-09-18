[CmdletBinding()]
param([string]$InnoCompiler)
$ErrorActionPreference = 'Stop'
if (-not $InnoCompiler) { $InnoCompiler = & (Join-Path $PSScriptRoot 'scripts\get-inno-setup.ps1') }
$release = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'version.json') -Raw | ConvertFrom-Json
$runtime = Join-Path $PSScriptRoot 'artifacts\windows-x64'
$identity = 'UGInstallerTest-' + [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path $PSScriptRoot ('artifacts\tests\' + $identity)
$installDirectory = Join-Path $testRoot 'installed app'
$mutexName = 'Local\' + $identity
$registryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\' + $identity + '_is1'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) ($identity + '\UPS Guardian.lnk')
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) ($identity + '.lnk')
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
# Only identity/mutex/menu are changed: use the production installer code and payload.
& $InnoCompiler '/Qp' "/DAppVersion=$($release.version)" "/DAssemblyVersion=$($release.assemblyVersion)" "/DRuntimeDirectory=$runtime" "/DOutputDirectory=$testRoot" "/DGuardianAppId=$identity" "/DGuardianMutex=$mutexName" "/DGuardianGroup=$identity" (Join-Path $PSScriptRoot 'installer\UPSGuardian.iss')
if ($LASTEXITCODE -ne 0) { throw 'Test installer compilation failed.' }
$setup = Join-Path $testRoot "UPSGuardian-$($release.version)-windows-x64-setup.exe"
$uninstaller = Join-Path $installDirectory 'unins000.exe'
function Assert-True($Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Run-Installer([string]$Executable, [string[]]$Arguments, [bool]$ExpectSuccess = $true) {
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(45000)) { throw 'Installer did not finish within 45 seconds. Inspect the isolated test process.' }
    $process.Refresh()
    Assert-True (($process.ExitCode -eq 0) -eq $ExpectSuccess) "Unexpected installer exit: $($process.ExitCode); expected success=$ExpectSuccess"
}
$setupArguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/DIR="' + $installDirectory + '"'),'/LANG=english')
$uninstallArguments = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART')
$runtimeFiles = @('UPSGuardian.exe','UPSGuardian.Updater.exe','README.md','LICENSE','CHANGELOG.md')
try {
    $mutex = [Threading.Mutex]::new($false, $mutexName)
    try {
        Run-Installer $setup ($setupArguments + ('/LOG="' + (Join-Path $testRoot 'running.log') + '"')) $false
        Assert-True (-not (Test-Path -LiteralPath $uninstaller)) 'Running app must block installation.'
    } finally { $mutex.Dispose() }
    Run-Installer $setup ($setupArguments + ('/LOG="' + (Join-Path $testRoot 'install.log') + '"'))
    Assert-True (Test-Path -LiteralPath $uninstaller) 'Missing uninstaller.'
    Assert-True (Test-Path -LiteralPath $registryPath) 'Missing current-user uninstall entry.'
    Assert-True (Test-Path -LiteralPath $shortcut) 'Missing Start menu shortcut.'
    Assert-True (-not (Test-Path -LiteralPath $desktopShortcut)) 'Desktop shortcut must be opt-in.'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $installDirectory 'data'))) 'Fresh setup must not ship or create a profile.'
    foreach ($file in $runtimeFiles) {
        Assert-True ((Get-FileHash (Join-Path $installDirectory $file)).Hash -eq (Get-FileHash (Join-Path $runtime $file)).Hash) "Installed file differs: $file"
    }
    $data = Join-Path $installDirectory 'data'
    New-Item -ItemType Directory -Path $data | Out-Null
    $settings = Join-Path $data 'settings.xml'
    $recovery = Join-Path $data 'recovery.xml'
    $profile = '<GuardSettings><StartAtLogon>false</StartAtLogon><LoadThreshold>750</LoadThreshold></GuardSettings>'
    Set-Content -LiteralPath $settings -Value $profile -Encoding utf8
    Set-Content -LiteralPath (Join-Path $data 'ui.xml') -Value '<UiPreferences><Language>zh-CN</Language></UiPreferences>' -Encoding utf8
    Set-Content -LiteralPath (Join-Path $data 'events.log') -Value 'installer test event'
    $savedHashes = @{}; Get-ChildItem -LiteralPath $data -File | ForEach-Object { $savedHashes[$_.Name] = (Get-FileHash -LiteralPath $_.FullName).Hash }
    Set-Content -LiteralPath $recovery -Value 'test-only pending recovery'
    Run-Installer $setup $setupArguments $false
    Run-Installer $uninstaller $uninstallArguments $false
    Assert-True (Test-Path -LiteralPath $recovery) 'Recovery record was removed.'
    # Single known test file, never any actual profile or recovery record.
    Remove-Item -LiteralPath $recovery
    $mutex = [Threading.Mutex]::new($false, $mutexName)
    try { Run-Installer $uninstaller $uninstallArguments $false } finally { $mutex.Dispose() }
    Set-Content -LiteralPath $settings -Value $profile.Replace('false', 'true') -Encoding utf8
    Run-Installer $uninstaller $uninstallArguments $false
    Set-Content -LiteralPath $settings -Value '<invalid'
    Run-Installer $uninstaller $uninstallArguments $false
    Set-Content -LiteralPath $settings -Value $profile -Encoding utf8
    # Repair an older/corrupt runtime while preserving the exact user-data bytes.
    Set-Content -LiteralPath (Join-Path $installDirectory 'UPSGuardian.exe') -Value 'old runtime'
    Run-Installer $setup ($setupArguments + '/TASKS=desktopicon' + ('/LOG="' + (Join-Path $testRoot 'upgrade.log') + '"'))
    Assert-True (Test-Path -LiteralPath $desktopShortcut) 'Selected desktop shortcut was not created.'
    foreach ($file in $runtimeFiles) {
        Assert-True ((Get-FileHash (Join-Path $installDirectory $file)).Hash -eq (Get-FileHash (Join-Path $runtime $file)).Hash) "Reinstall did not restore: $file"
    }
    foreach ($name in $savedHashes.Keys) { Assert-True ((Get-FileHash (Join-Path $data $name)).Hash -eq $savedHashes[$name]) "Upgrade changed $name" }
    Run-Installer $uninstaller ($uninstallArguments + ('/LOG="' + (Join-Path $testRoot 'uninstall.log') + '"'))
    foreach ($file in $runtimeFiles) { Assert-True (-not (Test-Path -LiteralPath (Join-Path $installDirectory $file))) "Uninstall retained $file" }
    Assert-True (-not (Test-Path -LiteralPath $registryPath)) 'Uninstall retained its registry entry.'
    Assert-True (-not (Test-Path -LiteralPath $shortcut)) 'Uninstall retained its Start menu shortcut.'
    Assert-True (-not (Test-Path -LiteralPath $desktopShortcut)) 'Uninstall retained its desktop shortcut.'
    foreach ($name in $savedHashes.Keys) { Assert-True ((Get-FileHash (Join-Path $data $name)).Hash -eq $savedHashes[$name]) "Uninstall changed $name" }
    Write-Output 'PASS: isolated install, current-user registration, shortcut, runtime repair, running/recovery/startup guards and uninstall; settings, language and events preserved. No app or power actions launched.'
} finally {
    # Clean up only this GUID-scoped test installation if an assertion failed.
    if (Test-Path -LiteralPath $registryPath) {
        Write-Warning "Test registration remains for inspection: $registryPath; sandbox: $testRoot"
    }
}
