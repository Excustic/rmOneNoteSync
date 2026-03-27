; --- PREPROCESSOR VARIABLES ---
#define MyAppName "rmOneNoteSyncApp"
#define MyAppVersion "0.0.0-PLACEHOLDER"
#define MyAppPublisher "excustic"
#define MyAppExeName "rmOneNoteSyncApp.exe"

[Setup]
; Basic app info mapped to the variables above
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/Excustic/rmOneNoteSync
AppSupportURL=https://github.com/Excustic/rmOneNoteSync/issues
AppUpdatesURL=https://github.com/Excustic/rmOneNoteSync/releases
SetupIconFile=app\rmOneNoteSyncApp\Assets\app-icon.ico
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#MyAppExeName}

; Where it installs by default (C:\Program Files\rmOneNoteSyncApp)
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}

; Where to output the finished Setup.exe
OutputDir=artifacts\windows-installer
OutputBaseFilename=rmOneNoteSyncApp-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64compatible

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
; Grabs everything out of the folder GitHub Actions just built
Source: "artifacts\windows\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Creates the Start Menu shortcut
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
; Creates the Desktop shortcut (tied to the Task above)
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; Add firewall rule on install (profile=any is required because the reMarkable USB connection defaults to a "Public" network)
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""rmOneNoteSync"" dir=in action=allow protocol=TCP localport=34983 profile=any"; Flags: runhidden

[UninstallRun]
; Remove the firewall rule when the user uninstalls the app
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""rmOneNoteSync"" protocol=TCP localport=34983"; Flags: runhidden

[UninstallDelete]
; 1. Delete any leftover files (like runtime logs) the app created in the installation folder
Type: filesandordirs; Name: "{app}\*"

; 2. Delete the application folder itself
Type: dirifempty; Name: "{app}"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // Trigger this right after the main uninstallation is finished
  if CurUninstallStep = usPostUninstall then
  begin
    if MsgBox('Do you want to permanently delete your local application data? (This includes your database, sync history, and OneNote login)', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = idYes then
    begin
      // '{localappdata}' maps to C:\Users\Username\AppData\Local
      DelTree(ExpandConstant('{localappdata}\{#MyAppName}'), True, True, True);
    end;
  end;
end;