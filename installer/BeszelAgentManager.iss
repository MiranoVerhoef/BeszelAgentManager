#define AppName "BeszelAgentManager"
#ifndef AppVersion
#define AppVersion "0.0.0"
#endif
#ifndef DistDir
#define DistDir "..\build\main.dist"
#endif
#ifndef InstallerOutputDir
#define InstallerOutputDir "..\installer-dist"
#endif
#ifdef LiteInstaller
#define RuntimeVariantId "lite"
#define RuntimeVariantSuffix " Lite"
#define OutputSuffix "-Lite"
#else
#define RuntimeVariantId "bundled"
#define RuntimeVariantSuffix ""
#define OutputSuffix ""
#endif

[Setup]
AppId={{8E3ED77F-F8A2-4D8C-8D5F-0F5295E1B10D}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}{#RuntimeVariantSuffix}
AppPublisher=Verhoef
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir={#InstallerOutputDir}
OutputBaseFilename={#AppName}Setup{#OutputSuffix}
SetupIconFile=..\BeszelAgentManager_icon.ico
WizardSmallImageFile=WizardSmallImage.bmp
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0.17763
CloseApplications=yes
CloseApplicationsFilter=BeszelAgentManager.exe
RestartApplications=no
UninstallDisplayIcon={app}\app\BeszelAgentManager.exe
VersionInfoCompany=Verhoef
VersionInfoDescription={#AppName}{#RuntimeVariantSuffix} Installer
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
VersionInfoVersion={#AppVersion}.0

[Files]
Source: "{#DistDir}\INSTALL-MANIFEST.sha256"; Flags: dontcopy
Source: "{#DistDir}\*"; DestDir: "{app}\app"; Excludes: "nssm.exe,*.pdb,INSTALL-MANIFEST.sha256"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: ShouldInstallApplicationFile
Source: "{#DistDir}\INSTALL-MANIFEST.sha256"; DestDir: "{app}\app"; Flags: ignoreversion
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
  FullApplicationRefresh: Boolean;
  InstallManifestLoaded: Boolean;
  InstallManifest: TArrayOfString;
  PreviousInstallManifest: TArrayOfString;

function NormalizeManifestPath(Value: String): String;
begin
  StringChangeEx(Value, '\', '/', True);
  Result := Value;
end;

function IsSafeManifestPath(const RelativePath: String): Boolean;
begin
  if RelativePath = '' then
  begin
    Result := False;
    Exit;
  end;

  Result :=
    (RelativePath[1] <> '/') and
    (Pos(':', RelativePath) = 0) and
    (Pos('../', RelativePath) = 0) and
    (Pos('/..', RelativePath) = 0) and
    (RelativePath <> '..');
end;

function IsValidSha256(const Value: String): Boolean;
var
  Index: Integer;
  Character: String;
begin
  Result := Length(Value) = 64;
  if not Result then
    Exit;

  for Index := 1 to Length(Value) do
  begin
    Character := LowerCase(Copy(Value, Index, 1));
    if Pos(Character, '0123456789abcdef') = 0 then
    begin
      Result := False;
      Exit;
    end;
  end;
end;

function TryGetManifestHash(
  const Manifest: TArrayOfString;
  const RelativePath: String;
  var ExpectedHash: String): Boolean;
var
  Index: Integer;
  SeparatorIndex: Integer;
  EntryPath: String;
begin
  Result := False;
  ExpectedHash := '';
  for Index := 0 to GetArrayLength(Manifest) - 1 do
  begin
    SeparatorIndex := Pos('|', Manifest[Index]);
    if SeparatorIndex > 1 then
    begin
      EntryPath := NormalizeManifestPath(Copy(
        Manifest[Index],
        SeparatorIndex + 1,
        Length(Manifest[Index]) - SeparatorIndex));
      if LowerCase(EntryPath) = LowerCase(RelativePath) then
      begin
        ExpectedHash := LowerCase(Copy(Manifest[Index], 1, SeparatorIndex - 1));
        Result := IsValidSha256(ExpectedHash);
        Exit;
      end;
    end;
  end;
end;

function GetCurrentApplicationRelativePath(): String;
var
  DestinationPath: String;
  ApplicationRoot: String;
begin
  DestinationPath := NormalizeManifestPath(ExpandConstant(CurrentFileName()));
  ApplicationRoot := NormalizeManifestPath(ExpandConstant('{app}\app\'));
  if Pos(LowerCase(ApplicationRoot), LowerCase(DestinationPath)) = 1 then
    Result := Copy(DestinationPath, Length(ApplicationRoot) + 1, Length(DestinationPath))
  else
    Result := '';
end;

function ShouldInstallApplicationFile(): Boolean;
var
  RelativePath: String;
  DestinationPath: String;
  ExpectedHash: String;
  InstalledHash: String;
begin
  if FullApplicationRefresh then
  begin
    Result := True;
    Exit;
  end;

  RelativePath := GetCurrentApplicationRelativePath();
  DestinationPath := ExpandConstant(CurrentFileName());
  if not InstallManifestLoaded or
    not IsSafeManifestPath(RelativePath) or
    not TryGetManifestHash(InstallManifest, RelativePath, ExpectedHash) or
    not FileExists(DestinationPath) then
  begin
    Result := True;
    Exit;
  end;

  try
    InstalledHash := LowerCase(GetSHA256OfFile(DestinationPath));
    Result := InstalledHash <> ExpectedHash;
    if Result then
      Log('Updating changed application file: ' + RelativePath)
    else
      Log('Keeping unchanged application file: ' + RelativePath);
  except
    Log('Could not hash installed application file; replacing it: ' + RelativePath);
    Result := True;
  end;
end;

function HasDotNet10DesktopRuntime(): Boolean;
var
  DotNetPath: String;
  Parameters: String;
  ResultCode: Integer;
begin
  DotNetPath := ExpandConstant('{pf64}\dotnet\dotnet.exe');
  if not FileExists(DotNetPath) then
  begin
    Result := False;
    Exit;
  end;

  Parameters :=
    '-NoProfile -NonInteractive -WindowStyle Hidden -Command ' +
    '"$runtime = & ''' + DotNetPath + ''' --list-runtimes | ' +
    'Where-Object { $_ -match ''^Microsoft\.WindowsDesktop\.App 10\.'' }; ' +
    'if ($runtime) { exit 0 }; exit 1"';
  Result := Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    Parameters,
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) and (ResultCode = 0);
end;

function InitializeSetup(): Boolean;
#ifdef LiteInstaller
var
  OpenDownload: Integer;
  ResultCode: Integer;
#endif
begin
  Result := True;
#ifdef LiteInstaller
  if not HasDotNet10DesktopRuntime() then
  begin
    Log('BeszelAgentManager Lite requires Microsoft .NET 10 Desktop Runtime x64.');
    if not WizardSilent then
    begin
      OpenDownload := MsgBox(
        'BeszelAgentManager Lite requires Microsoft .NET 10 Desktop Runtime x64.' + #13#10 + #13#10 +
        'Install it from Microsoft, then run this installer again.' + #13#10 + #13#10 +
        'Open the Microsoft download page now?',
        mbConfirmation,
        MB_YESNO);
      if OpenDownload = IDYES then
        ShellExec(
          'open',
          'https://dotnet.microsoft.com/download/dotnet/10.0/runtime',
          '',
          '',
          SW_SHOWNORMAL,
          ewNoWait,
          ResultCode);
    end;
    Result := False;
  end;
#endif
end;

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
  FullApplicationRefresh := Result;
  if Result then
    Log('A legacy, incomplete, or rollback installation requires a full application-directory refresh.')
  else
    Log('Using SHA-256-aware incremental application-file replacement.');
end;

procedure RemoveStaleApplicationFiles();
var
  Index: Integer;
  SeparatorIndex: Integer;
  RelativePath: String;
  IgnoredHash: String;
  StalePath: String;
begin
  if FullApplicationRefresh then
    Exit;

  for Index := 0 to GetArrayLength(PreviousInstallManifest) - 1 do
  begin
    SeparatorIndex := Pos('|', PreviousInstallManifest[Index]);
    if SeparatorIndex > 1 then
    begin
      RelativePath := NormalizeManifestPath(Copy(
        PreviousInstallManifest[Index],
        SeparatorIndex + 1,
        Length(PreviousInstallManifest[Index]) - SeparatorIndex));
      if IsSafeManifestPath(RelativePath) and
        not TryGetManifestHash(InstallManifest, RelativePath, IgnoredHash) then
      begin
        StalePath := ExpandConstant('{app}\app\') + RelativePath;
        StringChangeEx(StalePath, '/', '\', True);
        if FileExists(StalePath) then
        begin
          Log('Removing obsolete application file: ' + RelativePath);
          if not DeleteFile(StalePath) then
            RaiseException('Could not remove obsolete application file: ' + RelativePath);
        end;
      end;
    end;
  end;
end;

procedure VerifyInstalledManifest();
var
  Index: Integer;
  SeparatorIndex: Integer;
  RelativePath: String;
  ExpectedHash: String;
  InstalledHash: String;
  InstalledPath: String;
begin
  for Index := 0 to GetArrayLength(InstallManifest) - 1 do
  begin
    SeparatorIndex := Pos('|', InstallManifest[Index]);
    if SeparatorIndex <= 1 then
      RaiseException('The installer application manifest contains an invalid entry.');

    ExpectedHash := LowerCase(Copy(InstallManifest[Index], 1, SeparatorIndex - 1));
    RelativePath := NormalizeManifestPath(Copy(
      InstallManifest[Index],
      SeparatorIndex + 1,
      Length(InstallManifest[Index]) - SeparatorIndex));
    if not IsSafeManifestPath(RelativePath) then
      RaiseException('The installer application manifest contains an unsafe path.');

    InstalledPath := ExpandConstant('{app}\app\') + RelativePath;
    StringChangeEx(InstalledPath, '/', '\', True);
    if not FileExists(InstalledPath) then
      RaiseException('An expected application file is missing: ' + RelativePath);

    InstalledHash := LowerCase(GetSHA256OfFile(InstalledPath));
    if InstalledHash <> ExpectedHash then
      RaiseException('Application file verification failed: ' + RelativePath);
  end;
end;

function HasExpectedVersion(FileName: String): Boolean;
var
  InstalledVersion: Int64;
  TargetVersion: Int64;
begin
  Result :=
    GetPackedVersion(FileName, InstalledVersion) and
    StrToVersion('{#AppVersion}', TargetVersion) and
    (ComparePackedVersion(InstalledVersion, TargetVersion) = 0);
end;

procedure VerifyInstalledApplication();
var
  VersionText: AnsiString;
begin
  if not LoadStringFromFile(ExpandConstant('{app}\app\VERSION'), VersionText) or
    (Trim(VersionText) <> '{#AppVersion}') then
    RaiseException('Installed VERSION metadata does not match {#AppVersion}.');

  if not HasExpectedVersion(ExpandConstant('{app}\app\BeszelAgentManager.exe')) then
    RaiseException('Installed manager executable does not match {#AppVersion}.');

  if not HasExpectedVersion(ExpandConstant('{app}\app\helper\BeszelAgentManager.Helper.exe')) then
    RaiseException('Installed background helper does not match {#AppVersion}.');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssPostInstall then
  begin
    RemoveStaleApplicationFiles();
    VerifyInstalledManifest();
    VerifyInstalledApplication();
    if not SaveStringToFile(
      ExpandConstant('{commonappdata}\{#AppName}\manager-runtime-variant.txt'),
      '{#RuntimeVariantId}' + #13#10,
      False) then
      RaiseException('Could not save the manager runtime variant.');

    if not Exec(
      ExpandConstant('{app}\app\helper\BeszelAgentManager.Helper.exe'),
      '--install-background-service',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) then
      RaiseException('Could not start the BeszelAgentManager background-service installer.');

    if ResultCode = 5 then
    begin
      Log('Background-service setup returned recoverable exit code 5. The manager will remain installed for repair from the Extra page.');
      if not WizardSilent then
        MsgBox(
          'BeszelAgentManager was installed, but Windows blocked the background-service security setup.' + #13#10 + #13#10 +
          'Open BeszelAgentManager, go to Extra, then use Background service > Install to repair it.' + #13#10 + #13#10 +
          'The manager can remain installed; no manual command is required.',
          mbInformation,
          MB_OK);
    end
    else if ResultCode <> 0 then
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
  ManifestPath: String;
begin
  Result := '';
  try
    ExtractTemporaryFile('INSTALL-MANIFEST.sha256');
    ManifestPath := ExpandConstant('{tmp}\INSTALL-MANIFEST.sha256');
    InstallManifestLoaded := LoadStringsFromFile(ManifestPath, InstallManifest);
    if not InstallManifestLoaded or (GetArrayLength(InstallManifest) = 0) then
    begin
      Result := 'The installer application manifest could not be loaded.';
      Exit;
    end;

    LoadStringsFromFile(
      ExpandConstant('{app}\app\INSTALL-MANIFEST.sha256'),
      PreviousInstallManifest);
  except
    Result := 'The installer application manifest could not be prepared: ' + GetExceptionMessage();
    Exit;
  end;

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
  Exec(
    ExpandConstant('{cmd}'),
    '/C taskkill /IM "BeszelAgentManager.Helper.exe" /T /F',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode
  );
end;
