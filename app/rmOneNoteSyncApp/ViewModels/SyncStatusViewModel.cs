using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using rmOneNoteSyncApp.Models;
using rmOneNoteSyncApp.Services;
using rmOneNoteSyncApp.Services.Interfaces;

namespace rmOneNoteSyncApp.ViewModels;

public partial class SyncStatusViewModel : ViewModelBase
{
    private readonly ILogger<SyncStatusViewModel>? _logger;
    private readonly IDatabaseService _databaseService;
    private readonly ISyncServerService _syncServer;
    private readonly IOneNoteClient _oneNoteClient;
    private readonly ISyncService _syncService;

    [ObservableProperty]
    private ObservableCollection<SyncItem> _syncItems = new();

    [ObservableProperty]
    private ObservableCollection<SyncQueueItem> _queueItems = new();

    [ObservableProperty]
    private bool _isServerRunning;

    [ObservableProperty]
    private string _serverStatus = "Server stopped";

    [ObservableProperty]
    private int _totalReceivedPages;

    [ObservableProperty]
    private int _totalReceivedNotebooks;

    [ObservableProperty]
    private int _totalUploadedPages;

    [ObservableProperty]
    private int _totalUploadedNotebooks;

    [ObservableProperty]
    private int _totalPendingPages;

    [ObservableProperty]
    private int _totalPendingNotebooks;

    [ObservableProperty]
    private int _totalFailedPages;

    [ObservableProperty]
    private int _totalFailedNotebooks;

    [ObservableProperty]
    private bool _isSyncing;

    [ObservableProperty]
    private string _syncProgress = "";

