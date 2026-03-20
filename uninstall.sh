#!/bin/bash
echo "Uninstalling reMarkable OneNote Sync..."

# 1. Remove the desktop shortcuts and auto-start entries
rm -f ~/.local/share/applications/rmOneNoteSync.desktop
rm -f ~/.config/autostart/rmOneNoteSyncApp.desktop

# 2. Remove the icon
rm -f ~/.local/share/icons/hicolor/256x256/apps/rmOneNoteSyncApp.png

# 3. Remove the binaries
rm -rf ~/.local/bin/rmOneNoteSync/

# NEW: 4. Ask to wipe local app data
echo ""
read -p "Do you want to permanently delete your local application data (database, settings, sync history)? [y/N] " -n 1 -r
echo ""
if [[ $REPLY =~ ^[Yy]$ ]]
then
    rm -rf ~/.local/share/rmOneNoteSyncApp
    echo "Local data wiped."
else
    echo "Local data preserved."
fi

echo "Uninstallation complete. Farewell!"