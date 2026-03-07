using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using rmOneNoteSyncApp.Models;
using Microsoft.Extensions.DependencyInjection;
using rmOneNoteSyncApp.Services.Interfaces;

namespace rmOneNoteSyncApp.ViewModels;

public partial class DashboardViewModel : ViewModelBase
{
    private readonly IDatabaseService _databaseService;
    private readonly ISshService _sshService;
    private readonly ISyncServerService _syncServer;
    
    [ObservableProperty]
    private bool _isConnected;
    
    [ObservableProperty]
    private string _deviceStatus = "Disconnected";
    
    [ObservableProperty]
    private string _deviceIp = "--";
    
    [ObservableProperty]
    private string _lastConnected = "Never";
    
    [ObservableProperty]
    private string _wifiStatus = "Unknown";
    
    [ObservableProperty]
    private string _storageInfo = "-- / --";
    
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
    private ObservableCollection<ActivityItem> _recentActivities = new();
    
    public DashboardViewModel(IDatabaseService databaseService, ISshService sshService, ISyncServerService syncServer)
    {
        _databaseService = databaseService;
        _sshService = sshService;
        _syncServer = syncServer;
        _sshService.OnConnectionChanged += async(sender, b) => await LoadDashboardDataAsync(); 
        // Load initial data
        Task.Run(LoadDashboardDataAsync);
    }
    
    private async Task LoadDashboardDataAsync()
    {
        // Load statistics
        var config = await _databaseService.GetConfigurationAsync();
        if (config != null)
        {
            TotalDocuments = config.SyncFiles.Count;
            DeviceIp = config.DeviceIp;
        }
        else
        {
            TotalDocuments = 0;
            DeviceIp = "--";
        }
        
        var documents = await _databaseService.GetAllDocumentsAsync();
        TotalPages = 0;
        foreach (var doc in documents)
        {
            TotalPages += doc.Pages.Count;
        }
        
        // Count synced vs pending
        var pendingPages = await _databaseService.GetPendingPagesAsync();
        PendingDocuments = pendingPages.Count;
        
        var uploadedPages = await _databaseService.GetPagesByStatusAsync(SyncStatus.Uploaded);
        SyncedDocuments = uploadedPages.Count;
        
        // Load recent activity directly from Serilog text outputs
        RecentActivities.Clear();
        try
        {
            var logDir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "rmOneNoteSyncApp", "logs");
            
            if (System.IO.Directory.Exists(logDir))
            {
                var files = new System.IO.DirectoryInfo(logDir).GetFiles("app-*.log");
                var latestFile = System.Linq.Enumerable.FirstOrDefault(System.Linq.Enumerable.OrderByDescending(files, f => f.LastWriteTime));
                
                if (latestFile != null)
                {
                    // Stream with ReadWrite to avoid locks by the active Serilog provider
                    using var fs = new System.IO.FileStream(latestFile.FullName, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
                    using var reader = new System.IO.StreamReader(fs);
                    
                    var allLines = (await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    var lastLines = System.Linq.Enumerable.ToList(System.Linq.Enumerable.Reverse(System.Linq.Enumerable.Take(System.Linq.Enumerable.Reverse(allLines), 15)));
                    
                    foreach (var line in lastLines)
                    {
                        // Example: "[23:42:50 INF] Data initialized"
                        if (line.Length > 14 && line.StartsWith("["))
                        {
                            var timeStr = line.Substring(1, 8);
                            var level = line.Substring(10, 3);
                            var msg = line.Substring(15).Trim();
                            
                            if (DateTime.TryParseExact(timeStr, "HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out var time))
                            {
                                RecentActivities.Add(new ActivityItem
                                {
                                    Timestamp = DateTime.Today.Add(time.TimeOfDay),
                                    DocumentName = "System",
                                    Action = level,
                                    Status = msg.Length > 60 ? msg.Substring(0, 57) + "..." : msg
                                });
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore parse failures for logs
        }
        
        // Check device connection
        if (_sshService.IsConnected)
        {
            IsConnected = true;
            DeviceStatus = "Connected";
            
            // Auto-Start Sync Server upon device connection
            if (!_syncServer.IsRunning)
            {
                await _syncServer.StartAsync();
            }
            
            try
            {
                var deviceInfo = await _sshService.GetDeviceInfoAsync();
                if (deviceInfo.TryGetValue("StorageUsed", out var used) && 
                    deviceInfo.TryGetValue("StorageAvailable", out var avail))
                {
                    StorageInfo = $"{used} / {avail}";
                }
            }
            catch
            {
                // Ignore errors
            }
            
            // Sync Server Status
            IsServerRunning = _syncServer.IsRunning;
            ServerStatusText = _syncServer.IsRunning ? "Running" : "Stopped";
        }
    }
    
    [RelayCommand]
    private async Task SyncNowAsync()
    {
        try
        {
            var syncStatusVm = App.ServiceProvider?.GetService<SyncStatusViewModel>();
            if (syncStatusVm != null && !syncStatusVm.IsSyncing)
            {
                await syncStatusVm.ProcessQueueCommand.ExecuteAsync(null);
            }
            
            LastSyncTime = DateTime.Now.ToString("HH:mm:ss");
        }
        catch (Exception)
        {
            // Ignore DI or execution failures
        }
    }
    
    [RelayCommand]
    private async Task StartServerAsync()
    {
        if (!_syncServer.IsRunning)
        {
            await _syncServer.StartAsync();
            await LoadDashboardDataAsync();
        }
    }
}

public class ActivityItem
{
    public DateTime Timestamp { get; set; }
    public string DocumentName { get; set; } = "";
    public string Action { get; set; } = "";
    public string Status { get; set; } = "";
}