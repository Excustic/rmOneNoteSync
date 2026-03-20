using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using rmOneNoteSyncApp.Models;
using rmOneNoteSyncApp.Services.Interfaces;
using rmOneNoteSyncApp.Views;

namespace rmOneNoteSyncApp.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly IDatabaseService _databaseService;
    private readonly ISshService _sshService;
    private readonly IStartupService _startupService;
    private readonly ISyncService _syncService;
    private readonly IOneNoteAuthService _oneNoteAuthService;
    private readonly IConfigurationProviderService _configProvider;
    private readonly ILogger<SettingsViewModel>? _logger;

    private SyncConfiguration? _configuration;

    [ObservableProperty]
    private bool _enableWifiSync;

    [ObservableProperty]
    private int _syncIntervalSeconds;

    [ObservableProperty]
    private bool _autoSync;

    [ObservableProperty]
    private long _maxCacheSizeMB;

    [ObservableProperty]
    private int _cacheRetentionDays;

    [ObservableProperty]
    private bool _keepLocalCopies;

    [ObservableProperty]
    private bool _isDeviceConnected;

    [ObservableProperty]
    private bool _isRestartingService;

    [ObservableProperty]
    private string _serviceStatus = "Unknown";

    [ObservableProperty]
    private string _deviceInfo = "No device connected";
    [ObservableProperty]
    private bool _runOnStartup;
    [ObservableProperty]
    private bool _isOneNoteAuthenticated;
    private bool _isDeviceCacheClearing;
    [ObservableProperty] private string _clearDeviceCacheButtonText = "Clear Device Sync Cache";
    [ObservableProperty] private string _clearDeviceCacheButtonBg = "#f3f4f6"; // Default Light Gray
    [ObservableProperty] private string _clearDeviceCacheButtonFg = "#374151"; // Default Dark Text
    private bool _isClearingCache;
    [ObservableProperty] private string _clearCacheButtonText = "Clear DB Cache";
    [ObservableProperty] private string _clearCacheButtonBg = "#f3f4f6"; // Default Light Gray
    [ObservableProperty] private string _clearCacheButtonFg = "#374151"; // Default Dark Text
    [ObservableProperty] private string _saveSettingsButtonText = "Save Settings";
    [ObservableProperty] private string _SaveSettingsButtonBg = "#2563eb"; // Default Light Gray
    [ObservableProperty] private string _SaveSettingsButtonFg = "#f0f4fe"; // Default Dark Text
    public SettingsViewModel(IDatabaseService databaseService,
        ISshService sshService, ISyncService syncService,
        IConfigurationProviderService configProvider,
        IStartupService startupService,
        IOneNoteAuthService oneNoteAuthService)
    {
        _databaseService = databaseService;
        _sshService = sshService;
        _syncService = syncService;
        _configProvider = configProvider;
        _startupService = startupService;
        _oneNoteAuthService = oneNoteAuthService;

        try
        {
            _logger = App.ServiceProvider?.GetService<ILogger<SettingsViewModel>>();
        }
        catch { }

        _runOnStartup = _startupService.IsStartupEnabled();
        _sshService.OnConnectionChanged += SshServiceOnConnectionChanged;
        SshServiceOnConnectionChanged(this, _sshService.IsConnected);
        _oneNoteAuthService.AuthenticationStateChanged += (s, e) =>
        {
            IsOneNoteAuthenticated = _oneNoteAuthService.IsAuthenticated;
        };
        IsOneNoteAuthenticated = _oneNoteAuthService.IsAuthenticated;
        // Load configuration
        Task.Run(LoadSettingsAsync);
    }

    partial void OnRunOnStartupChanged(bool value)
    {
        _startupService.SetStartup(value);
    }
    private void SshServiceOnConnectionChanged(object? sender, bool e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (e)
            {
                Task.Run(async () =>
                {
                    var info = await _sshService.GetDeviceInfoAsync();
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        DeviceInfo = $"Connected to {info.GetValueOrDefault("Model", "reMarkable")}";
                    });
                });
            }
            else
            {
                DeviceInfo = "No device connected.";
                ServiceStatus = "N/A";
            }

            IsDeviceConnected = e;
            OnPropertyChanged(nameof(IsDeviceConnected));
            OnPropertyChanged(nameof(ServiceStatus));
            OnPropertyChanged(nameof(DeviceInfo));
            DisconnectDeviceCommand.NotifyCanExecuteChanged();
        });
    }

    private async Task LoadSettingsAsync()
    {
        var config = await _databaseService.GetConfigurationAsync();
        if (config != null)
        {
            _configuration = config;
            EnableWifiSync = config.EnableWifiSync;
            SyncIntervalSeconds = config.SyncIntervalSeconds;
            AutoSync = config.AutoSync;
            MaxCacheSizeMB = config.MaxCacheSizeMB;
            CacheRetentionDays = config.CacheRetentionDays;
            KeepLocalCopies = config.KeepLocalCopies;
        }
    }
    [RelayCommand]
    private async Task SignInOneNoteAsync()
    {
        await _oneNoteAuthService.SignInAsync();
    }
    [RelayCommand]
    private async Task SignOutOneNoteAsync()
    {
        await _oneNoteAuthService.SignOutAsync();

        // The Nuclear Option: Force delete the file just in case MSAL serialization fails
        string cacheFilePath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "rmOneNoteSyncApp", "msalcache.bin");

        if (System.IO.File.Exists(cacheFilePath))
        {
            System.IO.File.Delete(cacheFilePath);
        }
    }
    // AllowConcurrentExecutions stops Avalonia from turning the button gray
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ClearDeviceCacheAsync()
    {
        // Prevent double-clicks and ensure we are actually connected
        if (_isDeviceCacheClearing || !_sshService.IsConnected) return;
        _isDeviceCacheClearing = true;

        // 1. Loading State
        ClearDeviceCacheButtonText = "Clearing...";
        ClearDeviceCacheButtonBg = "#fef08a";
        ClearDeviceCacheButtonFg = "#854d0e";

        try
        {
            // We use 'rm -f' so the command doesn't crash if the file is already gone
            await _sshService.ExecuteCommandAsync("rm -f /home/root/onenote-sync/cache/.sync_cache");

            // 2. Success State
            ClearDeviceCacheButtonText = "✅ Cache Cleared";
            ClearDeviceCacheButtonBg = "#16a34a";
            ClearDeviceCacheButtonFg = "#ffffff";
        }
        catch
        {
            // 3. Failed State (e.g., if SSH connection suddenly dropped)
            ClearDeviceCacheButtonText = "❌ Failed";
            ClearDeviceCacheButtonBg = "#ef4444";
            ClearDeviceCacheButtonFg = "#ffffff";
        }

        // Wait 2.5 seconds for the user to read the success/fail message
        await Task.Delay(2500);

        // 4. Revert to Default State
        ClearDeviceCacheButtonText = "Clear Device Sync Cache";
        ClearDeviceCacheButtonBg = "#f3f4f6";
        ClearDeviceCacheButtonFg = "#374151";

        _isDeviceCacheClearing = false;
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        _configuration.EnableWifiSync = EnableWifiSync;
        _configuration.SyncIntervalSeconds = SyncIntervalSeconds;
        _configuration.AutoSync = AutoSync;
        _configuration.MaxCacheSizeMB = MaxCacheSizeMB;
        _configuration.CacheRetentionDays = CacheRetentionDays;
        _configuration.KeepLocalCopies = KeepLocalCopies;

        // 1. Loading State
        SaveSettingsButtonText = "Saving...";
        SaveSettingsButtonBg = "#fef08a";
        SaveSettingsButtonFg = "#854d0e";

        await _databaseService.SaveConfigurationAsync(_configuration);

        // 2. Success State
        SaveSettingsButtonText = "✅ Settings Saved";
        SaveSettingsButtonBg = "#16a34a";
        SaveSettingsButtonFg = "#ffffff";

        if (AutoSync)
        {
            _ = _syncService.StartAutomaticSyncAsync(SyncIntervalSeconds);
        }
        else
        {
            _ = _syncService.StopAutomaticSyncAsync();
        }

        if (EnableWifiSync)
        {
            // Attempt to toggle the SSH WLAN feature
            _ = _sshService.EnableWifiOverSshAsync();
        }

        // Push the new interval to the watcher script inside the remarkable
        await _configProvider.UpdateDeviceConfigurationAsync(true);

        var dashboardVm = App.ServiceProvider?.GetService<DashboardViewModel>();
        if (dashboardVm != null)
        {
            Task.Run(dashboardVm.LoadDashboardDataAsync);
        }

        await Task.Delay(2000);
        // 4. Revert to Default State
        SaveSettingsButtonText = "Save Settings";
        SaveSettingsButtonBg = "#2563eb";
        SaveSettingsButtonFg = "#f3f4f6";

        _logger?.LogInformation("Settings saved.");

    }

    [RelayCommand]
    private async Task DisconnectDeviceAsync()
    {
        if (App.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainWindow = desktop.MainWindow;

            if (mainWindow == null) return;

            // 3. Set up your dialog
            var dialog = new ConfirmDialogWindow();
            var message = """
            Are you sure you want to disconnect? All data on the device will be erased.
            Otherwise, please disconnect it first to retain local data.
            """;
            var vm = new ConfirmDialogViewModel(dialog, message);

            // Don't forget to attach the ViewModel to the Window!
            dialog.DataContext = vm;

            // 4. Show the dialog using the mainWindow as the parent
            var result = await dialog.ShowDialog<bool?>(mainWindow);
            if (result != true) return;

            try
            {
                _logger?.LogInformation("Disconnecting device and resetting configuration");


                // Clear the configuration to force setup screen
                await _databaseService.ClearCacheAsync();

                // Clear saved configuration
                var config = await _databaseService.GetConfigurationAsync();
                if (config != null)
                {
                    config.DeviceIp = string.Empty;
                    config.DevicePassword = string.Empty;
                    config.ServiceVersion = string.Empty;
                    await _databaseService.SaveConfigurationAsync(config);
                }

                // Nuke the device
                try
                {
                    await _sshService.ExecuteCommandAsync("rm -rf /home/root/onenote-sync");
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Failed to erase device data.");
                }

                // Disconnect SSH
                if (_sshService.IsConnected)
                {
                    await _sshService.DisconnectAsync();
                }

                // Navigate back to setup
                if (App.ServiceProvider?.GetService<MainViewModel>() is { } mainVm)
                {
                    mainVm.ShowSetupScreen = true;
                    mainVm.ConnectionState = ConnectionState.Disconnected;
                }

                // Restart the application
                var currentProcess = Process.GetCurrentProcess();
                if (currentProcess.MainModule != null)
                {
                    var exePath = currentProcess.MainModule.FileName;
                    _logger?.LogDebug("Restarting application: {Path}", exePath);

                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exePath,
                        UseShellExecute = true
                    });

                    desktop.Shutdown();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to disconnect device");
            }
        }
    }

    // AllowConcurrentExecutions stops Avalonia from turning the button gray
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task ClearCacheAsync()
    {
        // Prevent double-clicks and ensure we are actually connected
        if (_isClearingCache || !_sshService.IsConnected) return;
        _isClearingCache = true;

        // 1. Loading State
        ClearCacheButtonText = "Clearing...";
        ClearCacheButtonBg = "#fef08a";
        ClearCacheButtonFg = "#854d0e";

        try
        {
            // We use 'rm -f' so the command doesn't crash if the file is already gone
            await _databaseService.ClearCacheAsync();
            _logger?.LogInformation("Database cache cleared");

            // 2. Success State
            ClearCacheButtonText = "✅ Cache Cleared";
            ClearCacheButtonBg = "#16a34a";
            ClearCacheButtonFg = "#ffffff";
        }
        catch
        {
            // 3. Failed State (e.g., if SSH connection suddenly dropped)
            ClearCacheButtonText = "❌ Failed";
            ClearCacheButtonBg = "#ef4444";
            ClearCacheButtonFg = "#ffffff";
        }

        // Wait 2.5 seconds for the user to read the success/fail message
        await Task.Delay(2500);

        // 4. Revert to Default State
        ClearCacheButtonText = "Clear Device Sync Cache";
        ClearCacheButtonBg = "#f3f4f6";
        ClearCacheButtonFg = "#374151";

        _isClearingCache = false;
    }
    [RelayCommand]
    private async Task CleanupOldCacheAsync()
    {
        var deleted = await _databaseService.CleanupOldCacheAsync(CacheRetentionDays);
        _logger?.LogInformation("Cleaned up {Count} old cache entries", deleted);
    }

    [RelayCommand]
    private async Task RestartServiceAsync()
    {
        try
        {
            IsRestartingService = true;
            ServiceStatus = "Restarting...";

            _logger?.LogDebug("Restarting reMarkable sync services");

            var res = await _sshService.RestartServiceAsync();
            if (res)
            {
                ServiceStatus = "Services running";
                _logger?.LogInformation("Services restarted successfully");
            }
            else
            {
                ServiceStatus = "Service error";
                _logger?.LogWarning("Services may not have started correctly");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to restart services");
            ServiceStatus = "Restart failed";
        }
        finally
        {
            IsRestartingService = false;
        }
    }

    [RelayCommand]
    private async Task CheckServiceStatusAsync()
    {
        try
        {
            if (!_sshService.IsConnected)
            {
                ServiceStatus = "Not connected";
                return;
            }

            var watcherStatus = await _sshService.CheckServiceStatusAsync("onenote-sync-watcher");
            var httpClientStatus = await _sshService.CheckServiceStatusAsync("onenote-sync-httpclient");

            if (watcherStatus && httpClientStatus)
            {
                ServiceStatus = "All services running";
            }
            else if (watcherStatus || httpClientStatus)
            {
                ServiceStatus = "Some services running";
            }
            else
            {
                ServiceStatus = "Services stopped";
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to check service status");
            ServiceStatus = "Unknown";
        }
    }

    // Check status when connecting
    partial void OnIsDeviceConnectedChanged(bool value)
    {
        if (value)
        {
            Task.Run(CheckServiceStatusAsync);
        }
    }
}