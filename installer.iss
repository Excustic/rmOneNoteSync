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
UninstallDisplayIcon={app}\{#MyAppExeName}

; Where it installs by default (C:\Program Files\rmOneNoteSyncApp)
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}

; Where to output the finished Setup.exe
OutputDir=artifacts\windows-installer
OutputBaseFilename=rmOneNoteSyncApp-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64

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
; 1. Tell HTTP.sys to let ANY standard user open port 8080 without Admin rights
Filename: "netsh"; Parameters: "http add urlacl url=http://*:8080/ user=EVERYONE"; Flags: runhidden
; 2. Add the Windows Firewall exception so it doesn't block incoming tablet syncs
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""{#MyAppName}"" dir=in action=allow protocol=TCP localport=8080"; Flags: runhidden

[UninstallRun]
; Clean up our mess when the user uninstalls the app!
Filename: "netsh"; Parameters: "http delete urlacl url=http://*:8080/"; Flags: runhidden
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""{#MyAppName}"""; Flags: runhidden