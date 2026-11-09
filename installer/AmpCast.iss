; AmpCast installer (Inno Setup 6.3+)
; Build: run ..\build.ps1, or ISCC.exe AmpCast.iss after "dotnet publish" + the native driver build.

#define AppName      "AmpCast"
#define AppVersion   "1.0.2"
#define AppPublisher "desNotto"
#define AppExe       "AmpCast.exe"
#define PublishDir   "..\build\publish"

; Virtual audio cable package (signed .inf/.sys/.cat), see ..\driver\README.md.
; Change these two lines if your package uses other names.
#define CableInf     "AmpCastCable.inf"
#define CableHwId    "Root\AmpCastCable"
#define HasCable     FileExists(AddBackslash(SourcePath) + "..\driver\" + CableInf)

[Setup]
AppId={{B3E0C1A4-6F2D-4C8E-9A71-2D5C0F4A8E11}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppCopyright=© {#AppPublisher}. Free software.
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=admin
MinVersion=10.0
OutputDir=..\build\installer
OutputBaseFilename=AmpCast-Setup-{#AppVersion}
SetupIconFile=..\src\AmpCast\Assets\AmpCast.ico
UninstallDisplayIcon={app}\{#AppExe}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"; InfoAfterFile: "after-install-ru.txt"
Name: "english"; MessagesFile: "compiler:Default.isl"; InfoAfterFile: "after-install-en.txt"

[CustomMessages]
russian.TaskCable=Установить виртуальный аудиокабель «AmpCast Cable» (микрофон для Discord)
english.TaskCable=Install the "AmpCast Cable" virtual audio device (Discord microphone)
russian.StepAsio=Регистрация драйвера AmpCast ASIO
english.StepAsio=Registering the AmpCast ASIO driver
russian.StepCable=Установка AmpCast Cable
english.StepCable=Installing AmpCast Cable
russian.StepFailed=%1: ошибка %2.%nПодробности: TEMP\AmpCast-setup.log
english.StepFailed=%1 failed with code %2.%nDetails: TEMP\AmpCast-setup.log

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
#if HasCable
Name: "cable"; Description: "{cm:TaskCable}"
#endif

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "AmpCastAsio*.dll"; Flags: ignoreversion recursesubdirs createallsubdirs
; The ASIO DLLs may be loaded by a running DAW: replace them after reboot if locked.
Source: "{#PublishDir}\AmpCastAsio64.dll"; DestDir: "{app}"; Flags: ignoreversion restartreplace uninsrestartdelete skipifsourcedoesntexist
Source: "{#PublishDir}\AmpCastAsio32.dll"; DestDir: "{app}"; Flags: ignoreversion restartreplace uninsrestartdelete skipifsourcedoesntexist
#if HasCable
Source: "..\driver\*"; Excludes: "README.md"; DestDir: "{app}\driver"; Flags: ignoreversion
#endif

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{app}\{#AppExe}"; Parameters: "--unregister-asio"; Flags: runhidden waituntilterminated; RunOnceId: "UnregisterAsio"
#if HasCable
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall-driver ""{#CableHwId}"" ""{app}\driver\{#CableInf}"""; Flags: runhidden waituntilterminated; RunOnceId: "UninstallCable"
#endif

[UninstallDelete]
Type: filesandordirs; Name: "{app}\driver"

[Code]
var
  RebootNeeded: Boolean;

{ Runs an admin step of AmpCast.exe silently. 3010 = success, reboot required. }
procedure RunStep(const Params, Title: String);
var
  Code: Integer;
begin
  WizardForm.StatusLabel.Caption := Title + '...';
  if not Exec(ExpandConstant('{app}\{#AppExe}'), Params, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  if Code = 3010 then
    RebootNeeded := True
  else if Code <> 0 then
    SuppressibleMsgBox(FmtMessage(CustomMessage('StepFailed'), [Title, IntToStr(Code)]), mbError, MB_OK, IDOK);
end;

{ AmpCast hides to the tray instead of closing, so Restart Manager may not stop it:
  end it before files are replaced or removed. Settings are saved on every change. }
procedure StopAmpCast();
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopAmpCast();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopAmpCast();
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
  begin
    RunStep('--register-asio', CustomMessage('StepAsio'));
#if HasCable
    if WizardIsTaskSelected('cable') then
      RunStep('--install-driver "' + ExpandConstant('{app}\driver\{#CableInf}') + '" "{#CableHwId}"', CustomMessage('StepCable'));
#endif
  end;
end;

function NeedRestart(): Boolean;
begin
  Result := RebootNeeded;
end;
