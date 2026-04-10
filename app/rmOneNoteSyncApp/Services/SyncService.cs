using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
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
    private bool _isSyncing;
    private CancellationTokenSource? _autoSyncCancellation;
    private readonly ConcurrentDictionary<string, byte> _cancelledItems = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _itemCts = new();
    private static readonly SemaphoreSlim _graphApiSemaphore = new(5, 5); // Microsoft GraphAPI limit for concurrent requests for personal accounts

    public event EventHandler<SyncProgressEventArgs>? SyncProgress;
    public event EventHandler<PageSyncCompletedEventArgs>? PageSyncCompleted;
    public event EventHandler<SyncCompletedEventArgs>? SyncCompleted;

    public bool IsSyncing => _isSyncing;
    public DateTime? LastSyncTime { get; private set; }

    private readonly Channel<bool> _syncChannel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });

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

        // Start the sequential sync consumer
        _ = Task.Run(async () => await ProcessSyncQueueAsync(_autoSyncCancellation?.Token ?? CancellationToken.None));

        // Nudge the channel when a new file arrives
        _syncServer.FileReceived += (s, e) =>
        {
            _logger.LogDebug("New file received: {Id}. Nudging sync queue.", e.PageId);
            TriggerSync();
        };
    }

    private async Task ProcessSyncQueueAsync(CancellationToken token)
    {
        _logger.LogInformation("Sync Queue Consumer started.");

        while (await _syncChannel.Reader.WaitToReadAsync(token))
        {
            if (_syncChannel.Reader.TryRead(out _)) // Queue is bounded to 1, so we only need to check if there's anything in it
            {
                try
                {
                    _logger.LogDebug("Processing sync request from queue...");
                    await SyncAllAsync(token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Error during background sync execution from queue.");
                }
            }
        }
    }

    public void TriggerSync()
    {
        _syncChannel.Writer.TryWrite(true);
        _logger.LogDebug("Manual sync trigger written to channel.");
    }

    public async Task RecoverInProgressItemsAsync()
    {
        _logger.LogInformation("Starting session recovery...");
        await _databaseService.ResetInProgressStatusAsync();
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
            ReportProgress("Starting sync...", 0, 0, SyncStatus.Pending);

            // Get all pending pages
            var pendingPages = await _databaseService.GetPendingPagesAsync(1000);
            result.TotalDocuments = pendingPages.GroupBy(p => p.DocumentId).Count();

            if (!pendingPages.Any())
            {
                _logger.LogDebug("No pending items to sync");
                result.Success = true;
                return result;
            }

            ReportProgress($"Found {pendingPages.Count} pages to sync", pendingPages.Count, 0, SyncStatus.Pending);

            var config = await _databaseService.GetConfigurationAsync();
            var whitelist = config?.SyncFiles ?? new List<string>();
            var maxThreads = Math.Max(1, config?.MaxThreads ?? 3);

            int processed = 0;
            int successCount = 0;
            int failCount = 0;
            var errors = new ConcurrentBag<string>();

            _logger.LogDebug("Starting parallel sync with MaxThreads={MaxThreads} for {Count} pages", maxThreads, pendingPages.Count);

            // Per-sync cache for Notebooks and Sections to avoid redundant Graph calls
            var notebookCache = new ConcurrentDictionary<string, Notebook>();
            var sectionCache = new ConcurrentDictionary<string, Section>(); // Key: notebookId|sectionName

            // Keyed locks to prevent race conditions during notebook/section creation
            var notebookLocks = new ConcurrentDictionary<string, SemaphoreSlim>();
            var sectionLocks = new ConcurrentDictionary<string, SemaphoreSlim>();

            // Fetch initial hierarchy once to pre-populate cache using $expand=sections
            try
            {
                var initialNotebooks = await _oneNoteClient.GetNotebooksAsync();
                foreach (var nb in initialNotebooks)
                {
                    if (nb.DisplayName != null) notebookCache[nb.DisplayName] = nb;
                    if (nb.Sections != null)
                    {
                        foreach (var sec in nb.Sections)
                        {
                            sectionCache[$"{nb.Id}|{sec.DisplayName}"] = sec;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to pre-populate OneNote hierarchy cache. Sync will continue with on-demand lookups.");
            }

            await Parallel.ForEachAsync(pendingPages, new ParallelOptions
            {
                MaxDegreeOfParallelism = maxThreads,
                CancellationToken = cancellationToken
            }, async (page, globalToken) =>
            {
                string itemKey = $"{page.DocumentId}_{page.PageId}";

                // Skip if pre-cancelled
                if (_cancelledItems.TryRemove(itemKey, out _))
                {
                    _logger.LogDebug("Skipping pre-cancelled item {Page}", page.Title);
                    Interlocked.Increment(ref processed);
                    return;
                }

                // Enforce whitelist
                if (!whitelist.Contains(page.DocumentId))
                {
                    _logger.LogWarning("Skipping upload for {Page} (Doc ID: {DocId}) — not in current whitelist (Whitelist count: {Count})",
                        page.Title, page.DocumentId, whitelist.Count);
                    await _databaseService.UpdatePageStatusAsync(page.DocumentId, page.PageId, SyncStatus.Skipped);
                    Interlocked.Increment(ref processed);
                    return;
                }

                // Create a per-item CTS linked to the global token
                using var itemCts = CancellationTokenSource.CreateLinkedTokenSource(globalToken);
                _itemCts[itemKey] = itemCts;
                var itemToken = itemCts.Token;

                try
                {
                    await _databaseService.UpdatePageStatusAsync(page.DocumentId, page.PageId, SyncStatus.Queued);

                    var total = pendingPages.Count;
                    var current = Interlocked.CompareExchange(ref processed, 0, 0); // read without modifying

                    ReportProgress($"Converting notes...", total, current, SyncStatus.Transcoding, page.VirtualPath, page.DocumentId, page.PageId, 1, 8);
                    await _databaseService.UpdatePageStatusAsync(page.DocumentId, page.PageId, SyncStatus.Transcoding);

                    if (!File.Exists(page.LocalFilePath))
                    {
                        throw new FileNotFoundException($"The sync service could not find the raw file: {page.LocalFilePath}");
                    }

                    itemToken.ThrowIfCancellationRequested();

                    // 1. Convert .rm to InkML (.xml) and Presentation (.html)
                    var convResult = await _converterService.ConvertToInkMLAsync(page.LocalFilePath);
                    if (!convResult.Success || string.IsNullOrEmpty(convResult.InkMLPath) || string.IsNullOrEmpty(convResult.HtmlPath))
                    {
                        throw new Exception($"Conversion to InkML failed: {convResult.ErrorMessage}");
                    }

                    itemToken.ThrowIfCancellationRequested();

                    // 2. Parse Virtual Path to create Graph nodes
                    var (notebook, section, pageName) = ParseVirtualPath(page.VirtualPath);
                    if (section.Length > 49) section = section.Substring(0, 49);

                    // Ensure notebook exists
                    ReportProgress($"Fetching Notebook...", total, current, SyncStatus.InProgress, page.VirtualPath, page.DocumentId, page.PageId, 2, 8);

                    if (!notebookCache.TryGetValue(notebook, out var targetNotebook))
                    {
                        var nbLock = notebookLocks.GetOrAdd(notebook, _ => new SemaphoreSlim(1, 1));
                        await nbLock.WaitAsync(itemToken);
                        try
                        {
                            // Double-check cache inside the lock
                            if (!notebookCache.TryGetValue(notebook, out targetNotebook))
                            {
                                var notebooks = await _oneNoteClient.GetNotebooksAsync();
                                targetNotebook = notebooks.FirstOrDefault(n => n.DisplayName == notebook);
                                if (targetNotebook == null)
                                {
                                    _logger.LogInformation("Creating new OneNote notebook: {Notebook}", notebook);
                                    targetNotebook = await _oneNoteClient.CreateNotebookAsync(notebook);
                                }
                                notebookCache[notebook] = targetNotebook;
                            }
                        }
                        finally
                        {
                            nbLock.Release();
                        }
                    }

                    itemToken.ThrowIfCancellationRequested();

                    // Ensure section exists (Keyed by notebookId and section name)
                    ReportProgress($"Fetching Section...", total, current, SyncStatus.InProgress, page.VirtualPath, page.DocumentId, page.PageId, 3, 8);
                    string sectionKey = $"{targetNotebook.Id}|{section}";

                    if (!sectionCache.TryGetValue(sectionKey, out var targetSection))
                    {
                        var secLock = sectionLocks.GetOrAdd(sectionKey, _ => new SemaphoreSlim(1, 1));
                        await secLock.WaitAsync(itemToken);
                        try
                        {
                            // Double-check cache inside the lock
                            if (!sectionCache.TryGetValue(sectionKey, out targetSection))
                            {
                                var sections = await _oneNoteClient.GetSectionsAsync(targetNotebook.Id!);
                                targetSection = sections.FirstOrDefault(s => s.DisplayName == section);
                                if (targetSection == null)
                                {
                                    _logger.LogInformation("Creating new OneNote section: {Section} in Notebook: {Notebook}", section, notebook);
                                    targetSection = await _oneNoteClient.CreateSectionAsync(targetNotebook.Id!, section);
                                }
                                sectionCache[sectionKey] = targetSection;
                            }
                        }
                        finally
                        {
                            secLock.Release();
                        }
                    }

                    // Save the Notebook link back to DocumentMetadata CustomMetadata
                    var docMeta = await _databaseService.GetDocumentMetadataAsync(page.DocumentId);
                    if (docMeta != null)
                    {
                        var notebookLink = targetNotebook.Links?.OneNoteWebUrl?.Href ?? targetNotebook.Links?.OneNoteClientUrl?.Href;
                        if (!string.IsNullOrEmpty(notebookLink) &&
                            (!docMeta.CustomMetadata.ContainsKey("OneNoteUrl") || docMeta.CustomMetadata["OneNoteUrl"]?.ToString() != notebookLink))
                        {
                            docMeta.CustomMetadata["OneNoteUrl"] = notebookLink;
                            await _databaseService.SaveDocumentMetadataAsync(docMeta);
                        }
                    }

                    itemToken.ThrowIfCancellationRequested();

                    // Check for existing page with same title to prevent duplication
                    ReportProgress($"Checking records...", total, current, SyncStatus.InProgress, page.VirtualPath, page.DocumentId, page.PageId, 4, 8);

                    var existingPages = await _oneNoteClient.GetPagesAsync(targetSection.Id!);
                    var existingPage = existingPages.FirstOrDefault(p => p.Title == pageName);
                    if (existingPage != null)
                    {
                        ReportProgress($"Overwriting duplicate...", total, current, SyncStatus.InProgress, page.VirtualPath, page.DocumentId, page.PageId, 5, 8);
                        _logger.LogDebug("Deleting existing OneNote page {Page} before upload to prevent duplication", pageName);
                        await _oneNoteClient.DeletePageAsync(existingPage.Id!);
                    }

                    itemToken.ThrowIfCancellationRequested();

                    ReportProgress($"Preparing payload...", total, current, SyncStatus.InProgress, page.VirtualPath, page.DocumentId, page.PageId, 6, 8);
                    // 3. Read transcoded files
                    byte[] inkmlData = await File.ReadAllBytesAsync(convResult.InkMLPath, itemToken);
                    byte[] htmlData = await File.ReadAllBytesAsync(convResult.HtmlPath, itemToken);

                    var metadata = new Dictionary<string, string>
                    {
                        { "Original Path", page.VirtualPath },
                        { "Document ID", page.DocumentId },
                        { "Page Number", page.PageNumber },
                        { "Imported", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") }
                    };

                    // 4. Upload Multipart payload
                    ReportProgress($"Uploading page...", total, current, SyncStatus.Uploading, page.VirtualPath, page.DocumentId, page.PageId, 7, 8);
                    await _databaseService.UpdatePageStatusAsync(page.DocumentId, page.PageId, SyncStatus.Uploading);

                    string pageId;
                    await _graphApiSemaphore.WaitAsync(itemToken);
                    try
                    {
                        pageId = await _oneNoteClient.UploadInkMLPageAsync(
                            targetSection.Id!,
                            pageName,
                            inkmlData,
                            htmlData,
                            metadata);
                    }
                    finally
                    {
                        _graphApiSemaphore.Release();
                    }

                    itemToken.ThrowIfCancellationRequested();

                    ReportProgress($"Completing sync...", total, current, SyncStatus.Indexing, page.VirtualPath, page.DocumentId, page.PageId, 8, 8);
                    await _databaseService.UpdatePageStatusAsync(page.DocumentId, page.PageId, SyncStatus.Indexing);

                    string? oneNoteUrl = null;
                    try
                    {
                        await _graphApiSemaphore.WaitAsync(itemToken);
                        try
                        {
                            var createdPage = await _oneNoteClient.GetPageAsync(pageId);
                            oneNoteUrl = createdPage?.Links?.OneNoteWebUrl?.Href ?? createdPage?.Links?.OneNoteClientUrl?.Href;
                        }
                        finally
                        {
                            _graphApiSemaphore.Release();
                        }
                        
                        if (string.IsNullOrEmpty(oneNoteUrl))
                        {
                            throw new Exception("Graph API returned the page, but no valid OneNote URL was attached. Indexing might still be incomplete.");
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to retrieve OneNote URL for uploaded page {PageId}", pageId);
                        throw new Exception($"Graph API indexing timeout: {ex.Message}");
                    }

                    await _databaseService.UpdatePageStatusAsync(
                        page.DocumentId,
                        page.PageId,
                        SyncStatus.Uploaded,
                        null,
                        oneNoteUrl);

                    // Update timestamp so recent files jump to top
                    var currentDoc = await _databaseService.GetDocumentMetadataAsync(page.DocumentId);
                    if (currentDoc != null)
                    {
                        currentDoc.LastModified = DateTime.UtcNow;
                        await _databaseService.SaveDocumentMetadataAsync(currentDoc);
                    }

                    var currentPage = await _databaseService.GetPageMetadataAsync(page.DocumentId, page.PageId);
                    if (currentPage != null)
                    {
                        currentPage.LastModified = DateTime.UtcNow;
                        await _databaseService.SavePageMetadataAsync(currentPage);
                    }

                    Interlocked.Increment(ref successCount);
                    _logger.LogDebug("Successfully transcoded and uploaded {Path} to OneNote", page.VirtualPath);
                    PageSyncCompleted?.Invoke(this,
                        new PageSyncCompletedEventArgs(page, true, null, oneNoteUrl));
                }
                catch (OperationCanceledException) when (itemToken.IsCancellationRequested && !globalToken.IsCancellationRequested)
                {
                    // Per-item cancellation — mark as Skipped, don't propagate
                    _logger.LogDebug("Item {Page} was cancelled by user", page.Title);
                    await _databaseService.UpdatePageStatusAsync(page.DocumentId, page.PageId, SyncStatus.Skipped);
                }
                catch (OperationCanceledException)
                {
                    // Global cancellation — rethrow so Parallel.ForEachAsync stops
                    throw;
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

                    Interlocked.Increment(ref failCount);
                }
                finally
                {
                    _itemCts.TryRemove(itemKey, out _);
                    Interlocked.Increment(ref processed);
                }
            });

            result.SuccessfulDocuments = successCount;
            result.FailedDocuments = failCount;

            result.Success = result.FailedDocuments == 0;
            result.Errors = errors.ToList();
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

    public async Task StartAutomaticSyncAsync(int intervalSeconds)
    {
        // Cancel existing loop before starting a new one
        if (_autoSyncCancellation != null && !_autoSyncCancellation.IsCancellationRequested)
        {
            _logger.LogDebug("Cancelling existing auto-sync loop before restarting.");
            _autoSyncCancellation.Cancel();
            _autoSyncCancellation.Dispose();
        }

        _autoSyncCancellation = new CancellationTokenSource();
        var token = _autoSyncCancellation.Token;

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_isSyncing)
                {
                    _logger.LogDebug("Skipping automatic sync as another sync is already in progress.");
                }
                else
                {
                    _logger.LogDebug("Automatic Sync Interval Triggered (Every {Interval}s). Nudging queue.", intervalSeconds);
                    _syncChannel.Writer.TryWrite(true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto-sync failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), _autoSyncCancellation.Token);
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

    private void ReportProgress(string message, int total, int processed, SyncStatus status, string? currentItem = null, string? docId = null, string? pageId = null, int currentStep = 0, int totalSteps = 0)
    {
        SyncProgress?.Invoke(this, new SyncProgressEventArgs
        {
            Message = message,
            TotalItems = total,
            ProcessedItems = processed,
            CurrentItem = currentItem,
            CurrentDocumentId = docId,
            CurrentPageId = pageId,
            CurrentStep = currentStep,
            TotalSteps = totalSteps,
            Status = status
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

    private static string SanitizeOneNoteName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "Untitled";

        // Replace known invalid characters with underscore: ? * \ / : < > | & # " ' % ~
        var sanitized = Regex.Replace(name, @"[?*\\/:<>|&#""'%~]", "_");

        // Remove control characters (which can hide as bad chars) and trim
        sanitized = new string([.. sanitized.Where(c => !char.IsControl(c))]).Trim();

        if (string.IsNullOrEmpty(sanitized)) return "Untitled";

        // OneNote limits section names to 50 chars
        if (sanitized.Length > 49) sanitized = sanitized[..49].TrimEnd();

        return sanitized;
    }

    private static (string notebook, string section, string page) ParseVirtualPath(string virtualPath)
    {
        var parts = virtualPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
            return ("rm_Uncategorized", "Default", "Untitled");

        if (parts.Length == 1)
            return ("rm_Uncategorized", "Default", parts[0]);

        string notebook;
        string section;
        string page;

        if (parts.Length == 2)
        {
            notebook = "rm_" + parts[0];
            section = parts[0];
            page = parts[1];
        }
        else
        {
            var notebookParts = parts.Take(parts.Length - 2);
            notebook = "rm_" + string.Join("_", notebookParts);
            section = parts[^2];
            page = parts[^1];
        }

        notebook = SanitizeOneNoteName(notebook);
        section = SanitizeOneNoteName(section);

        return (notebook, section, page);
    }

    public async Task CancelSyncItemAsync(string documentId, string pageId)
    {
        string itemKey = $"{documentId}_{pageId}";

        // If the item is currently being processed, cancel its token
        if (_itemCts.TryGetValue(itemKey, out var cts))
        {
            _logger.LogDebug("Cancelling in-flight sync for {ItemKey}", itemKey);
            cts.Cancel();
        }
        else
        {
            // Not yet started — mark for skip so the worker skips it when it picks it up
            _logger.LogDebug("Pre-cancelling queued item {ItemKey}", itemKey);
            _cancelledItems[itemKey] = 0;
        }

        // Mark as skipped in DB
        await _databaseService.UpdatePageStatusAsync(documentId, pageId, SyncStatus.Skipped);
    }

    public void Dispose()
    {
        _autoSyncCancellation?.Cancel();
    }
}