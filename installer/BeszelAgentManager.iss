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
Source: "{#DistDir}\*"; DestDir: "{app}\app"; Excludes: "nssm.exe,*.pdb"; Flags: replacesameversion recursesubdirs createallsubdirs
Source: "{#DistDir}\nssm.exe"; DestDir: "{commonappdata}\{#AppName}\nssm"; Flags: ignoreversion onlyifdoesntexist

[Dirs]
Name: "{commonappdata}\{#AppName}"

[Tasks]
Name: "startmenuicon"; Description: "Create a Start Menu shortcut"; GroupDescription: "Shortcuts:"; Flags: checkedonce
Name: "desktopicon"; Description: "Create a Desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[InstallDelete]
Type: filesandordirs; Name: "{app}\*"; Check: ShouldCleanApplicationDirectory
Type: files; Name: "{commonprograms}\{#AppName}.lnk"
Type: files; Name: "{app}\app\*.pdb"
Type: files; Name: "{app}\app\helper\*.pdb"
Type: files; Name: "{app}\app\BeszelAgentManager.Helper.*"

[UninstallRun]
Filename: "{app}\app\helper\BeszelAgentManager.Helper.exe"; Parameters: "{code:GetUninstallHelperParameters}"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveBeszelAgentManagerBackgroundService"
Filename: "{cmd}"; Parameters: "/C taskkill /IM ""BeszelAgentManager.exe"" /T /F"; Flags: runhidden waituntilterminated; RunOnceId: "StopBeszelAgentManager"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
Type: files; Name: "{commonprograms}\{#AppName}.lnk"

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\app\BeszelAgentManager.exe"; WorkingDir: "{app}\app"; Tasks: startmenuicon
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\app\BeszelAgentManager.exe"; WorkingDir: "{app}\app"; Tasks: desktopicon

[Run]
Filename: "{cmd}"; Parameters: "/C if exist ""{autopf}\Beszel-Agent"" icacls ""{autopf}\Beszel-Agent"" /inheritance:e /grant *S-1-5-32-545:(OI)(CI)RX *S-1-5-11:(OI)(CI)RX *S-1-5-32-544:(OI)(CI)F *S-1-5-18:(OI)(CI)F /T /C"; Flags: runhidden waituntilterminated
Filename: "{app}\app\BeszelAgentManager.exe"; Description: "Open BeszelAgentManager"; WorkingDir: "{app}\app"; Flags: nowait postinstall skipifsilent unchecked runasoriginaluser

[Code]
var
  KeepAgentLogs: Boolean;

function InitializeUninstall(): Boolean;
begin
  KeepAgentLogs := True;
  Result := True;
end;

procedure InitializeUninstallProgressForm();
var
  UninstallOptionsPage: TNewNotebookPage;
  KeepAgentLogsCheckBox: TNewCheckBox;
  UninstallButton: TNewButton;
  OriginalPageName: String;
  OriginalPageDescription: String;
  OriginalCancelEnabled: Boolean;
  OriginalCancelModalResult: Integer;
