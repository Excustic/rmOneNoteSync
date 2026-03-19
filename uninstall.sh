#!/bin/bash
echo "Uninstalling reMarkable OneNote Sync..."

# 1. Remove the desktop shortcuts and auto-start entries
rm -f ~/.local/share/applications/rmOneNoteSync.desktop
rm -f ~/.config/autostart/rmOneNoteSyncApp.desktop

# 2. Remove the icon
rm -f ~/.local/share/icons/hicolor/256x256/apps/rmOneNoteSyncApp.png

# 3. Remove the binaries and the folder itself
rm -rf ~/.local/bin/rmOneNoteSync/

echo "Uninstallation complete. Farewell!"