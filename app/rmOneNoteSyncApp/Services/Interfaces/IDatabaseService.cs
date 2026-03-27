using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using rmOneNoteSyncApp.Models;

namespace rmOneNoteSyncApp.Services.Interfaces;

/// <summary>
/// Local database service for caching and persistence
/// </summary>
public interface IDatabaseService
{
    Task InitializeAsync(string databasePath);
    void Initialize(string databasePath);
    // Configuration management
    Task<SyncConfiguration?> GetConfigurationAsync();
    Task SaveConfigurationAsync(SyncConfiguration config);

    // Page metadata management
    Task<PageMetadata?> GetPageMetadataAsync(string documentId, string pageId);
    Task<List<PageMetadata>> GetPendingPagesAsync(int limit = 100);
    Task<List<PageMetadata>> GetRecentPagesAsync(int limit = 100);
    Task<List<PageMetadata>> GetPagesByStatusAsync(SyncStatus status);
    Task SavePageMetadataAsync(PageMetadata metadata);
    Task UpdatePageStatusAsync(string documentId, string pageId, SyncStatus status, string? error = null, string? oneNoteUrl = null);

    // Document metadata management
    Task<DocumentMetadata?> GetDocumentMetadataAsync(string documentId);
    Task<List<DocumentMetadata>> GetAllDocumentsAsync();
    Task SaveDocumentMetadataAsync(DocumentMetadata metadata);
    Task UpsertFileTreeAsync(IEnumerable<DocumentMetadata> documents);

    // Cache management
    Task<long> GetCacheSizeAsync();
    Task<int> CleanupOldCacheAsync(int daysToKeep);
    Task ClearCacheAsync();

    // Sync history
    Task RecordSyncEventAsync(string documentId, string pageId, bool success, string? details = null);
    Task<List<SyncEvent>> GetSyncHistoryAsync(int limit = 100);

    // Telemetry and generic state
    Task<string?> GetTelemetryAsync(string key);
    Task SaveTelemetryAsync(string key, string value);
}

public class SyncEvent
{
    public int Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string DocumentId { get; set; } = "";
    public string PageId { get; set; } = "";
    public bool Success { get; set; }
    public string? Details { get; set; }
}