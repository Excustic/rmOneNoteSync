using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using rmOneNoteSyncApp.Models;
using rmOneNoteSyncApp.Services;
using rmOneNoteSyncApp.Services.Interfaces;

namespace rmOneNoteSyncApp.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly IDeviceDetectionService _detectionService;
    private readonly ISshService _sshService;
    private readonly IDeploymentService _deploymentService;
    private readonly IDatabaseService _databaseService;
    private readonly ISyncServerService _syncServer;
    private readonly ILogger<MainViewModel> _logger;
    private SyncConfiguration? _configuration;

    [ObservableProperty]
    private ConnectionState _connectionState = ConnectionState.Disconnected;

    [ObservableProperty]
    private DeviceInfo? _currentDevice;

    [ObservableProperty]
    private string _devicePassword = string.Empty;

    [ObservableProperty]
    private bool _showSetupScreen = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentViewModel))]
    private string _currentView = "Dashboard";

    [ObservableProperty]
    private ViewModelBase? _currentViewModel;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isAuthenticated;

    [ObservableProperty]
    private bool _isOneNoteConfigured;

    [ObservableProperty]
    private string _deviceStatusText = "No device connected";

    [ObservableProperty]
    private string _connectionStateText = "Disconnected";

    [ObservableProperty]
    private string _authenticationError = "";

    [ObservableProperty]
    private string _oneNoteStatusText = "";

    [ObservableProperty]
    private bool _isAuthenticating;

    public bool CanConnect => IsConnected && !string.IsNullOrWhiteSpace(DevicePassword) && !IsAuthenticating &&
                              !IsAuthenticated;
    public bool CanCompleteSetup => IsAuthenticated && IsOneNoteConfigured;
    public bool HasAuthenticationError => !string.IsNullOrEmpty(AuthenticationError);

    public MainViewModel(
        IDeviceDetectionService detectionService,
        ISshService sshService,
        IDeploymentService deploymentService,
        IDatabaseService databaseService,
        IOneNoteAuthService oneNoteAuth,
        ISyncServerService syncServer,
        ILogger<MainViewModel> logger)
    {
        _detectionService = detectionService;
        _sshService = sshService;
        _deploymentService = deploymentService;
        _databaseService = databaseService;
        _syncServer = syncServer;
        _logger = logger;

        // Initialize with Dashboard view model
        CurrentViewModel = App.ServiceProvider?.GetRequiredService<DashboardViewModel>();

        // Check testing mode
        if (AppSettings.TestingMode)
        {
            _logger.LogWarning("TESTING MODE ENABLED");

            if (AppSettings.TestMode.SkipDeviceConnection)
            {
                // Simulate device connection
                CurrentDevice = new DeviceInfo
                {
                    IpAddress = AppSettings.TestMode.TestDeviceIp,
                    InterfaceName = "test0",
                    DetectedAt = DateTime.Now,
                    ConnectionType = DeviceConnectionType.USB
                };
                IsConnected = true;
                IsAuthenticated = true;
                DeviceStatusText = "TEST MODE - Simulated Device";
                ConnectionStateText = "Test Connected";
            }

            if (AppSettings.TestMode.SkipOneNoteAuth)
            {
                // Simulate OneNote authentication
                IsOneNoteConfigured = true;
                OneNoteStatusText = "TEST MODE - OneNote Bypassed";
            }
        }

        // Check if already configured
        Task.Run(async () =>
        {
            var config = await _databaseService.GetConfigurationAsync();
            ShowSetupScreen = config is null or { DevicePassword: "", DeviceIp: "" };

            if (!ShowSetupScreen)
            {
                // Make sure we're on the main thread for UI updates
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    CurrentView = "Dashboard";
                    CurrentViewModel = new DashboardViewModel(_databaseService, _sshService, _syncServer);
                });
            }
        });

        // Subscribe to device connection events
        _detectionService.DeviceConnectionChanged += OnConnectionChanged;

        // Start monitoring for devices
        Task.Run(async () => await _detectionService.StartMonitoringAsync());

        _oneNoteAuth = oneNoteAuth;

        // Check if already authenticated with OneNote
        Task.Run(async () =>
        {
            var silentAuth = await _oneNoteAuth.SignInSilentAsync();
            if (silentAuth.Success)
            {
                IsOneNoteConfigured = true;
                OneNoteStatusText = $"Signed in as {silentAuth.UserName}";
            }
        });
    }

    private async Task ReconnectSSH()
    {
        _configuration = await _databaseService.GetConfigurationAsync();

        if (_detectionService.IsConnected)
        {
            var d = _detectionService.CurrentDevice;
            if (!_sshService.IsConnected)
            {
                _logger?.LogInformation("Reconnecting SSH using IP: {IP}, Password: {PASS}",
                    _configuration?.DeviceIp ?? string.Empty, _configuration?.DevicePassword ?? string.Empty);
                await _sshService.ConnectAsync(_configuration?.DeviceIp ?? string.Empty,
                    _configuration?.DevicePassword ?? string.Empty);
            }
        }
        else
        {
            await _sshService.DisconnectAsync();
        }

        _logger?.LogInformation("DetectionServiceConnected: {DET}, SSHConnected: {SSH}",
            _detectionService.IsConnected, _sshService.IsConnected);
    }

    partial void OnDevicePasswordChanged(string value)
    {
        ConnectCommand.NotifyCanExecuteChanged(); // Notify the command to re-evaluate its state
        OnPropertyChanged(nameof(CanConnect));
    }

    private void OnConnectionChanged(object? sender, DeviceConnectionEventArgs e)
    {
        // Ensure UI updates happen on the main thread
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            CurrentDevice = e.Device;
            IsConnected = e.IsConnected;

            if (e is { IsConnected: true, Device: not null })
            {
                DeviceStatusText = $"Device detected at {e.Device.IpAddress}";
                ConnectionStateText = "USB Connected";
                ConnectionState = ConnectionState.Configured;
            }
            else
            {
                DeviceStatusText = "No device connected";
                ConnectionStateText = "Disconnected";
                DevicePassword = string.Empty;
                IsAuthenticated = false;
                ConnectionState = ConnectionState.Disconnected;

            }

            _ = ReconnectSSH();
            OnPropertyChanged(nameof(CanConnect));
        });
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (CurrentDevice == null) return;

        try
        {
            IsAuthenticating = true;
            AuthenticationError = "";

            var connected = await _sshService.ConnectAsync(CurrentDevice.IpAddress, DevicePassword);

            if (connected)
            {
                IsAuthenticated = true;
                DeviceStatusText = "Connected and authenticated";

                // Enable Wi-Fi
                await _sshService.EnableWifiOverSshAsync();

                // Deploy services if needed
                var installStatus = await _deploymentService.CheckInstallationAsync(_sshService);
                if (!installStatus.IsInstalled)
                {
                    await _deploymentService.DeployAsync(_sshService);
                }
            }
            else
            {
                AuthenticationError = "Authentication failed. Please check the password.";
            }
        }
        catch (Exception ex)
        {
            AuthenticationError = $"Connection failed: {ex.Message}";
            _logger.LogError(ex, "Failed to connect to device");
        }
        finally
        {
            IsAuthenticating = false;
            ConnectCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanCompleteSetup));
            CompleteSetupCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private async Task SignInToOneNoteAsync()
    {
        if (AppSettings.TestingMode)
        {
            IsOneNoteConfigured = true;
            OnPropertyChanged(nameof(CanCompleteSetup));
            CompleteSetupCommand.NotifyCanExecuteChanged();
            return;
        }
        try
        {
            OneNoteStatusText = "Signing in...";
            var result = await _oneNoteAuth.SignInAsync();

            if (result.Success)
            {
                IsOneNoteConfigured = true;
                OneNoteStatusText = $"Signed in as {result.UserName}";
                _logger.LogInformation("Successfully authenticated with OneNote as {User}", result.UserName);
            }
            else
            {
                OneNoteStatusText = "Sign in failed";
                _logger.LogWarning("OneNote authentication failed: {Error}", result.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            OneNoteStatusText = "Sign in error";
            _logger.LogError(ex, "OneNote sign in error");
        }

        OnPropertyChanged(nameof(CanCompleteSetup));
        CompleteSetupCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanCompleteSetup))]
    private async Task CompleteSetupAsync()
    {
        // In test mode, bypass some checks
        if (AppSettings.TestingMode)
        {
            _logger.LogWarning("Completing setup in TEST MODE");
        }

        // Save configuration
        var config = new SyncConfiguration
        {
            DeviceIp = CurrentDevice?.IpAddress ?? "10.11.99.1",
            DevicePassword = this.DevicePassword,
            EnableWifiSync = true,
            AutoSync = true
        };

        await _databaseService.SaveConfigurationAsync(config);
        
        // Wait briefly to allow services to transition state
        await Task.Delay(500);

        // Switch to main interface on UI thread
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            ShowSetupScreen = false;
            CurrentView = "Dashboard";
            CurrentViewModel = App.ServiceProvider?.GetRequiredService<DashboardViewModel>();
        });
    }

    [RelayCommand]
    private void Navigate(string viewName)
    {
        CurrentView = viewName;

        // Create the appropriate view model based on the navigation target
        CurrentViewModel = viewName switch
        {
            "Dashboard" => App.ServiceProvider?.GetRequiredService<DashboardViewModel>(),
            "FileBrowser" => App.ServiceProvider?.GetRequiredService<FolderPickerViewModel>(),
            "Settings" => App.ServiceProvider?.GetRequiredService<SettingsViewModel>(),
            "SyncStatus" => App.ServiceProvider?.GetRequiredService<SyncStatusViewModel>(),
            "Logs" => App.ServiceProvider?.GetRequiredService<LogsViewModel>(),
            _ => CurrentViewModel
        };

        // Explicitly notify that CurrentView has changed
        OnPropertyChanged(nameof(CurrentView));
    }

    // In MainViewModel constructor, add:
    private readonly IOneNoteAuthService _oneNoteAuth;
}