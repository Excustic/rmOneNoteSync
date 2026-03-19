using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using rmOneNoteSyncApp.Models;
using Microsoft.Extensions.DependencyInjection;
using rmOneNoteSyncApp.Services.Interfaces;
using rmOneNoteSyncApp.Services;
using Microsoft.Extensions.Logging;
using System.Threading;

namespace rmOneNoteSyncApp.ViewModels;

public partial class DashboardViewModel : ViewModelBase
{
    private readonly IDatabaseService _databaseService;
    private readonly ISshService _sshService;
    private readonly ISyncServerService _syncServer;
    private readonly IDeviceDetectionService _detectionService;
    private readonly ILogger<DashboardViewModel> _logger;

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _deviceStatus = "Disconnected";

    [ObservableProperty]
    private string _deviceIp = "--";

    [ObservableProperty]
    private string _lastConnected = "Never";

    [ObservableProperty]
    private bool _isServerRunning;

    [ObservableProperty]
    private string _serverStatusText = "Stopped";

    [ObservableProperty]
    private int _totalDocuments;

    [ObservableProperty]
    private int _totalPages;

    [ObservableProperty]
    private int _syncedDocuments;

    [ObservableProperty]
    private int _pendingDocuments;

    [ObservableProperty]
    private string _lastSyncTime = "Never";

    [ObservableProperty]
    private bool _requiresManualScan;

    [ObservableProperty]
    private ObservableCollection<ActivityItem> _recentActivities = new();

    [ObservableProperty]
    private ObservableCollection<DocumentMetadata> _trackedNotebooks = new();

