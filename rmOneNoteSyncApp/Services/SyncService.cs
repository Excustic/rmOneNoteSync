using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using rmOneNoteSyncApp.Models;
using rmOneNoteSyncApp.Services.Interfaces;

namespace rmOneNoteSyncApp.Services;

public class SyncService : ISyncService
{
    private readonly ILogger<SyncService> _logger;
    private readonly IDatabaseService _databaseService;
    private readonly IRmConverterService _converterService;
    private readonly IOneNoteClient _oneNoteClient;
    private readonly ISyncServerService _syncServer;
    private readonly Timer? _autoSyncTimer;
    private bool _isSyncing;
    private CancellationTokenSource? _autoSyncCancellation;

    public event EventHandler<SyncProgressEventArgs>? SyncProgress;
    public event EventHandler<SyncCompletedEventArgs>? SyncCompleted;

    public bool IsSyncing => _isSyncing;
    public DateTime? LastSyncTime { get; private set; }

    public SyncService(
        ILogger<SyncService> logger,
        IDatabaseService databaseService,
        IRmConverterService converterService,
        IOneNoteClient oneNoteClient,
        ISyncServerService syncServer)
    {
        _logger = logger;
        _databaseService = databaseService;
        _converterService = converterService;
        _oneNoteClient = oneNoteClient;
        _syncServer = syncServer;

        // Listen to background file drops and immediately start syncing
        _syncServer.FileReceived += async (s, e) =>
        {
            if (!_isSyncing)
            {
                await Task.Run(() => SyncAllAsync());
            }
        };
    }

    public async Task<SyncResult> SyncAllAsync(CancellationToken cancellationToken = default)
    {
        if (_isSyncing)
        {
            throw new InvalidOperationException("Sync already in progress");
        }

        _isSyncing = true;
        var result = new SyncResult { StartTime = DateTime.UtcNow };
        var startTime = DateTime.Now;

        try
        {
            ReportProgress("Starting sync...", 0, 0);

            // Get all pending pages
            var pendingPages = await _databaseService.GetPendingPagesAsync(1000);
            result.TotalDocuments = pendingPages.GroupBy(p => p.DocumentId).Count();

            if (!pendingPages.Any())
            {
                _logger.LogInformation("No pending items to sync");
                result.Success = true;
                return result;
            }

            ReportProgress($"Found {pendingPages.Count} pages to sync", pendingPages.Count, 0);

            var config = await _databaseService.GetConfigurationAsync();
            var whitelist = config?.SyncFiles ?? new List<string>();

            int processed = 0;
            var errors = new List<string>();

            foreach (var page in pendingPages)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                // Enforce whitelist check to intercept unauthorized items.
                if (!whitelist.Contains(page.DocumentId))
                {
                    _logger.LogWarning("Skipping upload for {Page} (Doc: {Doc}) because its notebook is not explicitly active in Sync settings.", page.Title, page.DocumentId);
                    await _databaseService.UpdatePageStatusAsync(page.DocumentId, page.PageId, SyncStatus.Skipped);
                    continue;
                }

                try
                {
                    ReportProgress($"Syncing {page.VirtualPath}...", pendingPages.Count, processed);

                    if (!File.Exists(page.LocalFilePath))
                    {
                        throw new FileNotFoundException($"The sync service could not find the raw file: {page.LocalFilePath}");
                    }

                    // 1. Convert .rm to InkML (.xml) and Presentation (.html)
                    var convResult = await _converterService.ConvertToInkMLAsync(page.LocalFilePath);
                    if (!convResult.Success || string.IsNullOrEmpty(convResult.InkMLPath) || string.IsNullOrEmpty(convResult.HtmlPath))
                    {
                        throw new Exception($"Conversion to InkML failed: {convResult.ErrorMessage}");
                    }

                    // 2. Parse Virtual Path to create Graph nodes
                    var (notebook, section, pageName) = ParseVirtualPath(page.VirtualPath);

                    // Ensure notebook exists
                    var notebooks = await _oneNoteClient.GetNotebooksAsync();
                    var targetNotebook = notebooks.FirstOrDefault(n => n.DisplayName == notebook);
                    if (targetNotebook == null)
                    {
                        targetNotebook = await _oneNoteClient.CreateNotebookAsync(notebook);
                    }

                    // Ensure section exists
                    var sections = await _oneNoteClient.GetSectionsAsync(targetNotebook.Id!);
                    var targetSection = sections.FirstOrDefault(s => s.DisplayName == section);
                    if (targetSection == null)
                    {
                        targetSection = await _oneNoteClient.CreateSectionAsync(targetNotebook.Id!, section);
                    }

                    // Check for existing page with same title to prevent duplication
                    var existingPages = await _oneNoteClient.GetPagesAsync(targetSection.Id!);
                    var existingPage = existingPages.FirstOrDefault(p => p.Title == pageName);
                    if (existingPage != null)
                    {
                        _logger.LogInformation("Deleting existing OneNote page {Page} before upload to prevent duplication", pageName);
                        await _oneNoteClient.DeletePageAsync(existingPage.Id!);
                    }

                    // 3. Read transcoded files
                    byte[] inkmlData = await File.ReadAllBytesAsync(convResult.InkMLPath, cancellationToken);
                    byte[] htmlData = await File.ReadAllBytesAsync(convResult.HtmlPath, cancellationToken);

                    var metadata = new Dictionary<string, string>
                    {
                        { "Original Path", page.VirtualPath },
                        { "Document ID", page.DocumentId },
                        { "Page Number", page.PageNumber },
                        { "Imported", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") }
                    };

                    // 4. Upload Multipart payload
                    await _oneNoteClient.UploadInkMLPageAsync(
                        targetSection.Id!,
                        pageName,
                        inkmlData,
                        htmlData,
                        metadata);

                    await _databaseService.UpdatePageStatusAsync(
                        page.DocumentId,
                        page.PageId,
                        SyncStatus.Uploaded);

                    result.SuccessfulDocuments++;
                    _logger.LogInformation("Successfully transcoded and uploaded {Path} to OneNote", page.VirtualPath);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to sync {Path}", page.VirtualPath);
                    errors.Add($"{page.VirtualPath}: {ex.Message}");

                    await _databaseService.UpdatePageStatusAsync(
                        page.DocumentId,
                        page.PageId,
                        SyncStatus.Failed,
                        ex.Message);

                    result.FailedDocuments++;
                }

                processed++;
            }

            result.Success = result.FailedDocuments == 0;
            result.Errors = errors;
            LastSyncTime = DateTime.Now;

            var duration = DateTime.Now - startTime;
            ReportCompleted(result.Success, result.SuccessfulDocuments, result.FailedDocuments, duration);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Sync failed");
            result.Success = false;
            result.Errors.Add(ex.Message);
            ReportCompleted(false, 0, 0, DateTime.Now - startTime, ex.Message);
        }
        finally
        {
            _isSyncing = false;
            result.EndTime = DateTime.UtcNow;
        }

        return result;
    }

