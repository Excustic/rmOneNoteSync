using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using rmOneNoteSyncApp.Services.Interfaces;

namespace rmOneNoteSyncApp.Services;

public class StartupService : IStartupService
{
    private const string AppName = "rmOneNoteSyncApp";
    private const string Name = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    public bool IsStartupEnabled()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(Name, false);
            return key?.GetValue(AppName) != null;
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            string autostartPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "autostart", $"{AppName}.desktop");
            return File.Exists(autostartPath);
        }
        return false; // macOS implementation can be added via .plist later
    }

    public void SetStartup(bool enable)
    {
        string exePath = Environment.ProcessPath ?? "";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(Name, true);
            if (enable)
                key?.SetValue(AppName, $"\"{exePath}\"");
            else
                key?.DeleteValue(AppName, false);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            string autostartDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "autostart");
            string autostartPath = Path.Combine(autostartDir, $"{AppName}.desktop");

            if (enable)
            {
                Directory.CreateDirectory(autostartDir);

                // Environment.ProcessPath ALWAYS returns the absolute path (e.g., /home/excustic/.local/bin/...)
                // We wrap it in quotes to protect against spaces in folder names
                string desktopContent =
                    "[Desktop Entry]\n" +
                    "Type=Application\n" +
                    "Name=reMarkable OneNote Sync\n" +
                    "Comment=Synchronize reMarkable notebooks to OneNote\n" +
                    $"Exec=\"{exePath}\"\n" +
                    "Icon=rmOneNoteSyncApp\n" +
                    "Terminal=false\n" +
                    "Categories=Office;Utility;";

                File.WriteAllText(autostartPath, desktopContent);
            }
            else if (File.Exists(autostartPath))
            {
                File.Delete(autostartPath);
            }
        }
    }
}