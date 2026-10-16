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
DisableDirPage=yes
UsePreviousAppDir=no
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

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimp"; MessagesFile: "Languages\ChineseSimplified.isl"

[CustomMessages]
english.TaskStartMenu=Create a Start Menu shortcut
english.TaskDesktop=Create a Desktop shortcut
english.TaskShortcuts=Shortcuts:
english.OpenManager=Open BeszelAgentManager
english.RedirectedDirectory=Installation cannot use a redirected directory: %1
english.RuntimeRequired=BeszelAgentManager Lite requires Microsoft .NET 10 Desktop Runtime x64.
english.RuntimeInstall=Install it from Microsoft, then run this installer again.
english.RuntimeDownload=Open the Microsoft download page now?
english.UninstallAction=Uninstall
english.KeepAgentLogs=Keep historical Beszel Agent logs
english.UninstallOptions=Uninstall options
english.UninstallDescription=Choose what should remain after BeszelAgentManager is removed.
english.ObsoleteFile=Could not remove obsolete application file: %1
english.ManifestInvalid=The installer application manifest contains an invalid entry.
english.ManifestUnsafe=The installer application manifest contains an unsafe path.
english.FileMissing=An expected application file is missing: %1
english.FileVerification=Application file verification failed: %1
english.VersionMetadata=Installed VERSION metadata does not match %1.
english.VersionManager=Installed manager executable does not match %1.
english.VersionHelper=Installed background helper does not match %1.
english.RuntimeVariant=Could not save the manager runtime variant.
english.BackgroundInstaller=Could not start the BeszelAgentManager background-service installer.
english.BackgroundSecurity=Background-service security setup failed. Open the manager Extra page to repair it.
english.BackgroundBlocked=BeszelAgentManager was installed, but Windows blocked the background-service security setup.
english.BackgroundRepair=Open BeszelAgentManager, go to Extra, then use Background service > Install to repair it.
english.BackgroundRemain=The manager can remain installed; no manual command is required.
english.BackgroundInstallFailed=The BeszelAgentManager background service could not be installed (exit code %1).
english.ProtectedDirectory=BeszelAgentManager must be installed in its protected Program Files directory.
english.FoldersSecurity=The installation folders could not be secured. Redirected paths, hard links or untrusted ownership need administrator review before continuing.
english.SecurityPreflight=Installation security preflight failed: %1
english.ManifestLoad=The installer application manifest could not be loaded.
english.ManifestPrepare=The installer application manifest could not be prepared: %1
chinesesimp.TaskStartMenu=创建开始菜单快捷方式
chinesesimp.TaskDesktop=创建桌面快捷方式
chinesesimp.TaskShortcuts=快捷方式：
chinesesimp.OpenManager=打开 BeszelAgentManager
chinesesimp.RedirectedDirectory=安装不能使用重定向目录：%1
chinesesimp.RuntimeRequired=BeszelAgentManager Lite 需要 Microsoft .NET 10 桌面运行时 x64。
chinesesimp.RuntimeInstall=请从 Microsoft 安装运行时，然后重新运行此安装程序。
chinesesimp.RuntimeDownload=现在打开 Microsoft 下载页面吗？
chinesesimp.UninstallAction=卸载
chinesesimp.KeepAgentLogs=保留历史 Beszel 代理日志
chinesesimp.UninstallOptions=卸载选项
chinesesimp.UninstallDescription=选择卸载 BeszelAgentManager 后需要保留的内容。
chinesesimp.ObsoleteFile=无法移除过时的应用文件：%1
chinesesimp.ManifestInvalid=安装程序的应用文件清单包含无效条目。
chinesesimp.ManifestUnsafe=安装程序的应用文件清单包含不安全的路径。
chinesesimp.FileMissing=缺少预期的应用文件：%1
chinesesimp.FileVerification=应用文件验证失败：%1
chinesesimp.VersionMetadata=已安装的 VERSION 元数据与 %1 不匹配。
chinesesimp.VersionManager=已安装的管理器可执行文件与 %1 不匹配。
chinesesimp.VersionHelper=已安装的后台辅助程序与 %1 不匹配。
chinesesimp.RuntimeVariant=无法保存管理器运行时版本标记。
chinesesimp.BackgroundInstaller=无法启动 BeszelAgentManager 后台服务安装程序。
chinesesimp.BackgroundSecurity=后台服务安全设置失败。请打开管理器“更多”页面进行修复。
chinesesimp.BackgroundBlocked=BeszelAgentManager 已安装，但 Windows 阻止了后台服务安全设置。
chinesesimp.BackgroundRepair=请打开 BeszelAgentManager，进入“更多”页面，使用“后台服务 > 安装”修复。
chinesesimp.BackgroundRemain=管理器可以保留安装，无需手动运行命令。
chinesesimp.BackgroundInstallFailed=无法安装 BeszelAgentManager 后台服务（退出代码：%1）。
chinesesimp.ProtectedDirectory=BeszelAgentManager 必须安装在受保护的 Program Files 目录中。
chinesesimp.FoldersSecurity=无法保护安装目录。继续之前，需要管理员检查重定向路径、硬链接或不受信任的所有者。
chinesesimp.SecurityPreflight=安装安全预检失败：%1
chinesesimp.ManifestLoad=无法加载安装程序的应用文件清单。
chinesesimp.ManifestPrepare=无法准备安装程序的应用文件清单：%1

