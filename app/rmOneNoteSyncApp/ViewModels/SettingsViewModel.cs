using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using rmOneNoteSyncApp.Models;
using rmOneNoteSyncApp.Services.Interfaces;

namespace rmOneNoteSyncApp.ViewModels;

public partial class SettingsViewModel : ViewModelBase
{
    private readonly IDatabaseService _databaseService;
    private readonly ISshService _sshService;
    private readonly IStartupService _startupService;
    private readonly ISyncService _syncService;
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
    public SettingsViewModel(IDatabaseService databaseService, ISshService sshService, ISyncService syncService, IConfigurationProviderService configProvider, IStartupService startupService)
    {
        _databaseService = databaseService;
        _sshService = sshService;
        _syncService = syncService;
        _configProvider = configProvider;
        _startupService = startupService;

        try
        {
            _logger = App.ServiceProvider?.GetService<ILogger<SettingsViewModel>>();
        }
        catch { }

        _runOnStartup = _startupService.IsStartupEnabled();
        _sshService.OnConnectionChanged += SshServiceOnConnectionChanged;
        SshServiceOnConnectionChanged(this, _sshService.IsConnected);
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
    private async Task SaveSettingsAsync()
    {
        _configuration.EnableWifiSync = EnableWifiSync;
        _configuration.SyncIntervalSeconds = SyncIntervalSeconds;
        _configuration.AutoSync = AutoSync;
        _configuration.MaxCacheSizeMB = MaxCacheSizeMB;
        _configuration.CacheRetentionDays = CacheRetentionDays;
        _configuration.KeepLocalCopies = KeepLocalCopies;

        await _databaseService.SaveConfigurationAsync(_configuration);

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

        _logger?.LogInformation("Settings saved and deployed to background workers");
    }

    [RelayCommand]
    private async Task DisconnectDeviceAsync()
    {
        try
        {
            _logger?.LogInformation("Disconnecting device and resetting configuration");

            // Disconnect SSH
            if (_sshService.IsConnected)
            {
                await _sshService.DisconnectAsync();
            }

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

            _logger?.LogInformation("Device disconnected and configuration reset");

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
                _logger?.LogInformation("Restarting application: {Path}", exePath);

                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true
                });

                if (App.Current.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    desktop.Shutdown();
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to disconnect device");
        }
    }

    [RelayCommand]
    private async Task ClearCacheAsync()
    {
        await _databaseService.ClearCacheAsync();
        _logger?.LogInformation("Cache cleared");
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

            _logger?.LogInformation("Restarting reMarkable sync services");

            // Stop the services first
            await _sshService.ExecuteCommandAsync("systemctl stop onenote-sync-watcher");
            await _sshService.ExecuteCommandAsync("systemctl stop onenote-sync-httpclient");

            // Wait a moment for services to fully stop
            await Task.Delay(2000);

            // Start the services again
            await _sshService.ExecuteCommandAsync("systemctl start onenote-sync-watcher");
            await _sshService.ExecuteCommandAsync("systemctl start onenote-sync-httpclient");

            // Wait for services to start
            await Task.Delay(2000);

            // Check service status
            var watcherStatus = await _sshService.CheckServiceStatusAsync("onenote-sync-watcher");
            var httpClientStatus = await _sshService.CheckServiceStatusAsync("onenote-sync-httpclient");

            if (watcherStatus && httpClientStatus)
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