    public SyncStatusViewModel(
        IDatabaseService databaseService,
        ISyncServerService syncServer,
        IOneNoteClient oneNoteClient,
        ISyncService syncService)
    {
        _databaseService = databaseService;
        _syncServer = syncServer;
        _oneNoteClient = oneNoteClient;
        _syncService = syncService;

        try
        {
            _logger = App.ServiceProvider?.GetService<ILogger<SyncStatusViewModel>>();
        }
        catch { }

        // Subscribe to server events
        _syncServer.FileReceived += OnFileReceived;
        _syncServer.StatusChanged += (sender, running) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                IsServerRunning = running;
                ServerStatus = running ? "Server running on port 8080" : "Server stopped";
            });
        };

        // Subscribe to sync events
        _syncService.SyncProgress += OnSyncProgress;
        _syncService.PageSyncCompleted += OnPageSyncCompleted;
        _syncService.SyncCompleted += OnSyncCompleted;

        // Initialize server state
        IsServerRunning = _syncServer.IsRunning;
        ServerStatus = IsServerRunning ? "Server running on port 8080" : "Server stopped";

        // Load initial data
        Task.Run(LoadSyncStatusAsync);

        // Load initial data
        Task.Run(LoadSyncStatusAsync);
    }

    private async Task LoadSyncStatusAsync()
    {
        try
        {
            // Load overall stats
            var pendingPages = await _databaseService.GetPagesByStatusAsync(SyncStatus.Pending);
            var uploadedPages = await _databaseService.GetPagesByStatusAsync(SyncStatus.Uploaded);
            var failedPages = await _databaseService.GetPagesByStatusAsync(SyncStatus.Failed);
            var skippedPages = await _databaseService.GetPagesByStatusAsync(SyncStatus.Skipped);

            _logger?.LogDebug("Loading recent files. Found {Pending} pending, {Uploaded} uploaded, {Failed} failed, {Skipped} skipped.",
                pendingPages.Count, uploadedPages.Count, failedPages.Count, skippedPages.Count);

            // Load actual recent files regardless of status
            var recentPages = await _databaseService.GetRecentPagesAsync(50);
            _logger?.LogDebug("Loaded {Count} recent pages from DB.", recentPages.Count);

            // Load Notebook stats by grabbing tracked Collections mapped to Sync Configuration directly!
            var config = await _databaseService.GetConfigurationAsync();
            var whitelistedIds = config?.SyncFiles ?? new List<string>();
            var allDocs = await _databaseService.GetAllDocumentsAsync();
            var rootBooks = allDocs.Count(d => whitelistedIds.Contains(d.DocumentId));

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                try
                {
                    var newItems = new List<SyncItem>();
                    foreach (var page in recentPages)
                    {
                        newItems.Add(MapToSyncItem(page));
                    }
                    SyncItems = new ObservableCollection<SyncItem>(newItems);
                    SortSyncItems();

                }
                catch (Exception uiEx)
                {
                    _logger?.LogError(uiEx, "Failed to reload recent files");
                }

                // Update UI Counter logic separating notebooks from pages explicitly
                TotalPendingPages = pendingPages.Count;
                TotalPendingNotebooks = pendingPages.Select(p => p.DocumentId).Distinct().Count();

                TotalUploadedPages = uploadedPages.Count;
                TotalUploadedNotebooks = uploadedPages.Select(p => p.DocumentId).Distinct().Count();

                TotalFailedPages = failedPages.Count;
                TotalFailedNotebooks = failedPages.Select(p => p.DocumentId).Distinct().Count();

                TotalReceivedPages = TotalPendingPages + TotalUploadedPages + TotalFailedPages + skippedPages.Count;
                TotalReceivedNotebooks = rootBooks;
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load sync status");
        }
    }

    private SyncItem MapToSyncItem(PageMetadata page)
    {
        var (notebook, section, pageName) = ParseVirtualPath(page.VirtualPath);

        return new SyncItem
        {
            DocumentId = page.DocumentId,
            PageId = page.PageId,
            FileName = Path.GetFileName(page.LocalFilePath),
            VirtualPath = page.VirtualPath,
            Notebook = notebook,
            Section = section,
            PageName = pageName,
            FileSize = FormatFileSize(page.FileSizeBytes),
            ReceivedTime = page.LastModified.ToLocalTime(),
            Status = page.Status,
            LastError = page.LastError,
            OneNoteUrl = page.OneNotePageUrl
        };
    }

    private async void OnFileReceived(object? sender, FileReceivedEventArgs e)
    {
        var config = await _databaseService.GetConfigurationAsync();
        var whitelist = config?.SyncFiles ?? new List<string>();

        if (!whitelist.Contains(e.DocumentId))
        {
            return; // Ignore non-whitelisted documents entirely.
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var (notebook, section, pageName) = ParseVirtualPath(e.VirtualPath);

            var item = new SyncItem
            {
                DocumentId = e.DocumentId,
                PageId = e.PageId,
                FileName = Path.GetFileName(e.LocalPath),
                VirtualPath = e.VirtualPath,
                Notebook = notebook,
                Section = section,
                PageName = pageName,
                FileSize = FormatFileSize(e.FileSize),
                ReceivedTime = e.ReceivedAt.ToLocalTime(),
                Status = SyncStatus.Pending
            };

            // Add to list and sort
            SyncItems.Add(item);
            SortSyncItems();

            // Update counters based on the new logic
            TotalReceivedPages++;
            TotalPendingPages++;
            // Note: TotalReceivedNotebooks doesn't reliably increment per file event since we count distincts, 
            // relying on the next load tick to reconstruct the accurate graph size.

            _logger?.LogInformation("File received: {Path}", e.VirtualPath);
        });
    }

    private void OnSyncProgress(object? sender, SyncProgressEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            SyncProgress = e.Message;

            // Mark items as 'InProgress' based on progress message if applicable
            if (!string.IsNullOrEmpty(e.CurrentDocumentId) && !string.IsNullOrEmpty(e.CurrentPageId))
            {
                foreach (var i in SyncItems)
                {
                    if (i.DocumentId == e.CurrentDocumentId && i.PageId == e.CurrentPageId)
                    {
                        if (i.Status == SyncStatus.Pending)
                        {
                            i.Status = SyncStatus.InProgress;
                        }

                        if (e.TotalSteps > 0)
                        {
                            i.UploadMaxProgress = e.TotalSteps;
                            i.UploadProgress = e.CurrentStep;
                        }

                        i.UpdateStatusDisplay();
                    }
                    else if (i.Status == SyncStatus.InProgress)
                    {
                        i.Status = SyncStatus.Pending;
                    }
                }
                SortSyncItems();
            }
        });
    }

    private void OnPageSyncCompleted(object? sender, PageSyncCompletedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            var item = SyncItems.FirstOrDefault(i => i.PageId == e.Page.PageId && i.DocumentId == e.Page.DocumentId);
            if (item != null)
            {
                item.Status = e.Success ? SyncStatus.Uploaded : SyncStatus.Failed;
                item.LastError = e.ErrorMessage;
                item.OneNoteUrl = e.OneNoteUrl;
            }
            SortSyncItems();

            // Refresh counts
            if (e.Success)
            {
                TotalPendingPages--;
                TotalUploadedPages++;
            }
            else
            {
                TotalPendingPages--;
                TotalFailedPages++;
            }
        });
    }

    private void OnSyncCompleted(object? sender, SyncCompletedEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (e.Success)
            {
                SyncProgress = $"Sync completed: {e.ItemsSynced} items uploaded";
            }
            else
            {
                SyncProgress = $"Sync failed: {e.ErrorMessage}";
            }

            // Reload items
            Task.Run(LoadSyncStatusAsync);
        });
    }

    [RelayCommand]
    private async Task StartServerAsync()
    {
        try
        {
            if (!_syncServer.IsRunning)
            {
                await _syncServer.StartAsync();
                IsServerRunning = true;
                ServerStatus = $"Server running on port 8080";
                _logger?.LogInformation("Sync server started");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to start server");
            ServerStatus = $"Server error: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task StopServerAsync()
    {
        try
        {
            if (_syncServer.IsRunning)
            {
                await _syncServer.StopAsync();
                IsServerRunning = false;
                ServerStatus = "Server stopped";
                _logger?.LogInformation("Sync server stopped");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to stop server");
        }
    }

    private CancellationTokenSource? _manualSyncCts;

    [RelayCommand]
    private async Task ProcessQueueAsync()
    {
        if (_syncService.IsSyncing)
        {
            _logger?.LogWarning("Cannot process queue, sync is already running");
            return;
        }

        _manualSyncCts = new CancellationTokenSource();

        try
        {
            // Allow manual user trigger through the SyncService orchestrator
            await _syncService.SyncAllAsync(_manualSyncCts.Token);
        }
        catch (OperationCanceledException)
        {
            _logger?.LogInformation("Manual sync was cancelled by the user");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to start manual sync");
        }
        finally
        {
            _manualSyncCts?.Dispose();
            _manualSyncCts = null;
        }
    }

    [RelayCommand]
    private void StopSync()
    {
        if (_manualSyncCts != null && !_manualSyncCts.IsCancellationRequested)
        {
            _manualSyncCts.Cancel();
            _logger?.LogInformation("Sent cancellation request to manual sync");
            // Mark items explicitly? SyncAllAsync will break out of the loop natively.
        }
    }

    [RelayCommand]
    private async Task RetryFailedAsync()
    {
        // Reset failed items to pending
        var failedPages = await _databaseService.GetPagesByStatusAsync(SyncStatus.Failed);

        foreach (var page in failedPages)
        {
            await _databaseService.UpdatePageStatusAsync(
                page.DocumentId,
                page.PageId,
                SyncStatus.Pending);
        }

        // Reload entirely from database to correctly display new Recents state
        await LoadSyncStatusAsync();

        // Start processing if we reset anything
        if (failedPages.Count > 0)
        {
            await ProcessQueueAsync();
        }
    }

    [RelayCommand]
    private async Task ClearCompletedAsync()
    {
        await LoadSyncStatusAsync();
    }

    [RelayCommand]
    private async Task CancelSyncAsync(SyncItem? item)
    {
        if (item == null) return;
        
        await _syncService.CancelSyncItemAsync(item.DocumentId, item.PageId);
        item.Status = SyncStatus.Skipped;
        
        // Remove from list if it's currently pending/inflight
        var existing = SyncItems.FirstOrDefault(i => i.DocumentId == item.DocumentId && i.PageId == item.PageId);
        if (existing != null)
        {
            SyncItems.Remove(existing);
        }
        
        SortSyncItems();
        
        // Refresh counts
        await LoadSyncStatusAsync();
    }

    [RelayCommand]
    private void OpenUrl(string? url)
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
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to open URL {Url}", url);
        }
    }

    [RelayCommand]
    private async Task CopyUrlAsync(string? url)
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
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to copy URL {Url}", url);
        }
    }

    private (string notebook, string section, string page) ParseVirtualPath(string virtualPath)
    {
        var parts = virtualPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
            return ("rm_Uncategorized", "Default", "Untitled");

        if (parts.Length == 1)
            return ("rm_Uncategorized", "Default", parts[0]);

        if (parts.Length == 2)
            return ($"rm_{parts[0]}", parts[0], parts[1]);

        var notebookParts = parts.Take(parts.Length - 2).ToList();
        var section = parts[parts.Length - 2];
        var page = parts[parts.Length - 1];

        var notebook = "rm_" + string.Join("_", notebookParts);

        return (notebook, section, page);
    }

    private string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        int order = 0;
        double size = bytes;

        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return $"{size:0.##} {sizes[order]}";
    }

    private void SortSyncItems()
    {
        var sorted = SyncItems.OrderBy(i => i.Status switch
        {
            SyncStatus.InProgress => 0,
            SyncStatus.Pending => 1,
            SyncStatus.Uploaded => 2,
            SyncStatus.Failed => 3,
            SyncStatus.Skipped => 4,
            _ => 5
        }).ThenByDescending(i => i.ReceivedTime).ToList();

        SyncItems = new ObservableCollection<SyncItem>(sorted);
    }
}

