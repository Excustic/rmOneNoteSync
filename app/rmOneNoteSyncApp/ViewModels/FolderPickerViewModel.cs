using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using rmOneNoteSyncApp.Models;
using rmOneNoteSyncApp.Services;
using rmOneNoteSyncApp.Services.Interfaces;
using System.Net.Http;

namespace rmOneNoteSyncApp.ViewModels;

public partial class FolderPickerViewModel : ViewModelBase
{
    private readonly ISshService _sshService;
    private readonly IDatabaseService _databaseService;
    private readonly ILogger<FolderPickerViewModel>? _logger;
    private readonly IDeviceDetectionService _deviceDetectionService;
    private SyncConfiguration? _syncConfiguration;
    private readonly IConfigurationProviderService? _configProvider;

    [ObservableProperty]
    private ObservableCollection<FileNode> _folders = [];

    [ObservableProperty]
    private FileNode? _selectedFolder;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _statusMessage = "Click 'Load Folders' to fetch document structure from your device";

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _hasLoadedFolders;

    [ObservableProperty]
    private string _selectionSummary = "";
    [ObservableProperty] private string _saveButtonText = "Save Selection";
    [ObservableProperty] private string _saveButtonBg = "#2563eb";
    [ObservableProperty] private string _saveButtonFg = "#f0f4fe";

    // Can only load folders if connected and not already loading
    public bool CanLoadFolders => IsConnected && !IsLoading;

    public FolderPickerViewModel(ISshService sshService, IDatabaseService databaseService, IDeviceDetectionService deviceDetectionService, IConfigurationProviderService configurationProviderService)
    {
        _sshService = sshService;
        _databaseService = databaseService;
        _deviceDetectionService = deviceDetectionService;

        try
        {
            _logger = App.ServiceProvider?.GetService<ILogger<FolderPickerViewModel>>();
        }
        catch
        {
            // ignored
        }

        // Check connection state AFTER services are assigned
        _sshService.OnConnectionChanged += SshServiceOnOnConnectionChanged;
        SshServiceOnOnConnectionChanged(this, _sshService.IsConnected);
        _logger?.LogDebug("FolderPickerViewModel initialized - IsConnected: {IsConnected}, IsLoading: {IsLoading}",
            IsConnected, IsLoading);

        // If connected, auto-load folders
        if (IsConnected)
        {
            _logger?.LogDebug("Auto-loading folders since device is connected");
            Task.Run(async () =>
            {
                _syncConfiguration = await _databaseService.GetConfigurationAsync();
                await LoadFoldersAsync();
            });
        }
    }

