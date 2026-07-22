#define MyAppName "CS2 Focus Guard"
#define MyAppVersion "1.0.2"
#define MyAppPublisher "CS2 Focus Guard"
#define MyAppExeName "CS2FocusGuard.exe"

[Setup]
AppId={{17CF2B5C-C1FE-42CA-B993-FB28F187DB77}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
MinVersion=10.0.19045
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts
OutputBaseFilename=CS2FocusGuard-Setup-{#MyAppVersion}-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=..\src\CS2FocusGuard.App\Assets\AppIcon.ico
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesetraditional"; MessagesFile: "ChineseTraditional.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "startup"; Description: "{cm:StartWithWindowsTask}"; GroupDescription: "{cm:StartupOptions}"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\THIRD-PARTY-NOTICES.txt"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "CS2FocusGuard"; \
    ValueData: """{app}\{#MyAppExeName}"" --background"; \
    Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; \
    Flags: nowait postinstall skipifsilent
Filename: "{app}\{#MyAppExeName}"; Flags: nowait skipifdoesntexist; \
    Check: ShouldRestartApplication

[UninstallRun]
Filename: "{app}\{#MyAppExeName}"; Parameters: "--exit"; \
    Flags: runhidden waituntilterminated skipifdoesntexist; RunOnceId: "CloseApp"

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\CS2FocusGuard"; Check: ShouldRemoveUserData

[CustomMessages]
english.StartWithWindowsTask=Start automatically when Windows starts
english.StartupOptions=Startup options:
english.RemoveSettingsPrompt=Also remove your settings and saved recovery state?
english.AppExitFailed=CS2 Focus Guard could not be asked to exit.
english.AppExitTimeout=CS2 Focus Guard did not exit within 10 seconds.
chinesetraditional.StartWithWindowsTask=隨 Windows 啟動
chinesetraditional.StartupOptions=啟動選項：
chinesetraditional.RemoveSettingsPrompt=是否一併移除設定與已儲存的還原狀態？
chinesetraditional.AppExitFailed=無法要求 CS2 Focus Guard 結束。
chinesetraditional.AppExitTimeout=CS2 Focus Guard 未在 10 秒內結束。

[Code]
var
  RemoveUserData: Boolean;

function InitializeUninstall(): Boolean;
begin
  if UninstallSilent then
    RemoveUserData := False
  else
    RemoveUserData :=
      MsgBox(ExpandConstant('{cm:RemoveSettingsPrompt}'),
        mbConfirmation, MB_YESNO) = IDYES;
  Result := True;
end;

function ShouldRemoveUserData(): Boolean;
begin
  Result := RemoveUserData;
end;

function ShouldRestartApplication(): Boolean;
var
  ParameterIndex: Integer;
begin
  Result := False;
  for ParameterIndex := 1 to ParamCount do
  begin
    if CompareText(ParamStr(ParameterIndex), '/RESTARTAPP') = 0 then
    begin
      Result := True;
      exit;
    end;
  end;
end;

function TryGetApplicationRunning(var IsRunning: Boolean): Boolean;
var
  OutputFile: String;
  ResultCode: Integer;
  OutputLines: TArrayOfString;
  LineIndex: Integer;
begin
  OutputFile := ExpandConstant('{tmp}\CS2FocusGuard-processes.txt');
  DeleteFile(OutputFile);
  Result := Exec(
    ExpandConstant('{sys}\cmd.exe'),
    '/C tasklist /FI "IMAGENAME eq CS2FocusGuard.exe" /NH /FO CSV > "' +
      OutputFile + '"',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) and
    (ResultCode = 0) and
    LoadStringsFromFile(OutputFile, OutputLines);
  DeleteFile(OutputFile);

  if not Result then
    exit;

  IsRunning := False;
  for LineIndex := 0 to GetArrayLength(OutputLines) - 1 do
  begin
    if Pos('"CS2FocusGuard.exe"', OutputLines[LineIndex]) > 0 then
    begin
      IsRunning := True;
      exit;
    end;
  end;
end;

function WaitForApplicationExit(): Boolean;
var
  Attempt: Integer;
  IsRunning: Boolean;
begin
  for Attempt := 1 to 50 do
  begin
    if not TryGetApplicationRunning(IsRunning) then
    begin
      Result := False;
      exit;
    end;

    if not IsRunning then
    begin
      Result := True;
      exit;
    end;

    Sleep(200);
  end;

  Result := False;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ExitRequested: Boolean;
  ResultCode: Integer;
begin
  Result := '';
  if FileExists(ExpandConstant('{app}\{#MyAppExeName}')) then
  begin
    ExitRequested := Exec(
      ExpandConstant('{app}\{#MyAppExeName}'),
      '--exit',
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode);
    if ExitRequested then
      ExitRequested := ResultCode = 0;

    if not WaitForApplicationExit() then
    begin
      if ExitRequested then
        Result := ExpandConstant('{cm:AppExitTimeout}')
      else
        Result := ExpandConstant('{cm:AppExitFailed}');
    end;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssInstall) and
     (not WizardIsTaskSelected('startup')) then
  begin
    RegDeleteValue(HKCU,
      'Software\Microsoft\Windows\CurrentVersion\Run',
      'CS2FocusGuard');
  end;
end;
