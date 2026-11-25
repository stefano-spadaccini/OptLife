; OptLife installer (Inno Setup 6). Built by build-installer.ps1, which passes the version and the folders.
; Program in Program Files; history and settings in %ProgramData%\OptLife, never touched by an update;
; the uninstaller asks whether to keep them. Italian and English.

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\app"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif
#ifndef LicenseFile
  #define LicenseFile "..\LICENSE"
#endif
#ifndef AppUrl
  #define AppUrl ""
#endif

[Setup]
AppId={{8F3C2B1E-5D4A-4C6B-9E2F-1A7D3C5B9E40}
AppName=OptLife
AppVersion={#AppVersion}
AppVerName=OptLife {#AppVersion}
AppPublisher=OptLife contributors
#if AppUrl != ""
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
#endif
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\OptLife
DefaultGroupName=OptLife
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19045
OutputDir={#OutputDir}
OutputBaseFilename=OptLife-Setup-{#AppVersion}
SetupIconFile=..\src\OptLife.ico
UninstallDisplayIcon={app}\optlife-panel.exe
UninstallDisplayName=OptLife
LicenseFile={#LicenseFile}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ShowLanguageDialog=auto
; The agent and the panel are stopped by the code below, in the right order.
CloseApplications=no
RestartApplications=no

[Languages]
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
it.OpenPanel=Apri OptLife
en.OpenPanel=Open OptLife
it.RegisterAgent=Avvio automatico di OptLife...
en.RegisterAgent=Setting OptLife to start with Windows...
it.KeepData=Vuoi tenere lo storico e le impostazioni di OptLife?%n%nSì: restano in %1 e una nuova installazione li ritrova.%nNo: vengono cancellati.
en.KeepData=Do you want to keep OptLife's history and settings?%n%nYes: they stay in %1 and a new installation finds them again.%nNo: they are deleted.
it.PanelOpen=Il pannello di OptLife è aperto: verrà chiuso per installare.
en.PanelOpen=The OptLife panel is open: it will be closed to install.

[Dirs]
; Changed only by the system and administrators (the settings decide what the agent runs as administrator); users may read it.
; The rules are set in [Run] with icacls: those inherited from ProgramData would let users create files in it.
Name: "{commonappdata}\OptLife"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "agent-task.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\OptLife"; Filename: "{app}\optlife-panel.exe"
Name: "{group}\{cm:UninstallProgram,OptLife}"; Filename: "{uninstallexe}"

[Run]
Filename: "{sys}\icacls.exe"; Parameters: """{commonappdata}\OptLife"" /inheritance:r /grant:r *S-1-5-18:(OI)(CI)F *S-1-5-32-544:(OI)(CI)F *S-1-5-32-545:(OI)(CI)RX /C /Q"; Flags: runhidden waituntilterminated
Filename: "{sys}\icacls.exe"; Parameters: """{commonappdata}\OptLife"" /setowner *S-1-5-32-544 /T /C /Q"; Flags: runhidden waituntilterminated
Filename: "{sys}\icacls.exe"; Parameters: """{commonappdata}\OptLife\*"" /reset /T /C /Q"; Flags: runhidden waituntilterminated
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\agent-task.ps1"" -Target ""{app}"""; \
  StatusMsg: "{cm:RegisterAgent}"; Flags: runhidden waituntilterminated
Filename: "{app}\optlife-panel.exe"; Description: "{cm:OpenPanel}"; Flags: postinstall nowait skipifsilent runasoriginaluser
; An update by OptLife itself (silent): the panel it had to close opens again.
Filename: "{app}\optlife-panel.exe"; Flags: nowait runasoriginaluser; Check: OpenPanelAfter

[UninstallRun]
Filename: "{app}\optlife.exe"; Parameters: "stop"; Flags: runhidden waituntilterminated; RunOnceId: "StopAgent"
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\agent-task.ps1"" -Target ""{app}"" -Remove"; \
  Flags: runhidden waituntilterminated; RunOnceId: "RemoveTask"

[UninstallDelete]
Type: files; Name: "{app}\installing.lock"

[Code]
function OpenPanelAfter: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:OPENPANEL|0}') = '1');
end;

function Running(const Name: String): Boolean;
var
  Code: Integer;
begin
  { tasklist prints the name only when such a process exists }
  Result := Exec(ExpandConstant('{cmd}'), '/C tasklist /FI "IMAGENAME eq ' + Name + '" /NH | find /I "' + Name + '" >NUL', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0);
end;

procedure WaitGone(const Name: String; Seconds: Integer);
var
  I: Integer;
begin
  for I := 1 to Seconds * 4 do
  begin
    if not Running(Name) then Exit;
    Sleep(250);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
  Lock: String;
begin
  Result := '';
  { While the files are copied the agent must not come back by itself: its task tries every 5 minutes. }
  ForceDirectories(ExpandConstant('{app}'));
  Lock := ExpandConstant('{app}\installing.lock');
  SaveStringToFile(Lock, GetDateTimeString('yyyy-mm-dd hh:nn:ss', '-', ':'), False);
  if Running('optlife-panel.exe') then
  begin
    if not WizardSilent then MsgBox(CustomMessage('PanelOpen'), mbInformation, MB_OK);
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM optlife-panel.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
    WaitGone('optlife-panel.exe', 5);
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM optlife-panel.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  end;
  if Running('optlife-agent.exe') then
  begin
    if FileExists(ExpandConstant('{app}\optlife.exe')) then
      Exec(ExpandConstant('{app}\optlife.exe'), 'stop', '', SW_HIDE, ewWaitUntilTerminated, Code);
    WaitGone('optlife-agent.exe', 15);
    Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM optlife-agent.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
    WaitGone('optlife-agent.exe', 5);
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then DeleteFile(ExpandConstant('{app}\installing.lock'));
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    Data := ExpandConstant('{commonappdata}\OptLife');
    { A silent uninstall keeps the data: deleting is a choice only a person makes. }
    if DirExists(Data) and not UninstallSilent then
      if MsgBox(FmtMessage(CustomMessage('KeepData'), [Data]), mbConfirmation, MB_YESNO) = IDNO then
        DelTree(Data, True, True, True);
  end;
end;
