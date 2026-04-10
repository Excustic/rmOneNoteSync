using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using rmOneNoteSyncApp.Models;
using rmOneNoteSyncApp.Services.Interfaces;

namespace rmOneNoteSyncApp.Services;

public class SqliteDatabaseService : IDatabaseService
{
    private readonly ILogger<SqliteDatabaseService> _logger;
    private string? _databasePath;
    private string ConnectionString => $"Data Source={_databasePath};Cache=Shared;";

    public SqliteDatabaseService(ILogger<SqliteDatabaseService> logger)
    {
        _logger = logger;
    }
    public void Initialize(string databasePath)
    {
        _databasePath = databasePath;

        // Ensure directory exists
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // Create tables
        CreateTables();

        _logger.LogDebug("Database initialized at {Path}", databasePath);
    }


    public async Task InitializeAsync(string databasePath)
    {
        _databasePath = databasePath;

        // Ensure directory exists
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        // Create tables
        await CreateTablesAsync();

        _logger.LogDebug("Database initialized at {Path}", databasePath);
    }
    private void CreateTables()
    {
        var createTablesSql = @"
            CREATE TABLE IF NOT EXISTS Configuration (
                Id TEXT PRIMARY KEY,
                Json TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            
            CREATE TABLE IF NOT EXISTS Documents (
                DocumentId TEXT PRIMARY KEY,
                VisibleName TEXT NOT NULL,
                Type TEXT NOT NULL,
                Parent TEXT,
                VirtualPath TEXT,
                LastModified TEXT NOT NULL,
                Json TEXT
            );
            
            CREATE TABLE IF NOT EXISTS Pages (
                DocumentId TEXT NOT NULL,
                PageId TEXT NOT NULL,
                PageNumber TEXT,
                Title TEXT,
                VirtualPath TEXT,
                LocalFilePath TEXT,
                CachedFilePath TEXT,
                FileSizeBytes INTEGER,
                LastModified TEXT NOT NULL,
                ContentHash TEXT,
                Status INTEGER NOT NULL,
                LastSyncTime TEXT,
                OneNotePageId TEXT,
                OneNotePageUrl TEXT,
                RetryCount INTEGER DEFAULT 0,
                LastError TEXT,
                Json TEXT,
                PRIMARY KEY (DocumentId, PageId),
                FOREIGN KEY (DocumentId) REFERENCES Documents(DocumentId)
            );
            
            CREATE TABLE IF NOT EXISTS SyncHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Timestamp TEXT NOT NULL,
                DocumentId TEXT NOT NULL,
                PageId TEXT NOT NULL,
                Success INTEGER NOT NULL,
                Details TEXT
            );
            
            CREATE TABLE IF NOT EXISTS Telemetry (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            
            CREATE INDEX IF NOT EXISTS idx_pages_status ON Pages(Status);
            CREATE INDEX IF NOT EXISTS idx_pages_lastsync ON Pages(LastSyncTime);
            CREATE INDEX IF NOT EXISTS idx_synchistory_timestamp ON SyncHistory(Timestamp);";
        
        try
        {
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            connection.Execute("PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;");
            connection.Execute(createTablesSql);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 10) // SQLITE_IOERR
        {
            _logger.LogWarning("Detected SQLite Disk I/O error during initialization. Attempting to clean ghost WAL files.");
            DeleteGhostWalFiles();
            
            // Retry once
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            connection.Execute("PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;");
            connection.Execute(createTablesSql);
        }
    }

    private void DeleteGhostWalFiles()
    {
        if (string.IsNullOrEmpty(_databasePath)) return;

        var ghostFiles = new[] { _databasePath + "-wal", _databasePath + "-shm" };
        foreach (var file in ghostFiles)
        {
            if (File.Exists(file))
            {
                try
                {
                    File.Delete(file);
                    _logger.LogDebug("Force deleted ghost file: {File}", file);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to force delete ghost file: {File}", file);
                }
            }
        }
    }

    private async Task CreateTablesAsync()
    {
        var createTablesSql = @"
            CREATE TABLE IF NOT EXISTS Configuration (
                Id TEXT PRIMARY KEY,
                Json TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            
            CREATE TABLE IF NOT EXISTS Documents (
                DocumentId TEXT PRIMARY KEY,
                VisibleName TEXT NOT NULL,
                Type TEXT NOT NULL,
                Parent TEXT,
                VirtualPath TEXT,
                LastModified TEXT NOT NULL,
                Json TEXT
            );
            
            CREATE TABLE IF NOT EXISTS Pages (
                DocumentId TEXT NOT NULL,
                PageId TEXT NOT NULL,
                PageNumber TEXT,
                Title TEXT,
                VirtualPath TEXT,
                LocalFilePath TEXT,
                CachedFilePath TEXT,
                FileSizeBytes INTEGER,
                LastModified TEXT NOT NULL,
                ContentHash TEXT,
                Status INTEGER NOT NULL,
                LastSyncTime TEXT,
                OneNotePageId TEXT,
                OneNotePageUrl TEXT,
                RetryCount INTEGER DEFAULT 0,
                LastError TEXT,
                Json TEXT,
                PRIMARY KEY (DocumentId, PageId),
                FOREIGN KEY (DocumentId) REFERENCES Documents(DocumentId)
            );
            
            CREATE TABLE IF NOT EXISTS SyncHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Timestamp TEXT NOT NULL,
                DocumentId TEXT NOT NULL,
                PageId TEXT NOT NULL,
                Success INTEGER NOT NULL,
                Details TEXT
            );
            
            CREATE TABLE IF NOT EXISTS Telemetry (
                Key TEXT PRIMARY KEY,
                Value TEXT NOT NULL,
                UpdatedAt TEXT NOT NULL
            );
            
            CREATE INDEX IF NOT EXISTS idx_pages_status ON Pages(Status);
            CREATE INDEX IF NOT EXISTS idx_pages_lastsync ON Pages(LastSyncTime);
            CREATE INDEX IF NOT EXISTS idx_synchistory_timestamp ON SyncHistory(Timestamp);";
        try
        {
            using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync("PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;");
            await connection.ExecuteAsync(createTablesSql);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 10) // SQLITE_IOERR
        {
            _logger.LogWarning("Detected SQLite Disk I/O error during async initialization. Attempting to clean ghost WAL files.");
            DeleteGhostWalFiles();

            // Retry once
            using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync();
            await connection.ExecuteAsync("PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;");
            await connection.ExecuteAsync(createTablesSql);
        }
    }

    public async Task<SyncConfiguration?> GetConfigurationAsync()
    {
        var sql = "SELECT Json FROM Configuration ORDER BY UpdatedAt DESC LIMIT 1";
        using var connection = new SqliteConnection(ConnectionString);
        var json = await connection.QueryFirstOrDefaultAsync<string>(sql);

        if (string.IsNullOrEmpty(json))
            return null;

        return System.Text.Json.JsonSerializer.Deserialize<SyncConfiguration>(json);
    }

    public async Task SaveConfigurationAsync(SyncConfiguration config)
    {
        config.UpdatedAt = DateTime.UtcNow;
        var json = System.Text.Json.JsonSerializer.Serialize(config);

        var sql = @"
            INSERT OR REPLACE INTO Configuration (Id, Json, UpdatedAt)
            VALUES (@Id, @Json, @UpdatedAt)";
        using var connection = new SqliteConnection(ConnectionString);
        await connection.ExecuteAsync(sql, new
        {
            config.Id,
            Json = json,
            UpdatedAt = config.UpdatedAt.ToString("O")
        });
    }

    public async Task<PageMetadata?> GetPageMetadataAsync(string documentId, string pageId)
    {
        var sql = @"
            SELECT * FROM Pages 
            WHERE DocumentId = @DocumentId AND PageId = @PageId";
        using var connection = new SqliteConnection(ConnectionString);
        var result = await connection.QueryFirstOrDefaultAsync<dynamic>(sql, new { DocumentId = documentId, PageId = pageId });

        if (result == null)
            return null;

        return MapToPageMetadata(result);
    }

    public async Task<List<PageMetadata>> GetPendingPagesAsync(int limit = 100)
    {
        var sql = @"
            SELECT * FROM Pages 
            WHERE Status = @Status 
            ORDER BY LastModified DESC 
            LIMIT @Limit";
        using var connection = new SqliteConnection(ConnectionString);
        var results = await connection.QueryAsync<dynamic>(sql, new
        {
            Status = (int)SyncStatus.Pending,
            Limit = limit
        });

        return [.. results.Select(MapToPageMetadata)];
    }

    public async Task<List<PageMetadata>> GetRecentPagesAsync(int limit = 100)
    {
        var sql = @"
            SELECT * FROM Pages 
            ORDER BY LastModified DESC 
            LIMIT @Limit";
        using var connection = new SqliteConnection(ConnectionString);
        var results = await connection.QueryAsync<dynamic>(sql, new { Limit = limit });
        return [.. results.Select(MapToPageMetadata)];
    }

    public async Task<List<PageMetadata>> GetPagesByStatusAsync(SyncStatus status)
    {
        var sql = "SELECT * FROM Pages WHERE Status = @Status";
        using var connection = new SqliteConnection(ConnectionString);
        var results = await connection.QueryAsync<dynamic>(sql, new { Status = (int)status });
        return [.. results.Select(MapToPageMetadata)];
    }

    public async Task SavePageMetadataAsync(PageMetadata metadata)
    {
        var sql = @"
            INSERT OR REPLACE INTO Pages (
                DocumentId, PageId, PageNumber, Title, VirtualPath,
                LocalFilePath, CachedFilePath, FileSizeBytes, LastModified,
                ContentHash, Status, LastSyncTime, OneNotePageId, OneNotePageUrl,
                RetryCount, LastError, Json
            ) VALUES (
                @DocumentId, @PageId, @PageNumber, @Title, @VirtualPath,
                @LocalFilePath, @CachedFilePath, @FileSizeBytes, @LastModified,
                @ContentHash, @Status, @LastSyncTime, @OneNotePageId, @OneNotePageUrl,
                @RetryCount, @LastError, @Json
            )";
        using var connection = new SqliteConnection(ConnectionString);
        await connection.ExecuteAsync(sql, new
        {
            metadata.DocumentId,
            metadata.PageId,
            metadata.PageNumber,
            metadata.Title,
            metadata.VirtualPath,
            metadata.LocalFilePath,
            metadata.CachedFilePath,
            metadata.FileSizeBytes,
            LastModified = metadata.LastModified.ToString("O"),
            metadata.ContentHash,
            Status = (int)metadata.Status,
            LastSyncTime = metadata.LastSyncTime?.ToString("O"),
            metadata.OneNotePageId,
            metadata.OneNotePageUrl,
            metadata.RetryCount,
            metadata.LastError,
            Json = System.Text.Json.JsonSerializer.Serialize(metadata)
        });
    }

    public async Task UpdatePageStatusAsync(string documentId, string pageId, SyncStatus status, string? error = null, string? oneNoteUrl = null)
    {
        var sql = @"
            UPDATE Pages 
            SET Status = @Status, LastError = @Error, LastSyncTime = @SyncTime";

        if (oneNoteUrl != null)
        {
            sql += ", OneNotePageUrl = @OneNoteUrl";
        }

        sql += @"
            WHERE DocumentId = @DocumentId AND PageId = @PageId";
        using var connection = new SqliteConnection(ConnectionString);
        await connection.ExecuteAsync(sql, new
        {
            Status = (int)status,
            Error = error,
            SyncTime = status == SyncStatus.Uploaded ? DateTime.UtcNow.ToString("O") : null,
            OneNoteUrl = oneNoteUrl,
            DocumentId = documentId,
            PageId = pageId
        });
    }

    public async Task<DocumentMetadata?> GetDocumentMetadataAsync(string documentId)
    {
        var sql = "SELECT * FROM Documents WHERE DocumentId = @DocumentId";
        using var connection = new SqliteConnection(ConnectionString);
        var result = await connection.QueryFirstOrDefaultAsync<dynamic>(sql, new { DocumentId = documentId });

        if (result == null)
            return null;
        DocumentMetadata doc;
        var jsonStr = result.Json as string;
        if (!string.IsNullOrEmpty(jsonStr))
        {
            doc = System.Text.Json.JsonSerializer.Deserialize<DocumentMetadata>(jsonStr) ?? new DocumentMetadata();
        }
        else
        {
            doc = new DocumentMetadata();
        }

        doc.DocumentId = result.DocumentId;
        doc.VisibleName = result.VisibleName;
        doc.Type = result.Type;
        doc.Parent = result.Parent ?? "";
        doc.LastModified = DateTime.Parse(result.LastModified);

        // Load pages
        var pages = await GetDocumentPagesAsync(documentId);
        doc.Pages.AddRange(pages);

        return doc;
    }

    private async Task<List<PageMetadata>> GetDocumentPagesAsync(string documentId)
    {
        var sql = "SELECT * FROM Pages WHERE DocumentId = @DocumentId";
        using var connection = new SqliteConnection(ConnectionString);
        var results = await connection.QueryAsync<dynamic>(sql, new { DocumentId = documentId });
        return [.. results.Select(MapToPageMetadata)];
    }

    public async Task<List<DocumentMetadata>> GetAllDocumentsAsync()
    {
        var sql = "SELECT * FROM Documents ORDER BY VisibleName";
        using var connection = new SqliteConnection(ConnectionString);
        var results = await connection.QueryAsync<dynamic>(sql);

        var documents = new List<DocumentMetadata>();
        foreach (var result in results)
        {
            DocumentMetadata doc;
            var jsonStr = result.Json as string;
            if (!string.IsNullOrEmpty(jsonStr))
            {
                doc = System.Text.Json.JsonSerializer.Deserialize<DocumentMetadata>(jsonStr) ?? new DocumentMetadata();
            }
            else
            {
                doc = new DocumentMetadata();
            }

            doc.DocumentId = result.DocumentId;
            doc.VisibleName = result.VisibleName;
            doc.Type = result.Type;
            doc.Parent = result.Parent ?? "";

            // Handle backwards compatibility for VirtualPath
            try { doc.VirtualPath = result.VirtualPath ?? ""; } catch { doc.VirtualPath = ""; }
            doc.LastModified = DateTime.Parse(result.LastModified);

            doc.Pages.AddRange(await GetDocumentPagesAsync(doc.DocumentId));
            documents.Add(doc);
        }

        return documents;
    }

    public async Task SaveDocumentMetadataAsync(DocumentMetadata metadata)
    {
        var sql = @"
            INSERT OR REPLACE INTO Documents (DocumentId, VisibleName, Type, Parent, VirtualPath, LastModified, Json)
            VALUES (@DocumentId, @VisibleName, @Type, @Parent, @VirtualPath, @LastModified, @Json)";
        using var connection = new SqliteConnection(ConnectionString);
        await connection.ExecuteAsync(sql, new
        {
            metadata.DocumentId,
            metadata.VisibleName,
            metadata.Type,
            metadata.Parent,
            metadata.VirtualPath,
            LastModified = metadata.LastModified.ToString("O"),
            Json = System.Text.Json.JsonSerializer.Serialize(metadata)
        });

        // Save pages
        foreach (var page in metadata.Pages)
        {
            await SavePageMetadataAsync(page);
        }
    }

    public async Task UpsertFileTreeAsync(IEnumerable<DocumentMetadata> documents)
    {
        using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();

        try
        {
            var sql = @"
                INSERT INTO Documents (DocumentId, VisibleName, Type, Parent, VirtualPath, LastModified, Json)
                VALUES (@DocumentId, @VisibleName, @Type, @Parent, @VirtualPath, @LastModified, @Json)
                ON CONFLICT(DocumentId) DO UPDATE SET
                    VisibleName = excluded.VisibleName,
                    Type = excluded.Type,
                    Parent = excluded.Parent,
                    VirtualPath = CASE WHEN excluded.VirtualPath != '' THEN excluded.VirtualPath ELSE Documents.VirtualPath END,
                    LastModified = MAX(Documents.LastModified, excluded.LastModified);";

            // Note: We deliberately EXCLUDE `Json` (which holds CustomMetadata aka Sync status) from updates 
            // so we don't wipe out OneNoteUrl, NotebookName configurations, and sync settings on reload.

            foreach (var doc in documents)
            {
                var param = new
                {
                    doc.DocumentId,
                    doc.VisibleName,
                    doc.Type,
                    doc.Parent,
                    doc.VirtualPath,
                    LastModified = doc.LastModified.ToString("O"),
                    Json = "{}" // Only used for new inserts, ignored on update
                };
                await connection.ExecuteAsync(sql, param, transaction);
            }

            await transaction.CommitAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to completely upsert filetree");
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<long> GetCacheSizeAsync()
    {
        if (string.IsNullOrEmpty(_databasePath))
            return 0;

        var cacheDir = Path.Combine(Path.GetDirectoryName(_databasePath)!, "cache");
        if (!Directory.Exists(cacheDir))
            return 0;

        return await Task.Run(() =>
        {
            var dir = new DirectoryInfo(cacheDir);
            return dir.EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);
        });
    }

    public async Task<int> CleanupOldCacheAsync(int daysToKeep)
    {
        var cutoffDate = DateTime.UtcNow.AddDays(-daysToKeep);

        var sql = @"
            DELETE FROM Pages 
            WHERE Status = @Status AND LastSyncTime < @CutoffDate";
        using var connection = new SqliteConnection(ConnectionString);
        var deleted = await connection.ExecuteAsync(sql, new
        {
            Status = (int)SyncStatus.Uploaded,
            CutoffDate = cutoffDate.ToString("O")
        });

        // Also clean up old sync history
        sql = "DELETE FROM SyncHistory WHERE Timestamp < @CutoffDate";
        await connection.ExecuteAsync(sql, new { CutoffDate = cutoffDate.ToString("O") });

        return deleted;
    }

    public async Task ClearCacheAsync()
    {
        using var connection = new SqliteConnection(ConnectionString);
        await connection.ExecuteAsync("DELETE FROM Pages");
        await connection.ExecuteAsync("DELETE FROM Documents");
        await connection.ExecuteAsync("DELETE FROM SyncHistory");

        // Clear cache directory
        if (!string.IsNullOrEmpty(_databasePath))
        {
            var cacheDir = Path.Combine(Path.GetDirectoryName(_databasePath)!, "cache");
            if (Directory.Exists(cacheDir))
            {
                Directory.Delete(cacheDir, true);
                Directory.CreateDirectory(cacheDir);
            }
        }
    }

    public async Task NukeDatabaseAsync()
    {
        _logger.LogWarning("Nuking entire database file at {Path}", _databasePath);

        SqliteConnection.ClearAllPools();
        // Give the OS a moment to release handles after clearing pools
        await Task.Delay(100);
        
        if (!string.IsNullOrEmpty(_databasePath))
        {
            var filesToDelete = new[]
            {
                _databasePath,
                _databasePath + "-wal",
                _databasePath + "-shm"
            };

            foreach (var file in filesToDelete)
            {
                if (File.Exists(file))
                {
                    try
                    {
                        File.Delete(file);
                        _logger.LogDebug("Deleted database component: {File}", file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to delete database component: {File}", file);
                    }
                }
            }

            try
            {
                await InitializeAsync(_databasePath);
                _logger.LogInformation("Database successfully nuked and rebuilt.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to re-initialize database after nuke.");
            }
        }
    }

    public async Task ResetInProgressStatusAsync()
    {
        var sql = "UPDATE Pages SET Status = @PendingStatus WHERE Status IN (@Queued, @Transcoding, @Uploading, @Indexing, @InProgress)";
        using var connection = new SqliteConnection(ConnectionString);
        await connection.ExecuteAsync(sql, new
        {
            PendingStatus = (int)SyncStatus.Pending,
            Queued = (int)SyncStatus.Queued,
            Transcoding = (int)SyncStatus.Transcoding,
            Uploading = (int)SyncStatus.Uploading,
            Indexing = (int)SyncStatus.Indexing,
            InProgress = (int)SyncStatus.InProgress
        });
        _logger.LogInformation("Reset all transient sync items back to 'Pending' for session recovery.");
    }

    public async Task RecordSyncEventAsync(string documentId, string pageId, bool success, string? details = null)
    {
        var sql = @"
            INSERT INTO SyncHistory (Timestamp, DocumentId, PageId, Success, Details)
            VALUES (@Timestamp, @DocumentId, @PageId, @Success, @Details)";
        using var connection = new SqliteConnection(ConnectionString);
        await connection.ExecuteAsync(sql, new
        {
            Timestamp = DateTime.UtcNow.ToString("O"),
            DocumentId = documentId,
            PageId = pageId,
            Success = success ? 1 : 0,
            Details = details
        });
    }

    public async Task<List<SyncEvent>> GetSyncHistoryAsync(int limit = 100)
    {
        var sql = @"
            SELECT * FROM SyncHistory 
            ORDER BY Timestamp DESC 
            LIMIT @Limit";
        using var connection = new SqliteConnection(ConnectionString);
        var results = await connection.QueryAsync<SyncEvent>(sql, new { Limit = limit });
        return [.. results];
    }

    private PageMetadata MapToPageMetadata(dynamic row)
    {
        return new PageMetadata
        {
            DocumentId = row.DocumentId,
            PageId = row.PageId,
            PageNumber = row.PageNumber ?? "",
            Title = row.Title ?? "",
            VirtualPath = row.VirtualPath ?? "",
            LocalFilePath = row.LocalFilePath ?? "",
            CachedFilePath = row.CachedFilePath ?? "",
            FileSizeBytes = row.FileSizeBytes ?? 0,
            LastModified = DateTime.Parse(row.LastModified),
            ContentHash = row.ContentHash ?? "",
            Status = (SyncStatus)Convert.ToInt32(row.Status ?? 0),
            LastSyncTime = row.LastSyncTime != null ? DateTime.Parse(row.LastSyncTime) : null,
            OneNotePageId = row.OneNotePageId,
            OneNotePageUrl = row.OneNotePageUrl,
            RetryCount = Convert.ToInt32(row.RetryCount ?? 0),
            LastError = row.LastError
        };
    }

    public async Task<string?> GetTelemetryAsync(string key)
    {
        var sql = "SELECT Value FROM Telemetry WHERE Key = @Key";
        using var connection = new SqliteConnection(ConnectionString);
        return await connection.QueryFirstOrDefaultAsync<string>(sql, new { Key = key });
    }

    public async Task SaveTelemetryAsync(string key, string value)
    {
        var sql = @"
            INSERT OR REPLACE INTO Telemetry (Key, Value, UpdatedAt)
            VALUES (@Key, @Value, @UpdatedAt)";
        using var connection = new SqliteConnection(ConnectionString);
        await connection.ExecuteAsync(sql, new
        {
            Key = key,
            Value = value,
            UpdatedAt = DateTime.UtcNow.ToString("O")
        });
    }
}