    private void SshServiceOnOnConnectionChanged(object? sender, bool e)
    {
        IsConnected = e;

        // Force command re-evaluation on UI thread
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            LoadFoldersCommand.NotifyCanExecuteChanged();
        });
        StatusMessage =
            e
                ? "Device connected. Click 'Load Folders' to fetch document structure."
                : "Device disconnected. Please reconnect to load folders.";
    }


    [RelayCommand(CanExecute = nameof(CanLoadFolders))]
    private async Task LoadFoldersAsync()
    {
        try
        {
            IsLoading = true;
            StatusMessage = "Loading folder structure from device...";
            Folders.Clear();

            // Check connection first
            if (!_sshService.IsConnected)
            {
                StatusMessage = "Device not connected. Please connect first.";
                return;
            }

            _logger?.LogDebug("Loading folder structure from reMarkable via HTTP API");

            // We fallback to DefaultDeviceIp if not detected
            var deviceIp = _deviceDetectionService.CurrentDevice?.IpAddress ?? AppSettings.DefaultDeviceIp;
            var allNodes = new List<FileNode>();
            var nodeMap = new Dictionary<string, FileNode>();

            try
            {
                using var filetreeClient = new HttpClient();
                filetreeClient.Timeout = TimeSpan.FromSeconds(10);
                var responseStr = await filetreeClient.GetStringAsync($"http://{deviceIp}:8000/filetree");

                using var doc = JsonDocument.Parse(responseStr);
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                    var name = item.TryGetProperty("visibleName", out var nameProp) ? nameProp.GetString() ?? "Untitled" : "Untitled";
                    var type = item.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? "" : "";
                    var parent = item.TryGetProperty("parent", out var parentProp) ? parentProp.GetString() : null;
                    var lastModifiedStr = item.TryGetProperty("lastModified", out var lmProp) ? lmProp.GetString() : "0";

                    if (string.IsNullOrWhiteSpace(parent) || parent == "trash")
                        parent = null;

                    var node = new FileNode
                    {
                        Id = id,
                        Name = name,
                        Path = id,
                        IsFolder = type == "CollectionType",
                        ParentId = parent,
                        IsNotebook = type != "CollectionType" // We infer DocumentType as Notebook
                    };

                    allNodes.Add(node);
                    nodeMap[node.Id] = node;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to fetch filetree from HTTP API on {DeviceIp}. Check if daemon is running.", deviceIp);
                StatusMessage = "Failed to fetch file tree via HTTP. Is rm-daemon running?";
                return;
            }

            // Build hierarchy FIRST
            var rootNodes = BuildHierarchy(allNodes, nodeMap);

            // Compute Virtual Paths
            ComputeVirtualPaths(rootNodes, "");

            // Ensure we sync the DB cache with the full tree so it's fully populated offline
            var documentMetaDataNodes = allNodes.Select(n => new DocumentMetadata
            {
                DocumentId = n.Id,
                VisibleName = n.Name,
                Type = n.IsFolder ? "CollectionType" : "DocumentType",
                Parent = n.ParentId ?? "",
                VirtualPath = n.VirtualPath ?? "", // Populated via ComputeVirtualPaths
                LastModified = DateTime.UtcNow
            }).ToList();

            await _databaseService.UpsertFileTreeAsync(documentMetaDataNodes);

            var whitelist = new List<string>();
            try
            {
                using var client = new HttpClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                var response = await client.GetStringAsync($"http://{deviceIp}:8000/whitelist");
                var doc = JsonDocument.Parse(response);
                if (doc.RootElement.TryGetProperty("whitelist", out var whitelistArr))
                {
                    foreach (var item in whitelistArr.EnumerateArray())
                    {
                        var id = item.GetString();
                        if (!string.IsNullOrEmpty(id)) whitelist.Add(id);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to fetch whitelist from HTTP API on {DeviceIp}", deviceIp);
                // Fallback to local config
                if (_syncConfiguration?.SyncFiles != null)
                {
                    whitelist.AddRange(_syncConfiguration.SyncFiles);
                }
            }

            // Update UI on main thread
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                Folders.Clear();
                foreach (var node in rootNodes)
                {
                    Folders.Add(node);
                    foreach (var res in whitelist
                                 .Select(id => FindFolderNode(node, id)).OfType<FileNode>()!)
                    {
                        res.SelectionState = true;
                        ToggleSelection(res);
                    }
                }

            });

            StatusMessage = $"Loaded {allNodes.Count} items ({allNodes.Count(n => n.IsFolder)} folders, {allNodes.Count(n => !n.IsFolder)} documents)";
            _logger?.LogInformation("Successfully loaded folder structure");
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load folders");
            StatusMessage = $"Error loading folders: {ex.Message}";
        }
        finally
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsLoading = false;
                LoadFoldersCommand.NotifyCanExecuteChanged();
                OnFoldersChanged(Folders);
            });
        }
    }

    private static void ComputeVirtualPaths(IEnumerable<FileNode> nodes, string currentPath)
    {
        foreach (var node in nodes)
        {
            var nodePath = string.IsNullOrEmpty(currentPath) ? node.Name : $"{currentPath}/{node.Name}";
            node.VirtualPath = nodePath;

            if (node.Children?.Count > 0)
            {
                ComputeVirtualPaths(node.Children, nodePath);
            }
        }
    }

    private static List<FileNode> BuildHierarchy(List<FileNode> allNodes, Dictionary<string, FileNode> nodeMap)
    {
        var rootNodes = new List<FileNode>();

        foreach (var node in allNodes)
        {
            if (string.IsNullOrEmpty(node.ParentId))
            {
                // Root level item
                rootNodes.Add(node);
            }
            else if (nodeMap.TryGetValue(node.ParentId, out var parent))
            {
                // Add to parent's children
                parent.Children ??= [];
                parent.Children.Add(node);
            }
            else
            {
                // Parent not found, treat as root
                rootNodes.Add(node);
            }
        }

        // Sort folders first, then documents, alphabetically
        rootNodes.Sort((a, b) => a.IsFolder != b.IsFolder ? b.IsFolder.CompareTo(a.IsFolder) :
            string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        // Sort children recursively
        foreach (var node in allNodes.Where(n => n.Children?.Count > 0))
        {
            if (node.Children == null) continue;
            var sorted = node.Children.OrderBy(n => !n.IsFolder).ThenBy(n => n.Name).ToList();
            node.Children.Clear();
            foreach (var child in sorted)
            {
                node.Children.Add(child);
            }
        }

        return rootNodes;
    }

    [RelayCommand]
    private void ToggleSelection(FileNode? node)
    {

        if (node == null) return;

        // Toggle the selection state
        if (node.IsFolder)
        {
            // Null state is reserved for children induced changes
            node.SelectionState ??= false;
            // Apply to all children
            node.SetChildrenSelection(node.SelectionState);
        }

        // Update parent states recursively
        UpdateParentSelectionStates(node);

        // Update selection summary
        UpdateSelectionSummary();
    }

    private void UpdateParentSelectionStates(FileNode node)
    {
        if (node.ParentId == null || node is { IsFolder: true, Children: null or { Count: 0 } }) return;

        FileNode? parent = null;
        foreach (var child in Folders)
        {
            parent = FindFolderNode(child, node.ParentId);
            if (parent != null)
            {
                break;
            }
        }

        if (parent != null)
        {
            parent.UpdateSelectionFromChildren();
            // Ascend the tree to bubble state
            UpdateParentSelectionStates(parent);
        }
    }
    /// <summary>
    /// Recursively searches for the node under a given FileNode.
    /// </summary>
    /// <param name="source">The source to explore.</param>
    /// <param name="nodeId">The target node's ID.</param>
    /// <returns>A nullable <see cref="FileNode"/> object.</returns>
    private static FileNode? FindFolderNode(FileNode source, string nodeId)
    {
        if (nodeId == string.Empty) return null;
        if (source.Id == nodeId) return source;
        if (source is not { IsFolder: true, Children.Count: > 0 }) return null;
        FileNode? res = null;
        foreach (var child in source.Children)
        {
            res = FindFolderNode(child, nodeId);
            if (res != null)
                break;
        }

        return res;

    }

    private void UpdateSelectionSummary()
    {
        var selectedDocs = 0;
        var selectedFolders = 0;

        foreach (var root in Folders)
        {
            CountSelected(root, ref selectedDocs, ref selectedFolders);
        }

        if (selectedDocs == 0 && selectedFolders == 0)
        {
            SelectionSummary = "No items selected";
        }
        else
        {
            var parts = new List<string>();
            if (selectedFolders > 0)
                parts.Add($"{selectedFolders} folder{(selectedFolders != 1 ? "s" : "")}");
            if (selectedDocs > 0)
                parts.Add($"{selectedDocs} document{(selectedDocs != 1 ? "s" : "")}");
            SelectionSummary = $"Selected: {string.Join(", ", parts)}";
        }
    }

    private static void CountSelected(FileNode node, ref int docs, ref int folders)
    {
        if (node.SelectionState == true)
        {
            if (node.IsFolder)
                folders++;
            else
                docs++;
        }

        if (node.Children == null) return;
        foreach (var child in node.Children)
        {
            CountSelected(child, ref docs, ref folders);
        }
    }

    // In FolderPickerViewModel.cs - Update the SaveSelectionAsync method:

    [RelayCommand]
    private async Task SaveSelectionAsync()
    {
        try
        {
            SaveButtonText = "Saving...";
            SaveButtonBg = "#f3f4f6";
            SaveButtonFg = "#374151";
            var selectedDocIds = new List<string>();
            var selectedFolders = new List<FileNode>();

            foreach (var root in Folders)
            {
                var (docs, folders) = root.GetSelectedItems();
                selectedDocIds.AddRange(docs.Select(n => n.Id));
                selectedDocIds.AddRange(folders.Select(f => f.Id));
                selectedFolders.AddRange(folders);
            }

            _logger?.LogDebug("Saving selection: {Count} documents across {FCount} folders", selectedDocIds.Count, selectedFolders.Count);

            // Save to database
            var config = await _databaseService.GetConfigurationAsync() ?? new SyncConfiguration();
            // Distinct just in case
            config.SyncFiles = [.. selectedDocIds.Distinct()];
            await _databaseService.SaveConfigurationAsync(config);

            // Create stub DocumentMetadata so new Notebooks appear on the Dashboard instantly
            var existingDocs = await _databaseService.GetAllDocumentsAsync();
            var existingIds = existingDocs.Select(d => d.DocumentId).ToHashSet();

            // Deduplicate folders (just in case)
            var uniqueFolders = selectedFolders.GroupBy(f => f.Id).Select(g => g.First()).ToList();

            foreach (var node in uniqueFolders)
            {
                if (!existingIds.Contains(node.Id))
                {
                    await _databaseService.SaveDocumentMetadataAsync(new DocumentMetadata
                    {
                        DocumentId = node.Id,
                        VisibleName = node.Name,
                        Type = "CollectionType",
                        Parent = node.ParentId ?? "",
                        VirtualPath = node.Path,
                        LastModified = DateTime.UtcNow
                    });
                }
            }

            StatusMessage = $"Saved {selectedDocIds.Count} documents to sync";

            // IMPORTANT: Update the reMarkable configuration
            if (_sshService.IsConnected)
            {
                StatusMessage = "Updating reMarkable configuration...";

                if (_configProvider != null)
                {
                    var success = await _configProvider.UpdateDeviceConfigurationAsync();
                    if (success)
                    {
                        StatusMessage = $"✅ Configuration synced to reMarkable! {selectedDocIds.Count} documents will sync.";
                        _logger?.LogInformation("Successfully updated reMarkable configuration");

                        SaveButtonText = "✅ Selection Saved";
                        SaveButtonBg = "#22c55e";
                        SaveButtonFg = "#f0f4fe";
                    }
                    else
                    {
                        StatusMessage = "⚠️ Failed to update reMarkable. Check connection and try again.";
                        _logger?.LogWarning("Failed to update reMarkable configuration");

                        SaveButtonText = "Failed to Save";
                        SaveButtonBg = "#ef4444";
                        SaveButtonFg = "#f0f4fe";

                    }
                }
            }
            else
            {
                StatusMessage = "⚠️ reMarkable not connected. Connect device to apply configuration.";

                SaveButtonText = "⚠️ Not Connected";
                SaveButtonBg = "#ef4444";
                SaveButtonFg = "#f0f4fe";
            }

        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to save selection");
            StatusMessage = $"❌ Error: {ex.Message}";
            SaveButtonText = "Failed to Save";
            SaveButtonBg = "#ef4444";
            SaveButtonFg = "#f0f4fe";
        }
        finally
        {
            await Task.Delay(2500);
            SaveButtonText = "Save Selection";
            SaveButtonBg = "#2563eb";
            SaveButtonFg = "#f0f4fe";
        }
    }

    // Update LoadFoldersAsync to set HasLoadedFolders
    partial void OnFoldersChanged(ObservableCollection<FileNode> value)
    {
        HasLoadedFolders = value is { Count: > 0 };
        UpdateSelectionSummary();
    }
}