    public async Task<SyncResult> SyncDocumentAsync(string documentId, CancellationToken cancellationToken = default)
    {
        var document = await _databaseService.GetDocumentMetadataAsync(documentId);
        if (document == null)
        {
            throw new ArgumentException($"Document {documentId} not found");
        }

        var result = new SyncResult { StartTime = DateTime.UtcNow };
        return result;
    }

    public async Task<SyncResult> SyncFolderAsync(string folderPath, CancellationToken cancellationToken = default)
    {
        var documents = await _databaseService.GetAllDocumentsAsync();
        var folderDocs = documents.Where(d => d.Parent == folderPath).ToList();

        var result = new SyncResult { StartTime = DateTime.UtcNow };
        return result;
    }

    public async Task StartAutomaticSyncAsync(int intervalMinutes)
    {
        _autoSyncCancellation = new CancellationTokenSource();

        while (!_autoSyncCancellation.Token.IsCancellationRequested)
        {
            try
            {
                await SyncAllAsync(_autoSyncCancellation.Token);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto-sync failed");
            }

            await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), _autoSyncCancellation.Token);
        }
    }

    public async Task StopAutomaticSyncAsync()
    {
        _autoSyncCancellation?.Cancel();

        while (_isSyncing)
        {
            await Task.Delay(100);
        }
    }

    private void ReportProgress(string message, int total, int processed)
    {
        SyncProgress?.Invoke(this, new SyncProgressEventArgs
        {
            Message = message,
            TotalItems = total,
            ProcessedItems = processed
        });
    }

    private void ReportCompleted(bool success, int synced, int failed, TimeSpan duration, string? error = null)
    {
        SyncCompleted?.Invoke(this, new SyncCompletedEventArgs
        {
            Success = success,
            ItemsSynced = synced,
            ItemsFailed = failed,
            Duration = duration,
            ErrorMessage = error
        });
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

    public void Dispose()
    {
        _autoSyncCancellation?.Cancel();
        _autoSyncTimer?.Dispose();
    }
}