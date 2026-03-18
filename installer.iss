[Setup]
; Basic app info
AppName=rmOneNoteSyncApp
AppVersion=1.0.0
Publisher=excustic

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