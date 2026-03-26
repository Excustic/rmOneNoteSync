using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using rmOneNoteSyncApp.Models;
using rmOneNoteSyncApp.Services.Interfaces;
using rmOneNoteSyncApp.Services;

namespace rmOneNoteSyncApp.ViewModels;

public partial class ManualSyncViewModel : ViewModelBase
{
    private readonly Window _dialog;
    private readonly DocumentMetadata _document;
    private readonly IDeviceDetectionService _deviceDetectionService;
    private readonly IDatabaseService _databaseService;
    private readonly ILogger? _logger;

    [ObservableProperty]
    private string _notebookName = "";

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private string _loadingMessage = "Fetching notebook metadata from device...";

    [ObservableProperty]
    private int _selectedCount = 0;

    [ObservableProperty]
    private ObservableCollection<ManualSyncPageItem> _pages = new();

    private bool _isUpdatingSelection = false;

    public ManualSyncViewModel(Window dialog, DocumentMetadata document, IDeviceDetectionService deviceDetectionService, IDatabaseService databaseService, ILogger? logger)
    {
        _dialog = dialog;
        _document = document;
        _deviceDetectionService = deviceDetectionService;
        _databaseService = databaseService;
        _logger = logger;

        NotebookName = document.NotebookName;
        Task.Run(LoadMetadataAsync);
    }

    private async Task LoadMetadataAsync()
    {
        try
        {
            var deviceIp = _deviceDetectionService.CurrentDevice?.IpAddress ?? "10.11.99.1";
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            
            var response = await client.GetStringAsync($"http://{deviceIp}:8000/metadata?id={_document.DocumentId}");
            var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            
            var pages = new List<ManualSyncPageItem>();
            if (root.TryGetProperty("pages", out var pagesArr))
            {
                var uploadedPages = await _databaseService.GetPagesByStatusAsync(SyncStatus.Uploaded);
                var uploadedDict = uploadedPages.Where(p => p.DocumentId == _document.DocumentId)
                                                .ToDictionary(p => p.PageId, p => p.LastSyncTime);

                int pageNum = 1;
                foreach (var page in pagesArr.EnumerateArray())
                {
                    var pageId = page.GetString();
                    if (string.IsNullOrEmpty(pageId)) continue;
                    
                    var isUploaded = uploadedDict.TryGetValue(pageId, out var lastUploaded);
                    
                    var item = new ManualSyncPageItem
                    {
                        PageId = pageId,
                        DisplayName = $"Page {pageNum++}",
                        IsOnline = isUploaded,
                        LastUploadedStr = isUploaded && lastUploaded.HasValue ? lastUploaded.Value.ToString("dd MMM yyyy, HH:mm") : "Offline",
                        IsSelected = !isUploaded // Pre-select only offline pages by default
                    };
                    
                    item.PropertyChanged += (s, e) =>
                    {
                        if (e.PropertyName == nameof(ManualSyncPageItem.IsSelected))
                            UpdateSelectedCount();
                    };
                    
                    pages.Add(item);
                }
            }
            
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                Pages = new ObservableCollection<ManualSyncPageItem>(pages);
                UpdateSelectedCount();
                IsLoading = false;
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load metadata for manual sync from device");
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                LoadingMessage = "Failed to communicate with device. Please ensure it is connected and awake.";
                // We leave IsLoading = true to show the error message, but you might want an Error state.
                IsLoading = false; 
            });
        }
    }

    private void UpdateSelectedCount()
    {
        if (_isUpdatingSelection) return;
        SelectedCount = Pages.Count(p => p.IsSelected);
    }

    [RelayCommand]
    private void SelectAll()
    {
        _isUpdatingSelection = true;
        foreach (var p in Pages) p.IsSelected = true;
        _isUpdatingSelection = false;
        UpdateSelectedCount();
    }

    [RelayCommand]
    private void SelectNone()
    {
        _isUpdatingSelection = true;
        foreach (var p in Pages) p.IsSelected = false;
        _isUpdatingSelection = false;
        UpdateSelectedCount();
    }

    [RelayCommand]
    private async Task SyncSelectedAsync()
    {
        try
        {
            var selectedPages = Pages.Where(p => p.IsSelected).Select(p => p.PageId).ToList();
            if (selectedPages.Count == 0) return;

            IsLoading = true;
            LoadingMessage = "Sending sync request to device...";

            var payload = new
            {
                document_id = _document.DocumentId,
                pages = selectedPages
            };

            var deviceIp = _deviceDetectionService.CurrentDevice?.IpAddress ?? "10.11.99.1";
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await client.PostAsync($"http://{deviceIp}:8000/sync", content);
            
            response.EnsureSuccessStatusCode();
            
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                _dialog.Close(true);
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to send sync request to device");
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                LoadingMessage = "Failed to sync. Ensure device is reachable.";
            });
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _dialog.Close(false);
    }
}

public partial class ManualSyncPageItem : ObservableObject
{
    public string PageId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool IsOnline { get; set; }
    
    public string StatusText => IsOnline ? "Online" : "Offline";
    public string StatusColor => IsOnline ? "#10b981" : "#64748b";
    
    public string LastUploadedStr { get; set; } = "";
    
    [ObservableProperty]
    private bool _isSelected;
}
