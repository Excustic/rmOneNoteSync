using System;
using System.Collections.Generic;
using System.Linq;

namespace rmOneNoteSyncApp.Models;

/// <summary>
/// Metadata for a reMarkable page/document
/// </summary>
public class PageMetadata
{
    public string DocumentId { get; set; } = "";
    public string PageId { get; set; } = "";
    public string PageNumber { get; set; } = "";
    public string Title { get; set; } = "";
    public string ParentFolder { get; set; } = "";
    public string VirtualPath { get; set; } = "";
    
    // File information
    public string LocalFilePath { get; set; } = "";
    public string CachedFilePath { get; set; } = "";
    public long FileSizeBytes { get; set; }
    public DateTime LastModified { get; set; }
    public string ContentHash { get; set; } = "";
    
    // Sync information
    public SyncStatus Status { get; set; } = SyncStatus.Pending;
    public DateTime? LastSyncTime { get; set; }
    public string? OneNotePageId { get; set; }
    public string? OneNotePageUrl { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
    
    // Conversion information
    public bool IsConverted { get; set; }
    public string? ConvertedFormat { get; set; }
    public string? ConvertedFilePath { get; set; }
}

public enum SyncStatus
{
    Pending,
    InProgress,
    Uploaded,
    Failed,
    Skipped,
    Deleted
}

public class DocumentMetadata
{
    public string DocumentId { get; set; } = "";
    public string VisibleName { get; set; } = "";
    public string Type { get; set; } = "DocumentType";
    public string Parent { get; set; } = "";
    public string VirtualPath { get; set; } = "";
    public DateTime LastModified { get; set; }
    public List<PageMetadata> Pages { get; set; } = new();
    public Dictionary<string, object> CustomMetadata { get; set; } = new();
    public string? OneNoteUrl => CustomMetadata.TryGetValue("OneNoteUrl", out var url) ? url?.ToString() : null;
    
    public string NotebookName
    {
        get
        {
            var virtualPath = Pages.FirstOrDefault()?.VirtualPath;
            if (string.IsNullOrEmpty(virtualPath)) return VisibleName;
            
            var parts = virtualPath.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 1) return "rm_Uncategorized";
            if (parts.Length == 2) return $"rm_{SanitizeName(parts[0])}";
            
            var notebookParts = parts.Take(parts.Length - 2).ToList();
            return "rm_" + string.Join("_", notebookParts.Select(SanitizeName));
        }
    }
    
    private string SanitizeName(string name)
    {
        return name.Replace("/", "_").Replace("\\", "_").Replace(":", "_").Replace("*", "_")
                   .Replace("?", "_").Replace("\"", "_").Replace("<", "_").Replace(">", "_").Replace("|", "_");
    }
}