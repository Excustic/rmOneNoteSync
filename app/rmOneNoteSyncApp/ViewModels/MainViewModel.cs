using System;
using System.Diagnostics;
using System.Reflection;
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
    private readonly ISoftwareUpdateService _updateService;
    private readonly IDeviceDetectionService _detectionService;
    private readonly ISshService _sshService;
    private readonly IDeploymentService _deploymentService;
    private readonly IDatabaseService _databaseService;
    private readonly ISyncService _syncService;
    private readonly ILogger<MainViewModel> _logger;
    private readonly IOneNoteAuthService _oneNoteAuth;
    private readonly IConfigurationProviderService _configProvider;
    private SyncConfiguration? _configuration;
    private string _fetchedMacAddress = string.Empty;

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
    private string _connectionStateText = "";

    [ObservableProperty]
    private string _authenticationError = "";

    [ObservableProperty]
    private string _oneNoteStatusText = "";

    [ObservableProperty]
    private bool _isAuthenticating;
    private bool _isDeploying;
    private bool _needsDeployment;
    public bool NeedsDeployment
    {
        get => _needsDeployment;
        set
        {
            if (SetProperty(ref _needsDeployment, value))
            {
                // When true, 100% opacity. When false, 0% opacity.
                DeployBannerOpacity = value ? 1.0 : 0.0;
            }
        }
    }
    [ObservableProperty]
    private double _deployProgress = 0.0;
    [ObservableProperty]
    private string _deployMessage = "";
    [ObservableProperty]
    private DeploymentStage _deployStage = DeploymentStage.Idle;

    [ObservableProperty] private double _deployBannerOpacity = 0.0;
    [ObservableProperty] private string _deployButtonText = "🔨 Fix it";
    [ObservableProperty] private string _deployButtonBg = "#ffffff";
    [ObservableProperty] private string _deployButtonFg = "#dd310fff";
    private bool _isOpeningUpdate;
    [ObservableProperty] private double _updateBannerOpacity = 0.0;
    [ObservableProperty] private string _updateButtonText = "⬇️ Download";
    [ObservableProperty] private string _updateButtonBg = "#ffffff";
    [ObservableProperty] private string _updateButtonFg = "#3b82f6";
    private bool _isUpdateAvailable;
    public bool IsUpdateAvailable
    {
        get => _isUpdateAvailable;
        set
        {
            if (SetProperty(ref _isUpdateAvailable, value))
            {
                UpdateBannerOpacity = value ? 1.0 : 0.0;
            }
        }
    }

    [ObservableProperty] private bool _isTourVisible;
    [ObservableProperty] private string _currentTourStep = "0";
    [ObservableProperty] private string _proceedButtonText = "Next Step";

    [RelayCommand]
    private void StartTour()
    {
        CurrentTourStep = "1";
        // Stay on current view (Dashboard) for the welcome step
    }

    [RelayCommand]
    private void NextTourStep()
    {
        if (!int.TryParse(CurrentTourStep, out int step)) return;
        step++;
        CurrentTourStep = step.ToString();

        switch (step)
        {
            case 2: Navigate("Settings"); break;
            case 3: Navigate("FileBrowser"); break;
            case 4: Navigate("SyncStatus"); break;
            case 5: Navigate("Dashboard"); break;
            case 6: { Navigate("Logs"); ProceedButtonText = "Finish"; }; break;
            default: DismissTour(); break;
        }
    }

    [RelayCommand]
    private void DismissTour()
    {
        IsTourVisible = false;
        CurrentTourStep = "0";
    }

    public bool CanConnect => IsConnected && !string.IsNullOrWhiteSpace(DevicePassword) && !IsAuthenticating &&
                              !IsAuthenticated;
    public bool CanCompleteSetup => IsAuthenticated && IsOneNoteConfigured;
    public bool HasAuthenticationError => !string.IsNullOrEmpty(AuthenticationError);
    private const string RepositoryURL = "https://github.com/excustic/rmOneNoteSync";

    public string AppVersion => $"v{Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "Unknown"}";

    private string _updateUrl = "";

    public MainViewModel(
        IDeviceDetectionService detectionService,
        ISshService sshService,
        IDeploymentService deploymentService,
        IDatabaseService databaseService,
        IOneNoteAuthService oneNoteAuth,
        ISyncService syncService,
        ISoftwareUpdateService updateService,
        IConfigurationProviderService configProvider,
        ILogger<MainViewModel> logger)
    {
        _detectionService = detectionService;
        _sshService = sshService;
        _deploymentService = deploymentService;
        _databaseService = databaseService;
        _syncService = syncService;
        _updateService = updateService;
        _configProvider = configProvider;
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

            // Recover any items that were left InProgress (e.g. app crashed)
            await _syncService.RecoverInProgressItemsAsync();

            ShowSetupScreen = config is null or { DevicePassword: "" };
            _detectionService.IncludeSSHConnectionCheck = !ShowSetupScreen;
            await _detectionService.StartMonitoringAsync();
            if (!ShowSetupScreen)
            {
                if (config == null || config.AutoSync)
                {
                    _logger.LogDebug("Initializing Automatic Sync (Interval: {Interval}s)", config.SyncIntervalSeconds);
                    _ = _syncService.StartAutomaticSyncAsync(config.SyncIntervalSeconds);
                }

                // _detectionService?.CheckConnectionAsync();

                // Make sure we're on the main thread for UI updates
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    CurrentView = "Dashboard";
                    CurrentViewModel = App.ServiceProvider?.GetRequiredService<DashboardViewModel>();
                });
            }
        });

        // Subscribe to device connection events
        _detectionService.DeviceConnectionChanged += OnConnectionChanged;
        _detectionService.IpAddressUpdated += OnIpAddressUpdated;
        _deploymentService.DeploymentProgress += OnDeploymentProgress;

        _oneNoteAuth = oneNoteAuth;

        CheckUpdates();

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

    private void OnDeploymentProgress(object? sender, DeploymentProgressEventArgs e)
    {
        DeployProgress = e.Progress;
        DeployMessage = e.Message;
        DeployStage = e.Stage;
    }

    private async void CheckUpdates()
    {
        var (updateAvailable, _, releaseUrl) = await _updateService.CheckForUpdatesAsync();

        if (updateAvailable)
        {
            _updateUrl = releaseUrl;
            IsUpdateAvailable = true;
        }
    }

    private async Task ReconnectSSH()
    {
        _configuration = await _databaseService.GetConfigurationAsync();

        if (_detectionService.IsConnected && _detectionService.CurrentDevice != null)
        {
            var device = _detectionService.CurrentDevice;
            if (!_sshService.IsConnected || _sshService.CurrentIp != device.IpAddress)
            {
                _logger?.LogDebug("Connecting SSH to {IP} via {Type}", device.IpAddress, device.ConnectionType);

                var connected = await _sshService.ConnectAsync(device.IpAddress, _configuration?.DevicePassword ?? string.Empty);
                if (connected)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        DeviceStatusText = $"Connected via {device.ConnectionType} at {device.IpAddress}";
                        ConnectionStateText = $"{device.ConnectionType} Connected";
                        ConnectionState = ConnectionState.Configured;
                        IsConnected = true;
                    });
                }
            }
        }
        else
        {
            if (_sshService.IsConnected)
            {
                await _sshService.DisconnectAsync();
            }

            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                ConnectionStateText = "Disconnected";
                DeviceStatusText = "No device connected";
                IsConnected = false;
            });
        }
        _logger?.LogDebug("DetectionServiceConnected: {DET}, SSHConnected: {SSH}",
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
        Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
        {
            CurrentDevice = e.Device;
            IsConnected = e.IsConnected;

            if (e is { IsConnected: true, Device: not null })
            {
                DeviceStatusText = $"Device detected at {e.Device.IpAddress}";
                ConnectionStateText = $"{e.Device.ConnectionType} Detected";
                ConnectionState = ConnectionState.Detected;

                // Grab the current desktop app version to compare against the tablet
                string currentAppVersion = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                    .InformationalVersion?.Split('+')[0] ?? "0.0.0";

                if (DevicePassword != string.Empty)
                    await ReconnectSSH();
                DeviceStatusText = $"Connected via {e.Device.ConnectionType} at {e.Device.IpAddress}";
                ConnectionStateText = $"{e.Device.ConnectionType} Connected";
                ConnectionState = ConnectionState.Configured;
                if (!ShowSetupScreen && _sshService.IsConnected)
                {
                    var installationStatus = await _deploymentService.CheckInstallationAsync();
                    NeedsDeployment = _detectionService.CurrentDevice?.SyncVersion == "Not installed" ||
                                        _detectionService.CurrentDevice?.SyncVersion == "Unknown" ||
                                        installationStatus.IsInstalled == false ||
                                    installationStatus.InstalledVersion != currentAppVersion;
                }
            }
            else
            {
                DeviceStatusText = "No device connected";
                ConnectionStateText = "Disconnected";
                DevicePassword = string.Empty;
                IsAuthenticated = false;
                ConnectionState = ConnectionState.Disconnected;
                NeedsDeployment = false;
            }

            OnPropertyChanged(nameof(CanConnect));
        });
    }

    private async void OnIpAddressUpdated(object? sender, string newIp)
    {
        _logger?.LogDebug("Device IP updated to {IP}. Synchronizing endpoints...", newIp);
        await _configProvider.RegisterEndpointAsync();
    }

    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DeployServiceAsync()
    {
        if (_isDeploying || CurrentDevice == null) return;
        _isDeploying = true; // Lock the button manually

        DeployButtonText = "Deploying...";
        DeployButtonBg = "#fef08a";
        DeployButtonFg = "#854d0e";

        var res = await _deploymentService.DeployAsync(_detectionService.CurrentDevice);

        if (res.IsInstalled)
        {
            await _sshService.RestartServiceAsync();

            DeployButtonText = "✅ Done";
            DeployButtonBg = "#16a34a";
            DeployButtonFg = "#ffffff";

            await Task.Delay(2000);
            NeedsDeployment = false; // Fades the banner out!
        }
        else
        {
            DeployButtonText = "❌ Failed";
            DeployButtonBg = "#ef4444";
            DeployButtonFg = "#ffffff";

            await Task.Delay(3000);

            // Revert to default state
            DeployButtonText = "Fix it 🔨";
            DeployButtonBg = "#ffffff";
            DeployButtonFg = "#dd310fff";
        }

        _isDeploying = false; // Unlock
    }
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task OpenUpdateUrlAsync() // Note: changed to Async to allow Task.Delay
    {
        if (_isOpeningUpdate || string.IsNullOrEmpty(_updateUrl)) return;
        _isOpeningUpdate = true;

        UpdateButtonText = "Opening...";
        UpdateButtonBg = "#bfdbfe";
        UpdateButtonFg = "#1e3a8a";

        Process.Start(new ProcessStartInfo
        {
            FileName = _updateUrl,
            UseShellExecute = true
        });

        await Task.Delay(500); // Give the browser a second to launch

        UpdateButtonText = "✅ Opened";
        UpdateButtonBg = "#16a34a";
        UpdateButtonFg = "#ffffff";

        await Task.Delay(2000);
        IsUpdateAvailable = false; // Fades the banner out so it stops bothering them!

        _isOpeningUpdate = false;
    }

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        if (CurrentDevice == null) return;

        try
        {
            IsAuthenticating = true;
            AuthenticationError = "";

            // Check SSH connection along with ping from now on
            _detectionService.IncludeSSHConnectionCheck = true;

            var connected = await _sshService.ConnectAsync(CurrentDevice.IpAddress, DevicePassword);

            if (connected)
            {
                IsAuthenticated = true;
                DeviceStatusText = "Connected and authenticated";

                // Enable Wi-Fi
                var EnableWifi = await _sshService.EnableWifiOverSshAsync();
                string wifiIp = string.Empty;
                if (EnableWifi)
                {
                    wifiIp = await _sshService.GetWifiIpAsync() ?? string.Empty;
                }

                // Fetch MAC address
                _fetchedMacAddress = await _sshService.GetMacAddressAsync() ?? "Unknown";

                // Deploy or Update services if needed
                var installStatus = await _deploymentService.CheckInstallationAsync();

                // Grab the current desktop app version to compare against the tablet
                string currentAppVersion = Assembly.GetExecutingAssembly()
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                    .InformationalVersion?.Split('+')[0] ?? "0.0.0";

                // Save configuration
                var config = await _databaseService.GetConfigurationAsync() ?? new SyncConfiguration();

                config.DeviceIp = CurrentDevice?.IpAddress ?? AppSettings.DefaultDeviceIp;
                config.DevicePassword = this.DevicePassword;
                config.DeviceMacAddress = _fetchedMacAddress;
                config.EnableWifiSync = EnableWifi;
                config.LastNetworkIp = wifiIp;
                config.AutoSync = true;

                // Fetch latest whitelist from device (Source of Truth)
                var (deviceWhitelist, deviceFolders) = await _configProvider.FetchWhitelistFromDeviceAsync();
                if (deviceWhitelist.Count > 0 || deviceFolders.Count > 0)
                {
                    _logger.LogInformation("Importing whitelist from device: {FileCount} files, {FolderCount} folders", deviceWhitelist.Count, deviceFolders.Count);
                    config.SyncFiles = deviceWhitelist;
                    config.SyncFolders = deviceFolders;
                }

                await _databaseService.SaveConfigurationAsync(config);

                // Refresh dashboard to show imported selection
                var dashboardVm = App.ServiceProvider?.GetService<DashboardViewModel>();
                if (dashboardVm != null)
                {
                    _ = Task.Run(dashboardVm.LoadDashboardDataAsync);
                }

                var deviceInfo = await _sshService.GetDeviceInfoAsync();
                DeviceInfo dev = new()
                {
                    Model = deviceInfo["Model"]
                };
                DeploymentResult res = new() { Success = true };
                if (!installStatus.IsInstalled)
                {
                    _logger.LogDebug("No installation found on device. Deploying fresh daemon...");
                    res = await _deploymentService.DeployAsync(dev);
                    await _sshService.RestartServiceAsync();
                }
                else if (installStatus.InstalledVersion != currentAppVersion)
                {
                    _logger.LogDebug("Tablet daemon is outdated ({Installed} vs {Current}). Triggering update...",
                        installStatus.InstalledVersion, currentAppVersion);
                    OnDeploymentProgress(this, new DeploymentProgressEventArgs()
                    {
                        Stage = DeploymentStage.Checking,
                        Message = "Old version detected. Updating...",
                        Progress = 0
                    });

                    res = await _deploymentService.UpdateAsync(dev);
                    await _sshService.RestartServiceAsync();

                    // Finalize configuration by pushing endpoints via HTTP
                    await _configProvider.RegisterEndpointAsync();
                }
                else
                {
                    OnDeploymentProgress(this, new DeploymentProgressEventArgs()
                    {
                        Stage = DeploymentStage.Complete,
                        Message = "Version is up to date!",
                        Progress = 1
                    });
                }
                if (res.Success && OneNoteStatusText.Contains("Signed in as"))
                {
                    // Ensure endpoints are registered if everything else is fine
                    await _configProvider.RegisterEndpointAsync();

                    OnPropertyChanged(nameof(CanCompleteSetup));
                    CompleteSetupCommand.NotifyCanExecuteChanged();
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
                _logger.LogDebug("Successfully authenticated with OneNote as {User}", result.UserName);
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

        if (DeployStage == DeploymentStage.Complete)
        {
            OnPropertyChanged(nameof(CanCompleteSetup));
            CompleteSetupCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanCompleteSetup))]
    private async Task CompleteSetupAsync()
    {
        // In test mode, bypass some checks
        if (AppSettings.TestingMode)
        {
            _logger.LogWarning("Completing setup in TEST MODE");
        }

        // Wait briefly to allow services to transition state
        await Task.Delay(500);

        // Switch to main interface on UI thread
        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            ShowSetupScreen = false;
            CurrentView = "Dashboard";
            CurrentViewModel = App.ServiceProvider?.GetRequiredService<DashboardViewModel>();

            // Trigger Guided Tour
            IsTourVisible = true;
            StartTour();
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

    [RelayCommand]
    private void OpenRepositoryURL()
    {
        try
        {
            var ps = new ProcessStartInfo(RepositoryURL)
            {
                UseShellExecute = true,
                Verb = "open"
            };
            Process.Start(ps);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to open URL {Url}", RepositoryURL);
        }
    }

}