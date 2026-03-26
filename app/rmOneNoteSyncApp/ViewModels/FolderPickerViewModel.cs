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

    [ObservableProperty]
    private ObservableCollection<FileNode> _folders = new();

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

    public FolderPickerViewModel(ISshService sshService, IDatabaseService databaseService, IDeviceDetectionService deviceDetectionService)
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

            _logger?.LogDebug("Loading folder structure from reMarkable");

            // Get all metadata files
            var findCommand = "find /home/root/.local/share/remarkable/xochitl -name '*.metadata' -type f 2>/dev/null";
            var metadataFiles = await _sshService.ExecuteCommandAsync(findCommand);

            if (string.IsNullOrWhiteSpace(metadataFiles))
            {
                StatusMessage = "No documents found on device";
                return;
            }

            var lines = metadataFiles.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            _logger?.LogDebug("Found {Count} metadata files", lines.Length);

            var allNodes = new List<FileNode>();
            var nodeMap = new Dictionary<string, FileNode>();

            // Process each metadata file
            foreach (var metadataPath in lines)
            {
                try
                {
                    // Extract document ID from path
                    var fileName = System.IO.Path.GetFileName(metadataPath);
                    var docId = fileName.Replace(".metadata", "");

                    // Read metadata content
                    var content = await _sshService.ExecuteCommandAsync($"cat '{metadataPath}'");
                    if (string.IsNullOrWhiteSpace(content))
                        continue;

                    // Parse metadata
                    var node = ParseMetadataToNode(docId, content);
                    if (node != null)
                    {
                        // Check if it's a native notebook (the directory exists)
                        if (!node.IsFolder)
                        {
                            var checkDir = await _sshService.ExecuteCommandAsync($"[ -d \"/home/root/.local/share/remarkable/xochitl/{docId}\" ] && echo 'yes' || echo 'no'");
                            node.IsNotebook = checkDir.Trim() == "yes";
                        }

                        allNodes.Add(node);
                        nodeMap[node.Id] = node;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to process metadata file: {Path}", metadataPath);
                }
            }

            // Build hierarchy
            var rootNodes = BuildHierarchy(allNodes, nodeMap);

            var deviceIp = _deviceDetectionService.CurrentDevice?.IpAddress ?? "10.11.99.1";
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

    private FileNode? ParseMetadataToNode(string docId, string metadata)
    {
        try
        {
            using var doc = JsonDocument.Parse(metadata);
            var root = doc.RootElement;

            var node = new FileNode
            {
                Id = docId,
                Name = root.TryGetProperty("visibleName", out var name) ? name.GetString() ?? "Untitled" : "Untitled",
                Path = docId,
                IsFolder = root.TryGetProperty("type", out var type) && type.GetString() == "CollectionType",
                ParentId = root.TryGetProperty("parent", out var parent) ? parent.GetString() : null
            };

            // Clean up parent ID (empty string or "trash" means root level)
            if (string.IsNullOrWhiteSpace(node.ParentId) || node.ParentId == "trash")
            {
                node.ParentId = null;
            }

            return node;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to parse metadata for document {DocId}", docId);
            return null;
        }
    }

    private List<FileNode> BuildHierarchy(List<FileNode> allNodes, Dictionary<string, FileNode> nodeMap)
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
    private FileNode? FindFolderNode(FileNode source, string nodeId)
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

    private void CountSelected(FileNode node, ref int docs, ref int folders)
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
            // Collect all selected document IDs and their FileNodes
            var selectedIds = new List<string>();
            var selectedNodes = new List<FileNode>();
            foreach (var root in Folders)
            {
                var nodes = root.GetSelectedDocuments();
                selectedNodes.AddRange(nodes);
                selectedIds.AddRange(nodes.Select(n => n.Id));
            }

            _logger?.LogDebug("Saving selection: {Count} documents", selectedIds.Count);

            // Save to database
            var config = await _databaseService.GetConfigurationAsync() ?? new SyncConfiguration();
            config.SyncFiles = selectedIds;
            await _databaseService.SaveConfigurationAsync(config);

            // Create stub DocumentMetadata so new Notebooks appear on the Dashboard instantly
            var existingDocs = await _databaseService.GetAllDocumentsAsync();
            var existingIds = existingDocs.Select(d => d.DocumentId).ToHashSet();
            
            foreach (var node in selectedNodes)
            {
                if (!existingIds.Contains(node.Id))
                {
                    await _databaseService.SaveDocumentMetadataAsync(new DocumentMetadata
                    {
                        DocumentId = node.Id,
                        VisibleName = node.Name,
                        Type = "CollectionType",
                        Parent = node.ParentId ?? "",
                        LastModified = DateTime.UtcNow
                    });
                }
            }

            StatusMessage = $"Saved {selectedIds.Count} documents to sync";

            // IMPORTANT: Update the reMarkable configuration
            if (_sshService.IsConnected)
            {
                StatusMessage = "Updating reMarkable configuration...";

                var configProvider = App.ServiceProvider?.GetService<IConfigurationProviderService>();
                if (configProvider != null)
                {
                    var success = await configProvider.UpdateDeviceConfigurationAsync();
                    if (success)
                    {
                        StatusMessage = $"✅ Configuration synced to reMarkable! {selectedIds.Count} documents will sync.";
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

    // Get all selected documents recursively
    public List<FileNode> GetSelectedDocuments()
    {
        var nodes = new List<FileNode>();

        if (!IsFolder && SelectionState == true)
        {
            nodes.Add(this);
        }

        if (Children != null)
        {
            foreach (var child in Children)
            {
                nodes.AddRange(child.GetSelectedDocuments());
            }
        }

        return nodes;
    }
}