public class SyncItem : ObservableObject
{
    public string DocumentId { get; set; } = "";
    public string PageId { get; set; } = "";
    public string FileName { get; set; } = "";
    public string VirtualPath { get; set; } = "";
    public string Notebook { get; set; } = "";
    public string Section { get; set; } = "";
    public string PageName { get; set; } = "";
    public string FileSize { get; set; } = "";
    public DateTime ReceivedTime { get; set; }

    private SyncStatus _status;
    public SyncStatus Status
    {
        get => _status;
        set
        {
            SetProperty(ref _status, value);
            OnPropertyChanged(nameof(StatusDisplay));
            OnPropertyChanged(nameof(IsUploading));
            OnPropertyChanged(nameof(IsNotUploading));
        }
    }

    public bool IsUploading => Status == SyncStatus.InProgress;
    public bool IsNotUploading => Status != SyncStatus.InProgress;

    private double _uploadProgress;
    public double UploadProgress
    {
        get => _uploadProgress;
        set => SetProperty(ref _uploadProgress, value);
    }

    private double _uploadMaxProgress = 1;
    public double UploadMaxProgress
    {
        get => _uploadMaxProgress;
        set => SetProperty(ref _uploadMaxProgress, value);
    }

    public string? LastError { get; set; }
    public string? OneNoteUrl { get; set; }

    public void UpdateStatusDisplay()
    {
        OnPropertyChanged(nameof(StatusDisplay));
    }

    public string StatusDisplay => Status switch
    {
        SyncStatus.Pending => "⏳ Pending",
        SyncStatus.InProgress => "🔄 Uploading...",
        SyncStatus.Uploaded => "✅ Uploaded",
        SyncStatus.Failed => "❌ Failed",
        SyncStatus.Skipped => "⏭️ Skipped",
        _ => "Unknown"
    };
}

public class SyncQueueItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public double Progress { get; set; }
}