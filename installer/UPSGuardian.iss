; Build with package.ps1. Test builds override identity to avoid installed profiles.
#ifndef GuardianAppId
  #define GuardianAppId "UPSGuardian"
#endif
#ifndef GuardianMutex
  #define GuardianMutex "Local\KyleUPSGuardian"
#endif
#ifndef GuardianGroup
  #define GuardianGroup "UPS Guardian"
#endif

[Setup]
AppId={#GuardianAppId}
AppName=UPS Guardian
AppVersion={#AppVersion}
AppVerName=UPS Guardian {#AppVersion}
AppPublisher=kylefu8
AppPublisherURL=https://github.com/kylefu8/ups-guardian
AppSupportURL=https://github.com/kylefu8/ups-guardian/issues
AppUpdatesURL=https://github.com/kylefu8/ups-guardian/releases
DefaultDirName={localappdata}\Programs\UPS Guardian
DefaultGroupName={#GuardianGroup}
DisableProgramGroupPage=yes
DisableDirPage=no
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir={#OutputDirectory}
OutputBaseFilename=UPSGuardian-{#AppVersion}-windows-x64-setup
VersionInfoVersion={#AssemblyVersion}
VersionInfoProductVersion={#AssemblyVersion}
VersionInfoProductTextVersion={#AppVersion}
VersionInfoTextVersion={#AppVersion}
SetupIconFile=..\assets\app.ico
UninstallDisplayIcon={app}\UPSGuardian.exe
LicenseFile=..\LICENSE
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
CloseApplications=no
RestartApplications=no
SetupMutex={#GuardianAppId}-Setup
Uninstallable=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimplified"; MessagesFile: "languages\ChineseSimplified.isl"

[CustomMessages]
english.CloseGuardian=UPS Guardian is running. Pause protection, restore limits, then choose Exit from its tray menu before continuing.
chinesesimplified.CloseGuardian=UPS 守护仍在运行。请先暂停保护并恢复限制，再从托盘菜单退出，然后继续。
english.RestoreFirst=This installation has a pending power-recovery record. Open UPS Guardian and use Pause and restore before installing or uninstalling.
chinesesimplified.RestoreFirst=此安装目录存在待恢复的功耗记录。请先打开 UPS 守护并使用“暂停并恢复限制”，再安装或卸载。
english.DisableStartup=Turn off Start with Windows in UPS Guardian and save the setting before uninstalling. Your data folder will be kept.
chinesesimplified.DisableStartup=卸载前请在 UPS 守护中关闭“登录 Windows 后自动启动”并保存。卸载将保留 data 数据文件夹。
english.ReadSettingsFailed=Setup could not check the existing startup setting. Open UPS Guardian, turn off Start with Windows and save before uninstalling.
chinesesimplified.ReadSettingsFailed=无法检查现有启动设置。请打开 UPS 守护，关闭登录自启并保存后再卸载。
english.NeedFramework=UPS Guardian requires .NET Framework 4.8 or later. Install it before continuing.
chinesesimplified.NeedFramework=UPS 守护需要 .NET Framework 4.8 或更高版本。请安装后继续。

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; No wildcard: never distribute local settings, logs or recovery records.
Source: "{#RuntimeDirectory}\UPSGuardian.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RuntimeDirectory}\UPSGuardian.Updater.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RuntimeDirectory}\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RuntimeDirectory}\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#RuntimeDirectory}\CHANGELOG.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\UPS Guardian"; Filename: "{app}\UPSGuardian.exe"; WorkingDir: "{app}"
Name: "{group}\{cm:UninstallProgram,UPS Guardian}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#GuardianGroup}"; Filename: "{app}\UPSGuardian.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\UPSGuardian.exe"; WorkingDir: "{app}"; Description: "{cm:LaunchProgram,UPS Guardian}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[Code]
function InitializeSetup: Boolean;
begin
  Result := IsDotNetInstalled(net48, 0);
  if not Result then
    SuppressibleMsgBox(CustomMessage('NeedFramework'), mbError, MB_OK, IDOK);
end;

function GuardInstallDirectory: String;
begin
  Result := '';
  if CheckForMutexes('{#GuardianMutex}') then
    Result := CustomMessage('CloseGuardian')
  else if FileExists(ExpandConstant('{app}\data\recovery.xml')) then
    Result := CustomMessage('RestoreFirst');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := GuardInstallDirectory;
end;

function InitializeUninstall: Boolean;
var
  Problem, StartupText: String;
  Settings, Startup: Variant;
begin
  Problem := GuardInstallDirectory;
  if (Problem = '') and FileExists(ExpandConstant('{app}\data\settings.xml')) then begin
    try
      Settings := CreateOleObject('Msxml2.DOMDocument.6.0');
      Settings.async := False;
      Settings.resolveExternals := False;
      Settings.setProperty('ProhibitDTD', True);
      if not Settings.load(ExpandConstant('{app}\data\settings.xml')) then
        Problem := CustomMessage('ReadSettingsFailed')
      else begin
        Startup := Settings.selectSingleNode('/GuardSettings/StartAtLogon');
        if not VarIsNull(Startup) and not VarIsEmpty(Startup) then begin
          StartupText := Startup.text;
          if (Lowercase(Trim(StartupText)) = 'true') or (Trim(StartupText) = '1') then
            Problem := CustomMessage('DisableStartup');
        end;
      end;
    except
      Problem := CustomMessage('ReadSettingsFailed');
    end;
  end;
  Result := Problem = '';
  if not Result then
    SuppressibleMsgBox(Problem, mbError, MB_OK, IDOK);
end;