[Files]
Source: "{#DistDir}\INSTALL-MANIFEST.sha256"; Flags: dontcopy
Source: "{#DistDir}\helper\*"; DestDir: "{tmp}\bam-security"; Flags: dontcopy recursesubdirs createallsubdirs
Source: "{#DistDir}\*"; DestDir: "{app}\app"; Excludes: "nssm.exe,*.pdb,INSTALL-MANIFEST.sha256"; Flags: ignoreversion recursesubdirs createallsubdirs; Check: ShouldInstallApplicationFile
Source: "{#DistDir}\INSTALL-MANIFEST.sha256"; DestDir: "{app}\app"; Flags: ignoreversion
Source: "{#DistDir}\nssm.exe"; DestDir: "{autopf}\Beszel-Agent"; Flags: ignoreversion onlyifdoesntexist

[Dirs]
Name: "{commonappdata}\{#AppName}"

[Tasks]
Name: "startmenuicon"; Description: "{cm:TaskStartMenu}"; GroupDescription: "{cm:TaskShortcuts}"; Flags: checkedonce
Name: "desktopicon"; Description: "{cm:TaskDesktop}"; GroupDescription: "{cm:TaskShortcuts}"; Flags: unchecked

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
Filename: "{app}\app\BeszelAgentManager.exe"; Description: "{cm:OpenManager}"; WorkingDir: "{app}\app"; Flags: nowait postinstall skipifsilent unchecked runasoriginaluser

[Code]
var
  KeepAgentLogs: Boolean;
  FullApplicationRefresh: Boolean;
  InstallManifestLoaded: Boolean;
  InstallManifest: TArrayOfString;
  PreviousInstallManifest: TArrayOfString;

function GetFileAttributesW(FileName: String): Cardinal;
  external 'GetFileAttributesW@kernel32.dll stdcall';

procedure RequireLocalNonReparsePath(Path: String);
var
  Current: String;
  Attributes: Cardinal;
begin
  Current := RemoveBackslashUnlessRoot(Path);
  while Length(Current) > 3 do
  begin
    Attributes := GetFileAttributesW(Current);
    if (Attributes <> $FFFFFFFF) and ((Attributes and $400) <> 0) then
      RaiseException(FmtMessage(CustomMessage('RedirectedDirectory'), [Current]));
    Current := ExtractFileDir(Current);
  end;
