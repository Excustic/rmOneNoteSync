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
    private readonly IConfigurationProviderService _configProvider;
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

    public ManualSyncViewModel(Window dialog, DocumentMetadata document, IDeviceDetectionService deviceDetectionService, IDatabaseService databaseService, IConfigurationProviderService configProvider, ILogger? logger)
    {
        _dialog = dialog;
        _document = document;
        _deviceDetectionService = deviceDetectionService;
        _databaseService = databaseService;
        _configProvider = configProvider;
        _logger = logger;

        NotebookName = document.NotebookName;
        Task.Run(LoadMetadataAsync);
    }

    private async Task LoadMetadataAsync()
    {
        try
        {
            var deviceIp = _deviceDetectionService.CurrentDevice?.IpAddress ?? AppSettings.DefaultDeviceIp;
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            var response = await client.GetStringAsync($"http://{deviceIp}:8000/metadata?id={_document.DocumentId}");
            var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;

            var pages = new List<ManualSyncPageItem>();
            if (root.TryGetProperty("documents", out var docsArr))
            {
                var uploadedPages = await _databaseService.GetPagesByStatusAsync(SyncStatus.Uploaded);
                var uploadedDict = uploadedPages.ToDictionary(p => $"{p.DocumentId}_{p.PageId}", p => p.LastSyncTime);

                foreach (var docNode in docsArr.EnumerateArray())
                {
                    if (!docNode.TryGetProperty("id", out var idProp) || !docNode.TryGetProperty("name", out var nameProp)) continue;
                    var docId = idProp.GetString() ?? "";
                    var secName = nameProp.GetString() ?? "Unknown";

                    if (docNode.TryGetProperty("pages", out var pagesArr))
                    {
                        int pageNum = 1;
                        foreach (var page in pagesArr.EnumerateArray())
                        {
                            var pageId = page.GetString();
                            if (string.IsNullOrEmpty(pageId)) continue;

                            var isUploaded = uploadedDict.TryGetValue($"{docId}_{pageId}", out var lastUploaded);

                            var item = new ManualSyncPageItem
                            {
                                PageId = pageId,
                                DocumentId = docId,
                                SectionName = secName,
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
            var selectedPages = Pages.Where(p => p.IsSelected).ToList();
            if (selectedPages.Count == 0) return;

            IsLoading = true;
            LoadingMessage = "Sending sync request to device...";

            //Add to SyncFiles in config
            var config = await _databaseService.GetConfigurationAsync();
            if (config != null)
            {
                // Add only unsynced selected pages to SyncFiles
                config.SyncFiles.AddRange(selectedPages.Where(p => !config.SyncFiles.Contains(p.DocumentId)).Select(p => p.DocumentId));
                _logger?.LogDebug("Added {Count} new files to SyncFiles", selectedPages.Count);
                await _databaseService.SaveConfigurationAsync(config);
                var success = await _configProvider.UpdateDeviceConfigurationAsync();
                if (!success)
                {
                    LoadingMessage = "Failed to update device configuration. Please ensure the device is connected and try again.";
                    return;
                }
            }

            var deviceIp = _deviceDetectionService.CurrentDevice?.IpAddress ?? AppSettings.DefaultDeviceIp;
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

            var groups = selectedPages.GroupBy(p => p.DocumentId);
            foreach (var group in groups)
            {
                var payload = new
                {
                    document_id = group.Key,
                    pages = group.Select(p => p.PageId).ToList()
                };

                var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                var response = await client.PostAsync($"http://{deviceIp}:8000/sync", content);

                response.EnsureSuccessStatusCode();
            }

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
    public string DocumentId { get; set; } = "";
    public string SectionName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool IsOnline { get; set; }

    public string StatusText => IsOnline ? "Online" : "Offline";
    public string StatusColor => IsOnline ? "#10b981" : "#64748b";

    public string LastUploadedStr { get; set; } = "";

    [ObservableProperty]
    private bool _isSelected;
}
