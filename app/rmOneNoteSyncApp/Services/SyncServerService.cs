using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using rmOneNoteSyncApp.Models;
using rmOneNoteSyncApp.Services.Interfaces;

namespace rmOneNoteSyncApp.Services;

public class SyncServerService : ISyncServerService
{
    private readonly ILogger<SyncServerService> _logger;
    private readonly IConfigurationProviderService _configProvider;
    private readonly IDatabaseService _databaseService;
    private HttpListener? _listener;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _serverTask;
    private readonly string _uploadDirectory;

    public bool IsRunning => _listener?.IsListening ?? false;
    public event EventHandler<bool>? StatusChanged;
    public event EventHandler<FileReceivedEventArgs>? FileReceived;

    public SyncServerService(
        ILogger<SyncServerService> logger,
        IConfigurationProviderService configProvider,
        IDatabaseService databaseService)
    {
        _logger = logger;
        _configProvider = configProvider;
        _databaseService = databaseService;

        // Create upload directory
        _uploadDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "rmOneNoteSyncApp",
            "uploads");

        Directory.CreateDirectory(_uploadDirectory);
    }

    public async Task StartAsync()
    {
        if (IsRunning)
        {
            _logger.LogWarning("Server is already running");
            return;
        }

        int port = await _configProvider.GetServerPortAsync();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            await EnsureWindowsFirewallRuleAsync(port);
        }

        try
        {
            _listener = new HttpListener();
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add($"http://+:{port}/");

            // Try to start the listener
            _listener.Start();

            _cancellationTokenSource = new CancellationTokenSource();
            _serverTask = Task.Run(() => ServerLoop(_cancellationTokenSource.Token));

            _logger.LogCritical("Sync server started on port {Port}", port);
            StatusChanged?.Invoke(this, true);
        }
        catch (HttpListenerException ex)
        {
            _logger.LogError(ex, "Failed to start HTTP server. May need admin privileges or port {Port} is in use", port);
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (!IsRunning)
            return;

        _logger.LogDebug("Stopping sync server");

        _cancellationTokenSource?.Cancel();
        _listener?.Stop();

        if (_serverTask != null)
        {
            await _serverTask;
        }

        _listener?.Close();
        _listener = null;

        _logger.LogCritical("Sync server stopped");
        StatusChanged?.Invoke(this, false);
    }

    private async Task ServerLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener != null && _listener.IsListening)
        {
            try
            {
                // Get context asynchronously
                var contextTask = _listener.GetContextAsync();
                var completedTask = await Task.WhenAny(
                    contextTask,
                    Task.Delay(Timeout.Infinite, cancellationToken));

                if (completedTask == contextTask && !cancellationToken.IsCancellationRequested)
                {
                    var context = await contextTask;

                    // Process request in background
                    _ = Task.Run(async () => await ProcessRequestAsync(context), cancellationToken);
                }
            }
            catch (ObjectDisposedException)
            {
                // Listener was closed
                break;
            }
            catch (HttpListenerException ex)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "HTTP listener error");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in server loop");
            }
        }
    }

    private async Task ProcessRequestAsync(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var response = context.Response;

            _logger.LogDebug("Received {Method} request to {Path}",
                request.HttpMethod, request.Url?.AbsolutePath);

            // Route the request
            switch (request.Url?.AbsolutePath)
            {
                case "/config":
                    await HandleConfigRequest(request, response);
                    break;

                case "/upload":
                    await HandleUploadRequest(request, response);
                    break;

                case "/health":
                    await HandleHealthRequest(response);
                    break;

                default:
                    response.StatusCode = 404;
                    await WriteJsonResponse(response, new { error = "Not found" });
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing request");

            try
            {
                context.Response.StatusCode = 500;
                await WriteJsonResponse(context.Response, new { error = ex.Message });
            }
            catch { }
        }
        finally
        {
            context.Response.Close();
        }
    }

    private async Task HandleConfigRequest(HttpListenerRequest request, HttpListenerResponse response)
    {
        // Extract device ID from query string
        var deviceId = request.QueryString["device_id"] ?? "unknown";
        var userAgent = request.UserAgent;

        _logger.LogDebug("Config request from device: {DeviceId} using {UserAgent}", deviceId, userAgent ?? "Unknown");

        if (string.IsNullOrEmpty(userAgent) || !userAgent.Contains("RemarkableSyncClient/1.2"))
        {
            _logger.LogWarning("Rejecting config request from incompatible client: {UserAgent}", userAgent ?? "Unknown");
            response.StatusCode = 426; // Upgrade Required
            await WriteJsonResponse(response, new { error = "Client protocol mismatch. Expected RemarkableSyncClient/1.2, please update your reMarkable client." });
            return;
        }

        // Get configuration JSON
        var configJson = await _configProvider.GetConfigurationJsonAsync(deviceId);

        // Send response
        response.ContentType = "application/json";
        response.StatusCode = 200;

        var buffer = Encoding.UTF8.GetBytes(configJson);
        await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
    }

    private async Task HandleUploadRequest(HttpListenerRequest request, HttpListenerResponse response)
    {
        if (request.HttpMethod != "POST")
        {
            response.StatusCode = 405;
            await WriteJsonResponse(response, new { error = "Method not allowed" });
            return;
        }

        // Extract headers
        var apiKey = request.Headers["X-API-Key"];
        var documentPath = request.Headers["X-Document-Path"] ?? "Unknown";
        var filename = request.Headers["X-Filename"] ?? "unknown.rm";
        var userAgent = request.UserAgent;

        // Validate Protocol Version
        if (string.IsNullOrEmpty(userAgent) || !userAgent.Contains("RemarkableSyncClient/1.2"))
        {
            _logger.LogWarning("Rejecting upload from incompatible client: {UserAgent}", userAgent ?? "Unknown");
            response.StatusCode = 426; // Upgrade Required
            await WriteJsonResponse(response, new { error = "Client protocol mismatch. Expected RemarkableSyncClient/1.2, please update your reMarkable client." });
            return;
        }

        // Validate API key (basic check)
        if (string.IsNullOrEmpty(apiKey) || !apiKey.StartsWith("rmOneNoteSync"))
        {
            response.StatusCode = 401;
            await WriteJsonResponse(response, new { error = "Invalid API key" });
            return;
        }

        // Parse document and page IDs from filename
        // Format: documentId/pageId.rm
        var parts = filename.Replace(".rm", "").Split('/');
        var pageId = parts.Length > 0 ? parts[^1] : Guid.NewGuid().ToString();

        // Parse virtual path to extract notebook/section/page structure
        var (notebook, section, pageName) = ParseVirtualPath(documentPath);

        // Extract DocumentId either from header, filename structure, or fallback to the parsed notebook logical name!
        var documentId = request.Headers["X-Document-Id"];

        if (string.IsNullOrEmpty(documentId))
        {
            documentId = parts.Length > 1 ? parts[^2] : notebook;
        }

        // Filter against user's whitelist early
        var config = await _databaseService.GetConfigurationAsync();
        var isWhiteListed = config?.SyncFiles.Contains(documentId) ?? false;

        if (!isWhiteListed)
        {
            _logger.LogDebug("Dropping unwhitelisted file transmission: {Path}", documentPath);
            response.StatusCode = 200;
            await WriteJsonResponse(response, new
            {
                status = "success",
                message = "File uploaded successfully",
                document_id = documentId,
                page_id = pageId,
                notebook = notebook,
                section = section,
                page = pageName,
                size = 0,
                timestamp = DateTime.UtcNow.ToString("O")
            });
            // Can be useful later - need to update the client to handle this response
            // await WriteJsonResponse(response, new
            // {
            //     status = "skipped",
            //     message = "File is not tracked in the current Sync Configuration",
            //     document_id = documentId
            // });
            return;
        }

        _logger.LogInformation("Receiving file {Path} corresponding to notebook '{Notebook}', section '{Section}' (DocId: {DocId})",
            documentPath, notebook, section, documentId);

        // Create directory structure
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var saveDir = Path.Combine(_uploadDirectory, documentId);
        Directory.CreateDirectory(saveDir);

        var localPath = Path.Combine(saveDir, $"{pageId}_{timestamp}.rm");

        // Save file
        using (var fileStream = File.Create(localPath))
        {
            await request.InputStream.CopyToAsync(fileStream);
        }

        var fileInfo = new FileInfo(localPath);

        _logger.LogDebug("Saved file: {Path} ({Size} bytes)", localPath, fileInfo.Length);

        // Save to database

        // Ensure the Document exists before inserting the Page to respect SQLite Foreign Key constraints
        var existingDoc = await _databaseService.GetDocumentMetadataAsync(documentId);
        if (existingDoc == null || string.IsNullOrEmpty(existingDoc.DocumentId))
        {
            await _databaseService.SaveDocumentMetadataAsync(new DocumentMetadata
            {
                DocumentId = documentId,
                VisibleName = section,
                Type = "CollectionType",
                Parent = "",
                LastModified = DateTime.UtcNow
            });
        }
        else
        {
            existingDoc.LastModified = DateTime.UtcNow;
            await _databaseService.SaveDocumentMetadataAsync(existingDoc);
        }

        var metadata = new PageMetadata
        {
            DocumentId = documentId,
            PageId = pageId,
            Title = pageName,
            VirtualPath = documentPath,
            LocalFilePath = localPath,
            FileSizeBytes = fileInfo.Length,
            LastModified = DateTime.UtcNow,
            Status = SyncStatus.Pending,
            PageNumber = ExtractPageNumber(pageName)
        };

        await _databaseService.SavePageMetadataAsync(metadata);

        // Raise event
        FileReceived?.Invoke(this, new FileReceivedEventArgs
        {
            DocumentId = documentId,
            PageId = pageId,
            VirtualPath = documentPath,
            LocalPath = localPath,
            FileSize = fileInfo.Length,
            ReceivedAt = DateTime.UtcNow
        });

        // Send success response
        response.StatusCode = 200;
        await WriteJsonResponse(response, new
        {
            status = "success",
            message = "File uploaded successfully",
            document_id = documentId,
            page_id = pageId,
            notebook = notebook,
            section = section,
            page = pageName,
            size = fileInfo.Length,
            timestamp = DateTime.UtcNow.ToString("O")
        });
    }

    private async Task HandleHealthRequest(HttpListenerResponse response)
    {
        response.StatusCode = 200;
        await WriteJsonResponse(response, new
        {
            status = "healthy",
            timestamp = DateTime.UtcNow.ToString("O"),
            upload_directory = _uploadDirectory
        });
    }

    private async Task WriteJsonResponse(HttpListenerResponse response, object data)
    {
        response.ContentType = "application/json; charset=utf-8";

        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var buffer = Encoding.UTF8.GetBytes(json);
        await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
    }

    private (string notebook, string section, string page) ParseVirtualPath(string virtualPath)
    {
        // Example: Academy/YearOne/BA-Phys/Math Prep I/Page 3
        // Result: notebook="rm_Academy_YearOne_BA-Phys", section="Math Prep I", page="Page 3"

        var parts = virtualPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
        {
            return ("rm_Uncategorized", "Default", "Untitled");
        }

        if (parts.Length == 1)
        {
            // Just a page name
            return ("rm_Uncategorized", "Default", parts[0]);
        }

        if (parts.Length == 2)
        {
            // Section/Page
            return ($"rm_{SanitizeName(parts[0])}", parts[0], parts[1]);
        }

        // Multiple levels - combine all except last two into notebook name
        var notebookParts = parts.Take(parts.Length - 2).ToList();
        var section = parts[parts.Length - 2];
        var page = parts[parts.Length - 1];

        // Add rm_ prefix and join with underscores
        var notebook = "rm_" + string.Join("_", notebookParts.Select(SanitizeName));

        return (notebook, section, page);
    }

    private string SanitizeName(string name)
    {
        // Remove invalid characters for OneNote names
        return name.Replace("/", "_")
                   .Replace("\\", "_")
                   .Replace(":", "_")
                   .Replace("*", "_")
                   .Replace("?", "_")
                   .Replace("\"", "_")
                   .Replace("<", "_")
                   .Replace(">", "_")
                   .Replace("|", "_");
    }

    private string ExtractPageNumber(string pageName)
    {
        // Extract page number from names like "Page 3" or "page_3"
        var match = System.Text.RegularExpressions.Regex.Match(
            pageName, @"(?:page|p)\s*[_\s]?\s*(\d+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        return match.Success ? match.Groups[1].Value : "1";
    }

    private async Task EnsureWindowsFirewallRuleAsync(int port)
    {
        try
        {
            var checkCmd = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = "advfirewall firewall show rule name=\"rmOneNoteSync\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(checkCmd);
            if (proc == null) return;
            string output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (output.Contains("No rules match"))
            {
                _logger.LogInformation("Windows Firewall rule not found. Prompting to add rule.");
                var addCmd = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = $"advfirewall firewall add rule name=\"rmOneNoteSync\" dir=in action=allow protocol=TCP localport={port} profile=any",
                    UseShellExecute = true,
                    Verb = "runas"
                };
                using var addProc = Process.Start(addCmd);
                if (addProc != null)
                {
                    await addProc.WaitForExitAsync();
                    _logger.LogInformation("Windows Firewall rule addition finished.");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to ensure Windows Firewall rule");
        }
    }
}