end;

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
        CustomMessage('RuntimeRequired') + #13#10 + #13#10 +
        CustomMessage('RuntimeInstall') + #13#10 + #13#10 +
        CustomMessage('RuntimeDownload'),
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
  UninstallButton.Caption := CustomMessage('UninstallAction');
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
  KeepAgentLogsCheckBox.Caption := CustomMessage('KeepAgentLogs');
  KeepAgentLogsCheckBox.Checked := True;

  OriginalPageName := UninstallProgressForm.PageNameLabel.Caption;
  OriginalPageDescription := UninstallProgressForm.PageDescriptionLabel.Caption;
  OriginalCancelEnabled := UninstallProgressForm.CancelButton.Enabled;
  OriginalCancelModalResult := UninstallProgressForm.CancelButton.ModalResult;

  UninstallProgressForm.PageNameLabel.Caption := CustomMessage('UninstallOptions');
  UninstallProgressForm.PageDescriptionLabel.Caption :=
    CustomMessage('UninstallDescription');
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
            RaiseException(FmtMessage(CustomMessage('ObsoleteFile'), [RelativePath]));
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
      RaiseException(CustomMessage('ManifestInvalid'));

    ExpectedHash := LowerCase(Copy(InstallManifest[Index], 1, SeparatorIndex - 1));
    RelativePath := NormalizeManifestPath(Copy(
      InstallManifest[Index],
      SeparatorIndex + 1,
      Length(InstallManifest[Index]) - SeparatorIndex));
    if not IsSafeManifestPath(RelativePath) then
      RaiseException(CustomMessage('ManifestUnsafe'));

    InstalledPath := ExpandConstant('{app}\app\') + RelativePath;
    StringChangeEx(InstalledPath, '/', '\', True);
    if not FileExists(InstalledPath) then
      RaiseException(FmtMessage(CustomMessage('FileMissing'), [RelativePath]));

    InstalledHash := LowerCase(GetSHA256OfFile(InstalledPath));
    if InstalledHash <> ExpectedHash then
      RaiseException(FmtMessage(CustomMessage('FileVerification'), [RelativePath]));
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
    RaiseException(FmtMessage(CustomMessage('VersionMetadata'), ['{#AppVersion}']));

  if not HasExpectedVersion(ExpandConstant('{app}\app\BeszelAgentManager.exe')) then
    RaiseException(FmtMessage(CustomMessage('VersionManager'), ['{#AppVersion}']));

  if not HasExpectedVersion(ExpandConstant('{app}\app\helper\BeszelAgentManager.Helper.exe')) then
    RaiseException(FmtMessage(CustomMessage('VersionHelper'), ['{#AppVersion}']));
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
      ExpandConstant('{app}\app\RUNTIME_VARIANT'),
      '{#RuntimeVariantId}' + #13#10,
      False) then
      RaiseException(CustomMessage('RuntimeVariant'));

    if not Exec(
      ExpandConstant('{app}\app\helper\BeszelAgentManager.Helper.exe'),
      '--install-background-service',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) then
      RaiseException(CustomMessage('BackgroundInstaller'));

    if ResultCode = 5 then
    begin
      if WizardSilent then
        RaiseException(CustomMessage('BackgroundSecurity'));
      Log('Background-service setup returned recoverable exit code 5. The manager will remain installed for repair from the Extra page.');
      if not WizardSilent then
        MsgBox(
          CustomMessage('BackgroundBlocked') + #13#10 + #13#10 +
          CustomMessage('BackgroundRepair') + #13#10 + #13#10 +
          CustomMessage('BackgroundRemain'),
          mbInformation,
          MB_OK);
    end
    else if ResultCode <> 0 then
      RaiseException(FmtMessage(CustomMessage('BackgroundInstallFailed'), [IntToStr(ResultCode)]));
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
  if CompareText(RemoveBackslashUnlessRoot(ExpandConstant('{app}')),
    RemoveBackslashUnlessRoot(ExpandConstant('{autopf}\{#AppName}'))) <> 0 then
  begin
    Result := CustomMessage('ProtectedDirectory');
    Exit;
  end;
  try
    RequireLocalNonReparsePath(ExpandConstant('{app}'));
    RequireLocalNonReparsePath(ExpandConstant('{app}\app\helper'));
    RequireLocalNonReparsePath(ExpandConstant('{autopf}\Beszel-Agent'));
    RequireLocalNonReparsePath(ExpandConstant('{commonappdata}\{#AppName}.ServiceData'));
  except
    Result := GetExceptionMessage();
    Exit;
  end;
  try
    ExtractTemporaryFiles('{tmp}\bam-security\*');
    if not Exec(ExpandConstant('{tmp}\bam-security\BeszelAgentManager.Helper.exe'),
      '--secure-installation-root', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
    begin
      Result := CustomMessage('FoldersSecurity');
      Exit;
    end;
  except
    Result := FmtMessage(CustomMessage('SecurityPreflight'), [GetExceptionMessage()]);
    Exit;
  end;
  try
    ExtractTemporaryFile('INSTALL-MANIFEST.sha256');
    ManifestPath := ExpandConstant('{tmp}\INSTALL-MANIFEST.sha256');
    InstallManifestLoaded := LoadStringsFromFile(ManifestPath, InstallManifest);
    if not InstallManifestLoaded or (GetArrayLength(InstallManifest) = 0) then
    begin
      Result := CustomMessage('ManifestLoad');
      Exit;
    end;

    LoadStringsFromFile(
      ExpandConstant('{app}\app\INSTALL-MANIFEST.sha256'),
      PreviousInstallManifest);
  except
    Result := FmtMessage(CustomMessage('ManifestPrepare'), [GetExceptionMessage()]);
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
    '/C taskkill /IM "BeszelAgentManager.Helper.exe" /F',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode
  );
end;
