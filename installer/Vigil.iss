; Vigil installer (Inno Setup 6). Build it with ..\build-installer.ps1, which publishes the app first and
; passes the version:  ISCC /DAppVersion=0.1.0 Vigil.iss
;
; Asks whether to install for the current user only (the default, for which Windows does not ask for administrator
; rights) or for all users, under Program Files. Only a copy under Program Files may start in administrator mode
; without a Windows prompt each time (Installation.ForAllUsers in Settings.cs): no one can change its files without
; administrator rights.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define AppName "Vigil"
#define AppExe "Vigil.exe"
; The AppId's GUID, without Inno's escaping brace: the uninstall key is "<guid>_is1".
#define AppGuid "{9E4B7A21-3F6C-4D8E-B5A2-71C0D94E6F38}"
; Where build-installer.ps1 publishes the app.
#define PublishDir "..\publish"

[Setup]
; Identifies the app to Windows across versions: never change it.
AppId={{9E4B7A21-3F6C-4D8E-B5A2-71C0D94E6F38}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
; "Install for me only" or "Install for all users", or /CURRENTUSER and /ALLUSERS, which the update from the tray
; passes (Updater.cs). The question is asked every time instead of taken from the earlier install, so that a copy
; installed for one user can move to Program Files by running Setup again (SwitchToAllUsers below).
PrivilegesRequiredOverridesAllowed=dialog
UsePreviousPrivileges=no
; "Start with Windows" stays in the user's own Run key in an install for all users too: it is the entry the app
; reads and writes (Settings.cs, Autostart).
UsedUserAreasWarning=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 version 2004: the media API behind the music view.
MinVersion=10.0.19041
WizardStyle=modern
SetupIconFile=..\Vigil\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
OutputDir=..\dist
OutputBaseFilename=Vigil-Setup-{#AppVersion}
; The app holds this mutex while it runs: Setup and the uninstaller ask to close it instead of failing on files in use.
AppMutex=Vigil

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "pt"; MessagesFile: "compiler:Languages\Portuguese.isl"
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"

[CustomMessages]
en.StartWithWindows=Start Vigil when I sign in to Windows
pt.StartWithWindows=Iniciar o Vigil quando inicio sessão no Windows
ptbr.StartWithWindows=Iniciar o Vigil quando eu entrar no Windows
es.StartWithWindows=Iniciar Vigil al iniciar sesión en Windows
fr.StartWithWindows=Démarrer Vigil à l'ouverture de session Windows
de.StartWithWindows=Vigil bei der Windows-Anmeldung starten
it.StartWithWindows=Avvia Vigil all'accesso a Windows

[Tasks]
Name: "startup"; Description: "{cm:StartWithWindows}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Same value the app writes for "Start with Windows" (Settings.cs, Autostart), so the app shows it as on.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; \
  ValueData: """{app}\{#AppExe}"" --tray"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; An update started from the tray (Updater.cs) runs silently with /update=1: start the app again afterwards.
; Not with the installer's administrator rights: in administrator mode the app gets them itself.
Filename: "{app}\{#AppExe}"; Parameters: "--tray"; Flags: nowait runasoriginaluser; Check: IsUpdate

[UninstallDelete]
; The window's browser cache that 0.1.0 to 0.3.0 kept here in administrator mode, in an install for all users.
Type: filesandordirs; Name: "{app}\WebView2"
; The window's browser cache and the log. The settings (%AppData%\Vigil\settings.json) are kept for a later reinstall.
Type: filesandordirs; Name: "{localappdata}\{#AppName}\WebView2"
Type: filesandordirs; Name: "{localappdata}\{#AppName}\logs"
Type: dirifempty; Name: "{localappdata}\{#AppName}"

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppGuid}_is1';

function IsUpdate: Boolean;
begin
  Result := ExpandConstant('{param:update|0}') = '1';
end;

// Installing for one user while a copy for all users is there would leave two copies, both starting at sign-in.
function InitializeSetup: Boolean;
begin
  Result := True;
  if not IsAdminInstallMode and RegKeyExists(HKEY_LOCAL_MACHINE, UninstallKey) then
  begin
    SuppressibleMsgBox('Vigil is already installed for all users. Run Setup again and choose "Install for all ' +
      'users" to update it, or uninstall it first.', mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

// Moving from an install for one user to one for all users: remove the old copy first, so that only the one under
// Program Files is left and the start-up entry points at it. The settings are kept, as on any uninstall.
procedure SwitchToAllUsers;
var
  Uninstaller: String;
  Code, Waited: Integer;
begin
  if not IsAdminInstallMode then Exit;
  if not RegQueryStringValue(HKEY_CURRENT_USER, UninstallKey, 'UninstallString', Uninstaller) then Exit;
  Uninstaller := RemoveQuotes(Uninstaller);
  if not FileExists(Uninstaller) then Exit;
  Log('Removing the copy installed for this user only: ' + Uninstaller);
  if not Exec(Uninstaller, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, Code) then
  begin
    Log('Could not remove it: ' + SysErrorMessage(Code));
    Exit;
  end;
  // The uninstaller hands over to a copy of itself in the temp folder and may return before that copy is done:
  // wait for its key to go, for up to 30 seconds.
  Waited := 0;
  while RegKeyExists(HKEY_CURRENT_USER, UninstallKey) and (Waited < 30000) do
  begin
    Sleep(250);
    Waited := Waited + 250;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then SwitchToAllUsers;
end;

function Schtasks(const Params: String): Integer;
begin
  if not Exec(ExpandConstant('{sys}\schtasks.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated, Result) then
    Result := -1;
end;

// Administrator mode adds a scheduled task (Settings.cs, Autostart). It is created with administrator rights and
// can only be removed with them: when the plain attempt fails, ask Windows for them once, so the task is not left
// behind pointing at a file that no longer exists. A silent uninstall skips the prompt.
procedure RemoveTask;
var
  Code: Integer;
begin
  if Schtasks('/Query /TN "{#AppName}"') <> 0 then Exit;
  if Schtasks('/Delete /TN "{#AppName}" /F') = 0 then Exit;
  if not UninstallSilent then
    ShellExec('runas', ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN "{#AppName}" /F', '', SW_HIDE,
      ewWaitUntilTerminated, Code);
end;

// "Start with Windows" switched on inside the app, without the installer task, is not known to the uninstaller.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    RemoveTask;
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#AppName}');
    // What Task Manager remembers when the entry was switched off under Startup apps.
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run', '{#AppName}');
  end;
end;
