[Setup]
; Basic app info
AppName=rmOneNoteSyncApp
AppVersion=0.0.0-PLACEHOLDER
AppPublisher=excustic
AppPublisherURL=https://github.com/Excustic/rmOneNoteSync
AppSupportURL=https://github.com/Excustic/rmOneNoteSync/issues
AppUpdatesURL=https://github.com/Excustic/rmOneNoteSync/releases

; Where it installs by default (C:\Program Files\rmOneNoteSyncApp)
DefaultDirName={autopf}\rmOneNoteSyncApp
DefaultGroupName=rmOneNoteSyncApp

; Where to output the finished Setup.exe
OutputDir=artifacts\windows-installer
OutputBaseFilename=rmOneNoteSyncApp-Setup
Compression=lzma2
SolidCompression=yes
ArchitecturesInstallIn64BitMode=x64

[Tasks]
; Gives the user a checkbox on the install screen to create a desktop icon
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional icons:"

[Files]
; Grabs everything out of the folder GitHub Actions just built
Source: "artifacts\windows\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; Creates the Start Menu shortcut
Name: "{group}\rmOneNoteSyncApp"; Filename: "{app}\rmOneNoteSyncApp.exe"
; Creates the Desktop shortcut (tied to the Task above)
Name: "{autodesktop}\rmOneNoteSyncApp"; Filename: "{app}\rmOneNoteSyncApp.exe"; Tasks: desktopicon

[Run]
; 1. Tell HTTP.sys to let ANY standard user open port 8080 without Admin rights
Filename: "netsh"; Parameters: "http add urlacl url=http://*:8080/ user=EVERYONE"; Flags: runhidden
; 2. Add the Windows Firewall exception so it doesn't block incoming tablet syncs
Filename: "netsh"; Parameters: "advfirewall firewall add rule name=""rmOneNoteSyncApp"" dir=in action=allow protocol=TCP localport=8080"; Flags: runhidden

[UninstallRun]
; Clean up our mess when the user uninstalls the app!
Filename: "netsh"; Parameters: "http delete urlacl url=http://*:8080/"; Flags: runhidden
Filename: "netsh"; Parameters: "advfirewall firewall delete rule name=""rmOneNoteSyncApp"""; Flags: runhidden