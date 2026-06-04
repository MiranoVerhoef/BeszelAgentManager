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
Source: "{#DistDir}\*"; DestDir: "{app}\app"; Flags: ignoreversion recursesubdirs createallsubdirs

[InstallDelete]
Type: filesandordirs; Name: "{app}\*"
Type: files; Name: "{commonprograms}\{#AppName}.lnk"

[UninstallRun]
Filename: "{cmd}"; Parameters: "/C taskkill /IM ""BeszelAgentManager.exe"" /T /F"; Flags: runhidden waituntilterminated; RunOnceId: "StopBeszelAgentManager"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
Type: files; Name: "{commonprograms}\{#AppName}.lnk"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\app\BeszelAgentManager.exe"; WorkingDir: "{app}\app"

[Run]
Filename: "{app}\app\BeszelAgentManager.exe"; Description: "Launch {#AppName}"; WorkingDir: "{app}\app"; Flags: nowait postinstall skipifsilent
