#define AppName "BeszelAgentManager"
#ifndef AppVersion
#define AppVersion "0.0.0"
#endif
#ifndef DistDir
#define DistDir "..\build\main.dist"
#endif

[Setup]
AppId={{8E3ED77F-F8A2-4D8C-8D5F-0F5295E1B10D}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Verhoef
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\installer-dist
OutputBaseFilename={#AppName}Setup
SetupIconFile=..\BeszelAgentManager_icon.ico
WizardSmallImageFile=WizardSmallImage.bmp
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
CloseApplications=yes
CloseApplicationsFilter=BeszelAgentManager.exe
RestartApplications=no
UninstallDisplayIcon={app}\app\BeszelAgentManager.exe
VersionInfoCompany=Verhoef
VersionInfoDescription={#AppName} Installer
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
VersionInfoVersion={#AppVersion}.0

[Files]
Source: "{#DistDir}\*"; DestDir: "{app}\app"; Excludes: "nssm.exe"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#DistDir}\nssm.exe"; DestDir: "{app}"; Flags: ignoreversion

[Dirs]
Name: "{commonappdata}\{#AppName}"; Permissions: users-modify

[Tasks]
Name: "cleanlegacyroot"; Description: "Stop old manager and clean legacy Program Files layout"; GroupDescription: "Migration:"; Check: IsLegacyRootInstall; Flags: checkedonce
Name: "startmenuicon"; Description: "Create a Start Menu shortcut"; GroupDescription: "Shortcuts:"; Flags: checkedonce
Name: "desktopicon"; Description: "Create a Desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[InstallDelete]
Type: filesandordirs; Name: "{app}\*"
Type: files; Name: "{commonprograms}\{#AppName}.lnk"

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM ""BeszelAgentManager.exe"" /T /F"; Flags: runhidden waituntilterminated; RunOnceId: "StopBeszelAgentManager"
Filename: "{cmd}"; Parameters: "/C ping 127.0.0.1 -n 3 >NUL & rmdir /S /Q ""{app}"""; Flags: runhidden nowait; RunOnceId: "RemoveBeszelAgentManagerFolder"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
Type: files; Name: "{commonprograms}\{#AppName}.lnk"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\app\BeszelAgentManager.exe"; WorkingDir: "{app}\app"; Tasks: startmenuicon
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\app\BeszelAgentManager.exe"; WorkingDir: "{app}\app"; Tasks: desktopicon

[Run]
Filename: "{cmd}"; Parameters: "/C icacls ""{commonappdata}\{#AppName}"" /inheritance:e /grant *S-1-5-32-545:(OI)(CI)M *S-1-5-11:(OI)(CI)M /T /C"; Flags: runhidden waituntilterminated
Filename: "{cmd}"; Parameters: "/C if exist ""{autopf}\Beszel-Agent"" icacls ""{autopf}\Beszel-Agent"" /inheritance:e /grant *S-1-5-32-545:(OI)(CI)RX *S-1-5-11:(OI)(CI)RX *S-1-5-32-544:(OI)(CI)F *S-1-5-18:(OI)(CI)F /T /C"; Flags: runhidden waituntilterminated
Filename: "{app}\app\BeszelAgentManager.exe"; WorkingDir: "{app}\app"; Flags: nowait skipifsilent runasoriginaluser

[Code]
function IsLegacyRootInstall(): Boolean;
begin
  Result :=
    FileExists(ExpandConstant('{app}\BeszelAgentManager.exe')) or
    FileExists(ExpandConstant('{app}\python3.dll')) or
    FileExists(ExpandConstant('{app}\python312.dll')) or
    FileExists(ExpandConstant('{app}\python313.dll')) or
    FileExists(ExpandConstant('{app}\python314.dll')) or
    DirExists(ExpandConstant('{app}\charset_normalizer')) or
    DirExists(ExpandConstant('{app}\PIL'));
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  if WizardIsTaskSelected('cleanlegacyroot') then
  begin
    Exec(
      ExpandConstant('{cmd}'),
      '/C taskkill /IM "BeszelAgentManager.exe" /T /F',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode
    );
  end;
end;
