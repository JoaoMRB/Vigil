; Vigil installer (Inno Setup 6). Build it with ..\build-installer.ps1, which publishes the app first and
; passes the version:  ISCC /DAppVersion=0.1.0 Vigil.iss
;
; Installs for the current user only, so Windows does not ask for administrator rights.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define AppName "Vigil"
#define AppExe "Vigil.exe"
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
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
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

[UninstallRun]
; "Start with Windows" in administrator mode is a scheduled task (Settings.cs). Removing it needs the rights it
; was created with: when that fails the task is left behind, pointing at a file that no longer exists.
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""{#AppName}"" /F"; Flags: runhidden; RunOnceId: "RemoveStartupTask"

[UninstallDelete]
; The window's browser cache. The settings (%AppData%\Vigil\settings.json) are kept for a later reinstall.
Type: filesandordirs; Name: "{localappdata}\{#AppName}\WebView2"
Type: dirifempty; Name: "{localappdata}\{#AppName}"

[Code]
// "Start with Windows" switched on inside the app, without the installer task, is not known to the uninstaller.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#AppName}');
end;