public partial class FileNode : ObservableObject
{
    [ObservableProperty]
    private string _id = "";

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _path = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDocument))]
    private bool _isFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDocument))]
    private bool _isNotebook;

    public bool IsDocument => !IsFolder && !IsNotebook;

    [ObservableProperty]
    private bool _isExpanded;

    // Changed from simple IsSelected to tri-state
    [ObservableProperty]
    private bool? _selectionState = false;

    [ObservableProperty]
    private string _virtualPath = "";

    [ObservableProperty]
    private ObservableCollection<FileNode>? _children;

    public string? ParentId { get; set; }

    // Helper property for displaying item count in folders
    public string ItemCountText
    {
        get
        {
            if (!IsFolder || Children == null || Children.Count == 0)
                return "";

            var folderCount = Children.Count(c => c.IsFolder);
            var docCount = Children.Count(c => !c.IsFolder);

            var parts = new List<string>();
            if (folderCount > 0) parts.Add($"{folderCount} folder{(folderCount != 1 ? "s" : "")}");
            if (docCount > 0) parts.Add($"{docCount} document{(docCount != 1 ? "s" : "")}");

            return parts.Count > 0 ? $"({string.Join(", ", parts)})" : "";
        }
    }

    // Update selection state based on children
    public void UpdateSelectionFromChildren()
    {
        if (!IsFolder || Children == null || Children.Count == 0) return;

        var selectedCount = Children.Count(child => child.SelectionState is true or null);

        // Update our state based on children
        SelectionState = selectedCount == 0 ? false : selectedCount == Children.Count ? true : null;
    }

    // Recursively set selection state for all children
    public void SetChildrenSelection(bool? state)
    {
        if (Children == null) return;

        foreach (var child in Children)
        {
            child.SelectionState = state;
            if (child.IsFolder)
            {
                child.SetChildrenSelection(state);
            }
        }
    }

    // Get all selected items recursively (returns both documents and parent folders containing selected documents)
    public (List<FileNode> documents, List<FileNode> folders) GetSelectedItems()
    {
        var docs = new List<FileNode>();
        var folders = new List<FileNode>();

        if (!IsFolder && SelectionState == true)
        {
            docs.Add(this);
        }

        if (Children != null)
        {
            bool hasSelectedChildren = false;
            foreach (var child in Children)
            {
                var (childDocs, childFolders) = child.GetSelectedItems();
                if (childDocs.Count > 0)
                {
                    hasSelectedChildren = true;
                    docs.AddRange(childDocs);
                    folders.AddRange(childFolders);
                }
            }

            // If this is a folder and contains selected documents, include it
            if (IsFolder && hasSelectedChildren)
            {
                folders.Add(this);
            }
        }

        return (docs, folders);
    }
}