    private int _isLoadingDashboardData;
    private string _lastLogFileName = string.Empty;
    private long _lastLogFilePosition = 0;
    private const int MaxLogItems = 1000;
    private const int REFRESH_INTERVAL_SECONDS = 30;
    public DashboardViewModel(IDatabaseService databaseService, ISshService sshService, ISyncServerService syncServer, IDeviceDetectionService detectionService, ILogger<DashboardViewModel> logger)
    {
        _databaseService = databaseService;
        _sshService = sshService;
        _syncServer = syncServer;
        _detectionService = detectionService;
        _logger = logger;

        _sshService.OnConnectionChanged += async (sender, b) =>
        {
            if (b)
            {
                var timeStr = DateTime.Now.ToString("ddd dd MMM yyyy HH:mm");
                await _databaseService.SaveTelemetryAsync("LastConnected", timeStr);
                Avalonia.Threading.Dispatcher.UIThread.Post(() => LastConnected = timeStr);
            }
        };

        _syncServer.StatusChanged += (sender, running) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsServerRunning = running;
                ServerStatusText = running ? "Running" : "Stopped";
            });
        };

        _detectionService.DeviceConnectionChanged += (sender, e) =>
        {
            _logger.LogInformation("Device connection changed: {IsConnected}", e.IsConnected ? "Connected" : "Disconnected");
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsConnected = e.IsConnected;
                RequiresManualScan = _detectionService.RequiresManualScan;
            });
            Task.Run(LoadDashboardDataAsync);

        };

        Task.Delay(2000).ContinueWith(async _ => await LoadDashboardDataAsync());

        Task.Run(async () =>
        {
            PeriodicTimer timer = new(TimeSpan.FromSeconds(REFRESH_INTERVAL_SECONDS));
            do
            {
                await RefreshRecentActivity();
            }
            while (await timer.WaitForNextTickAsync());
        });
    }
    [RelayCommand]
    private async Task RefreshRecentActivity()
    {
        try
        {
            var logDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "rmOneNoteSyncApp", "logs");

            if (!System.IO.Directory.Exists(logDir)) return;

            var files = new System.IO.DirectoryInfo(logDir).GetFiles("app-*.log");
            var latestFile = files.OrderByDescending(f => f.LastWriteTime).FirstOrDefault();

            if (latestFile != null)
            {
                // 1. Check if the log file rolled over to a new file
                if (latestFile.FullName != _lastLogFileName)
                {
                    _lastLogFileName = latestFile.FullName;
                    _lastLogFilePosition = 0; // Reset position for a new file
                }

                using var fs = new System.IO.FileStream(latestFile.FullName, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);

                // 2. Safety check: if the file was truncated/cleared, reset our pointer
                if (fs.Length < _lastLogFilePosition)
                {
                    _lastLogFilePosition = 0;
                }

                // 3. Jump directly to where we left off last time!
                fs.Seek(_lastLogFilePosition, System.IO.SeekOrigin.Begin);

                using var reader = new System.IO.StreamReader(fs);
                var newLogs = new System.Collections.Generic.List<ActivityItem>();
                string? line;

                // 4. Read only the new lines
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    int bracketStart = line.IndexOf(" [");
                    int bracketEnd = bracketStart >= 0 ? line.IndexOf("] ", bracketStart + 1) : -1;

                    if (bracketStart > 0 && bracketEnd > bracketStart)
                    {
                        var timeStr = line[..bracketStart];
                        var level = line[(bracketStart + 2)..bracketEnd];
                        var msg = line[(bracketEnd + 2)..].Trim();

                        if (DateTime.TryParse(timeStr, out var time) && !level.Equals("DBG"))
                        {
                            newLogs.Add(new ActivityItem
                            {
                                Timestamp = time,
                                DocumentName = "System",
                                Action = level,
                                Status = msg.Length > 200 ? string.Concat(msg.AsSpan(0, 197), "...") : msg
                            });
                        }
                    }
                }

                // 5. Save our new position for the next run
                _lastLogFilePosition = fs.Position;

                // 6. Update the ObservableCollection on the UI Thread
                if (newLogs.Count > 0)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        // Ensure it's initialized
                        RecentActivities ??= [];

                        // newLogs contains items in chronological order (oldest -> newest).
                        // By inserting each one at Index 0, the very last item read (the absolute newest) 
                        // will end up at the very top of the list.
                        foreach (var log in newLogs)
                        {
                            RecentActivities.Insert(0, log);

                            // Enforce the FIFO limit: remove the oldest item from the bottom
                            if (RecentActivities.Count > MaxLogItems)
                            {
                                RecentActivities.RemoveAt(RecentActivities.Count - 1);
                            }
                        }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error refreshing recent activity");
        }
    }
    public async Task LoadDashboardDataAsync()
    {
        if (Interlocked.CompareExchange(ref _isLoadingDashboardData, 1, 0) == 1)
            return;

        try
        {
            _logger.LogDebug("Loading dashboard data...");
            // Fetch properties on background thread
            var config = await _databaseService.GetConfigurationAsync();
            var documents = await _databaseService.GetAllDocumentsAsync();
            var pendingPages = await _databaseService.GetPendingPagesAsync();
            var uploadedPages = await _databaseService.GetPagesByStatusAsync(SyncStatus.Uploaded);
            var lastConnected = await _databaseService.GetTelemetryAsync("LastConnected");

            // Dispatch properties to UI Thread
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _logger.LogDebug("IsConnected inside UI refresh: {IsConnected}, and local IsConnected: {localIsConnected}", _detectionService.IsConnected, IsConnected);

                RequiresManualScan = _detectionService.RequiresManualScan;
                DeviceStatus = IsConnected ? "Connected" : "Disconnected";
                if (lastConnected != null)
                {
                    LastConnected = lastConnected;
                }

                DeviceIp = _detectionService.IsConnected ? _detectionService.CurrentDevice?.IpAddress ?? "--" : config != null ? config.DeviceIp : "--";

                TotalDocuments = config?.SyncFiles.Count ?? 0;

                TotalPages = documents.Sum(doc => doc.Pages.Count);

                PendingDocuments = pendingPages.Count;
                SyncedDocuments = uploadedPages.Count;

                var lastUpload = uploadedPages.OrderByDescending(p => p.LastSyncTime).FirstOrDefault();
                if (lastUpload != null && lastUpload.LastSyncTime.HasValue)
                {
                    LastSyncTime = "Last: " + lastUpload.LastSyncTime.Value.ToString("dd MMM yyyy, HH:mm");
                }
                else
                {
                    LastSyncTime = "Last: Never";
                }

                if (config != null)
                {
                    var whitelistedNotebooks = documents
                        .Where(d => d.Type == "CollectionType" && config.SyncFiles.Contains(d.DocumentId))
                        .OrderByDescending(d => d.LastModified)
                        .ToList();

                    TrackedNotebooks = [.. whitelistedNotebooks];
                }
                else
                {
                    TrackedNotebooks = [];
                }
            });

            // Auto-Start Sync Server upon device connection
            if (!_syncServer.IsRunning)
            {
                await _syncServer.StartAsync();
            }

            // Dispatch properties to UI Thread
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                // Sync Server Status
                IsServerRunning = _syncServer.IsRunning;
                ServerStatusText = _syncServer.IsRunning ? "Running" : "Stopped";
            });
        }
        finally
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref _isLoadingDashboardData, 0);
            });
        }
    }

    [RelayCommand]
    private static async Task SyncNowAsync()
    {
        try
        {
            var syncStatusVm = App.ServiceProvider?.GetService<SyncStatusViewModel>();
            if (syncStatusVm != null && !syncStatusVm.IsSyncing)
            {
                await syncStatusVm.ProcessQueueCommand.ExecuteAsync(null);
            }
        }
        catch (Exception)
        {
            // Ignore DI or execution failures
        }
    }

    [RelayCommand]
    private async Task ScanForDeviceAsync()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            DeviceStatus = "Scanning...";
            RequiresManualScan = false;
        });

        _detectionService.ResetWifiScanAttempts();
        await _detectionService.CheckConnectionAsync();
    }

    [RelayCommand]
    private async Task StartServerAsync()
    {
        if (!_syncServer.IsRunning)
        {
            await _syncServer.StartAsync();
        }
    }

    [RelayCommand]
    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;

        try
        {
            var ps = new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
                Verb = "open"
            };
            System.Diagnostics.Process.Start(ps);
        }
        catch (Exception)
        {
        }
    }

    [RelayCommand]
    private static async Task CopyUrlAsync(string? url)
    {
        if (string.IsNullOrEmpty(url)) return;

        try
        {
            var topLevel = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
                ? desktop.MainWindow
                : null;

            var clipboard = topLevel?.Clipboard;
            if (clipboard != null)
            {
                await clipboard.SetTextAsync(url);
            }
        }
        catch (Exception)
        {
        }
    }
}

public class ActivityItem
{
    public DateTime Timestamp { get; set; }
    public string DocumentName { get; set; } = "";
    public string Action { get; set; } = "";
    public string Status { get; set; } = "";
    public string FormattedLogContent => Status;
}