begin
  if UninstallSilent then
    Exit;

  UninstallButton := TNewButton.Create(UninstallProgressForm);
  UninstallButton.Parent := UninstallProgressForm;
  UninstallButton.Left := UninstallProgressForm.CancelButton.Left -
    UninstallProgressForm.CancelButton.Width - ScaleX(10);
  UninstallButton.Top := UninstallProgressForm.CancelButton.Top;
  UninstallButton.Width := UninstallProgressForm.CancelButton.Width;
  UninstallButton.Height := UninstallProgressForm.CancelButton.Height;
  UninstallButton.TabOrder := UninstallProgressForm.CancelButton.TabOrder;
  UninstallButton.Caption := 'Uninstall';
  UninstallButton.ModalResult := mrOK;

  UninstallOptionsPage := TNewNotebookPage.Create(UninstallProgressForm);
  UninstallOptionsPage.Notebook := UninstallProgressForm.InnerNotebook;
  UninstallOptionsPage.Parent := UninstallProgressForm.InnerNotebook;
  UninstallOptionsPage.Align := alClient;
  UninstallProgressForm.InnerNotebook.ActivePage := UninstallOptionsPage;

  KeepAgentLogsCheckBox := TNewCheckBox.Create(UninstallProgressForm);
  KeepAgentLogsCheckBox.Parent := UninstallOptionsPage;
  KeepAgentLogsCheckBox.Left := UninstallProgressForm.StatusLabel.Left;
  KeepAgentLogsCheckBox.Top := UninstallProgressForm.StatusLabel.Top;
  KeepAgentLogsCheckBox.Width := UninstallProgressForm.StatusLabel.Width;
  KeepAgentLogsCheckBox.Height := ScaleY(30);
  KeepAgentLogsCheckBox.Caption := 'Keep historical Beszel Agent logs';
  KeepAgentLogsCheckBox.Checked := True;

  OriginalPageName := UninstallProgressForm.PageNameLabel.Caption;
  OriginalPageDescription := UninstallProgressForm.PageDescriptionLabel.Caption;
  OriginalCancelEnabled := UninstallProgressForm.CancelButton.Enabled;
  OriginalCancelModalResult := UninstallProgressForm.CancelButton.ModalResult;

  UninstallProgressForm.PageNameLabel.Caption := 'Uninstall options';
  UninstallProgressForm.PageDescriptionLabel.Caption :=
    'Choose what should remain after BeszelAgentManager is removed.';
  UninstallProgressForm.CancelButton.Enabled := True;
  UninstallProgressForm.CancelButton.ModalResult := mrCancel;
  UninstallProgressForm.CancelButton.TabOrder := UninstallButton.TabOrder + 1;

  if UninstallProgressForm.ShowModal = mrCancel then
    Abort;

  KeepAgentLogs := KeepAgentLogsCheckBox.Checked;
  UninstallButton.Visible := False;
  UninstallProgressForm.PageNameLabel.Caption := OriginalPageName;
  UninstallProgressForm.PageDescriptionLabel.Caption := OriginalPageDescription;
  UninstallProgressForm.CancelButton.Enabled := OriginalCancelEnabled;
  UninstallProgressForm.CancelButton.ModalResult := OriginalCancelModalResult;
  UninstallProgressForm.InnerNotebook.ActivePage :=
    UninstallProgressForm.InstallingPage;
end;

function GetUninstallHelperParameters(Param: String): String;
begin
  Result := '--remove-background-service';
  if not KeepAgentLogs then
    Result := Result + ' --remove-agent-logs';
end;

function ShouldCleanApplicationDirectory(): Boolean;
var
  InstalledVersion: Int64;
  TargetVersion: Int64;
  ManagerExecutable: String;
  IsLegacyLayout: Boolean;
  IsRollback: Boolean;
begin
  ManagerExecutable := ExpandConstant('{app}\app\BeszelAgentManager.exe');
  IsLegacyLayout :=
    FileExists(ExpandConstant('{app}\BeszelAgentManager.exe')) or
    (DirExists(ExpandConstant('{app}\app')) and
      not FileExists(ExpandConstant('{app}\app\BeszelAgentManager.Core.dll')));

  IsRollback :=
    GetPackedVersion(ManagerExecutable, InstalledVersion) and
    StrToVersion('{#AppVersion}', TargetVersion) and
    (ComparePackedVersion(InstalledVersion, TargetVersion) > 0);

  Result := IsLegacyLayout or IsRollback;
  if Result then
    Log('A legacy, incomplete, or rollback installation requires a full application-directory refresh.')
  else
    Log('Using version-aware incremental application-file replacement.');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    if not Exec(
      ExpandConstant('{app}\app\helper\BeszelAgentManager.Helper.exe'),
      '--install-background-service',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) then
      RaiseException('Could not start the BeszelAgentManager background-service installer.');

    if ResultCode <> 0 then
      RaiseException(Format('The BeszelAgentManager background service could not be installed (exit code %d).', [ResultCode]));
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    RegDeleteValue(
      HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Run',
      '{#AppName}');
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  Exec(
    ExpandConstant('{sys}\net.exe'),
    'stop "BeszelAgentManager Background" /y',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode
  );
  Exec(
    ExpandConstant('{cmd}'),
    '/C taskkill /IM "BeszelAgentManager.exe" /T /F',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode
  );
end;
