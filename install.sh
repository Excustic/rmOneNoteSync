#!/bin/bash
echo "Installing reMarkable OneNote Sync..."

# 1. Create local user directories
mkdir -p ~/.local/bin/rmOneNoteSync
mkdir -p ~/.local/share/applications
mkdir -p ~/.local/share/icons/hicolor/256x256/apps
mkdir -p ~/.config/autostart

# 2. Copy the binaries
cp -r ./* ~/.local/bin/rmOneNoteSync/
chmod +x ~/.local/bin/rmOneNoteSync/rmOneNoteSyncApp

# 3. Copy the Icon
cp Assets/app-icon.png ~/.local/share/icons/hicolor/256x256/apps/rmOneNoteSyncApp.png
cp Assets/rmOneNoteSync.desktop ~/.config/autostart/

# 4. Install the Desktop shortcut
cp Assets/rmOneNoteSync.desktop ~/.local/share/applications/
# Update the Exec path in the desktop file to point to the new location
sed -i "s|^Exec=.*|Exec=$HOME/.local/bin/rmOneNoteSync/rmOneNoteSyncApp|g" ~/.local/share/applications/rmOneNoteSync.desktop

echo "Installation complete! You can now find the app in your application launcher."