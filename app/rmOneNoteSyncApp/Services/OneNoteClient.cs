using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Identity.Client;
using Microsoft.Kiota.Abstractions;
using Microsoft.Kiota.Abstractions.Authentication;
using rmOneNoteSyncApp.Services.Interfaces;

namespace rmOneNoteSyncApp.Services;

public interface IOneNoteClient
{
    Task<bool> IsAuthenticatedAsync();
    Task<List<Notebook>> GetNotebooksAsync();
    Task<Notebook> CreateNotebookAsync(string displayName);
    Task<List<Section>> GetSectionsAsync(string notebookId);
    Task<Section> CreateSectionAsync(string notebookId, string displayName);
    Task<List<OneNotePage>> GetPagesAsync(string sectionId);
    Task<OneNotePage> CreatePageAsync(string sectionId, string title, string htmlContent);
    Task<OneNotePage> UpdatePageAsync(string pageId, string htmlContent);
    Task<string> UploadInkMLPageAsync(string sectionId, string title, byte[] inkmlData, byte[] htmlData, Dictionary<string, string> metadata);
    Task<bool> DeletePageAsync(string pageId);
    Task<OneNotePage> GetPageAsync(string pageId);
    Task<Stream> GetPageContentAsync(string pageId);
}

public class OneNoteClient : IOneNoteClient
{
    private readonly ILogger<OneNoteClient> _logger;
    private readonly IOneNoteAuthService _authService;
    private GraphServiceClient? _graphClient;
    private readonly HttpClient _httpClient;

    // Graph API endpoints
    private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";
    private const string OneNoteBaseUrl = "https://graph.microsoft.com/v1.0/me/onenote";
    private readonly Random _jitterer = new();

    public OneNoteClient(
        ILogger<OneNoteClient> logger,
        IOneNoteAuthService authService)
    {
        _logger = logger;
        _authService = authService;
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(2)
        };
    }

    public async Task<bool> IsAuthenticatedAsync()
    {
        var token = await _authService.GetAccessTokenAsync();
        return !string.IsNullOrEmpty(token);
    }

    private async Task<GraphServiceClient> GetGraphClientAsync()
    {
        if (_graphClient == null)
        {
            var token = await _authService.GetAccessTokenAsync();
            if (string.IsNullOrEmpty(token))
            {
                throw new InvalidOperationException("Not authenticated with OneNote");
            }

            _graphClient = new GraphServiceClient(new HttpClient(),
                new DelegateAuthenticationProvider(async (request) =>
                {
                    request.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                }), GraphBaseUrl);
        }

        return _graphClient;
    }

    private async Task<HttpRequestMessage> CreateAuthenticatedRequestAsync(
        HttpMethod method, string url)
    {
        var token = await _authService.GetAccessTokenAsync();
        if (string.IsNullOrEmpty(token))
        {
            throw new InvalidOperationException("Not authenticated");
        }

        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        return request;
    }

    public async Task<List<Notebook>> GetNotebooksAsync()
    {
        try
        {
            _logger.LogDebug("Fetching OneNote notebooks");

            var request = await CreateAuthenticatedRequestAsync(
                HttpMethod.Get,
                $"{OneNoteBaseUrl}/notebooks?$expand=sections");

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) await ThrowDetailedErrorAsync(response);

            var json = await response.Content.ReadAsStringAsync();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var result = JsonSerializer.Deserialize<ODataResponse<Notebook>>(json, options);

            _logger.LogDebug("Found {Count} notebooks", result?.Value?.Count ?? 0);
            return result?.Value ?? new List<Notebook>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get notebooks");
            throw;
        }
    }

    public async Task<Notebook> CreateNotebookAsync(string displayName)
    {
        try
        {
            _logger.LogDebug("Creating notebook: {Name}", displayName);

            var request = await CreateAuthenticatedRequestAsync(
                HttpMethod.Post,
                $"{OneNoteBaseUrl}/notebooks");

            var body = new { displayName };
            request.Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) await ThrowDetailedErrorAsync(response);

            var json = await response.Content.ReadAsStringAsync();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var notebook = JsonSerializer.Deserialize<Notebook>(json, options);

            _logger.LogDebug("Created notebook with ID: {Id}", notebook?.Id);
            return notebook!;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create notebook");
            throw;
        }
    }

    public async Task<List<Section>> GetSectionsAsync(string notebookId)
    {
        try
        {
            _logger.LogDebug("Fetching sections for notebook: {Id}", notebookId);

            var request = await CreateAuthenticatedRequestAsync(
                HttpMethod.Get,
                $"{OneNoteBaseUrl}/notebooks/{notebookId}/sections");

            var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var result = JsonSerializer.Deserialize<ODataResponse<Section>>(json, options);

            return result?.Value ?? new List<Section>();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get sections");
            throw;
        }
    }

    public async Task<Section> CreateSectionAsync(string notebookId, string displayName)
    {
        try
        {
            _logger.LogDebug("Creating section '{Name}' in notebook {Id}",
                displayName, notebookId);

            var request = await CreateAuthenticatedRequestAsync(
                HttpMethod.Post,
                $"{OneNoteBaseUrl}/notebooks/{notebookId}/sections");

            var body = new { displayName };
            request.Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) await ThrowDetailedErrorAsync(response);

            var json = await response.Content.ReadAsStringAsync();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var section = JsonSerializer.Deserialize<Section>(json, options);

            _logger.LogDebug("Created section with ID: {Id}", section?.Id);
            return section!;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create section");
            throw;
        }
    }

    public async Task<List<OneNotePage>> GetPagesAsync(string sectionId)
    {
        int maxRetries = 3;
        for (int retry = 0; retry < maxRetries; retry++)
        {
            try
            {
                _logger.LogDebug("Fetching pages for section: {Id} (Attempt {Attempt}/{Max})", 
                    sectionId, retry + 1, maxRetries);

                var request = await CreateAuthenticatedRequestAsync(
                    HttpMethod.Get,
                    $"{OneNoteBaseUrl}/sections/{sectionId}/pages?$select=id,title,createdDateTime,lastModifiedDateTime,contentUrl,links");

                var response = await _httpClient.SendAsync(request);
                
                if (!response.IsSuccessStatusCode)
                {
                    bool isRetryable = response.StatusCode == System.Net.HttpStatusCode.InternalServerError 
                                    || response.StatusCode == (System.Net.HttpStatusCode)429
                                    || response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable;

                    if (retry < maxRetries - 1 && isRetryable)
                    {
                        TimeSpan delay = TimeSpan.FromSeconds(Math.Pow(2, retry) + _jitterer.NextDouble() * 2);
                        _logger.LogWarning("Graph API error ({Status}) fetching pages. Retrying in {Delay}s...", response.StatusCode, delay.TotalSeconds);
                        await Task.Delay(delay);
                        continue;
                    }
                    await ThrowDetailedErrorAsync(response);
                }

                var json = await response.Content.ReadAsStringAsync();
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var result = JsonSerializer.Deserialize<ODataResponse<OneNotePage>>(json, options);

                return result?.Value ?? new List<OneNotePage>();
            }
            catch (Exception ex)
            {
                if (retry == maxRetries - 1)
                {
                    _logger.LogError(ex, "Failed to get pages for section {Id} after {Count} attempts", sectionId, retry + 1);
                    throw;
                }
                
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }
        throw new Exception("Unreachable code reached during page listing");
    }

    public async Task<OneNotePage> CreatePageAsync(
        string sectionId, string title, string htmlContent)
    {
        try
        {
            _logger.LogDebug("Creating page '{Title}' in section {Id}",
                title, sectionId);

            var request = await CreateAuthenticatedRequestAsync(
                HttpMethod.Post,
                $"{OneNoteBaseUrl}/sections/{sectionId}/pages");

            // OneNote expects HTML content with specific structure
            var fullHtml = WrapInOneNoteHtml(title, htmlContent);

            request.Content = new StringContent(fullHtml, Encoding.UTF8, "text/html");

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) await ThrowDetailedErrorAsync(response);

            var json = await response.Content.ReadAsStringAsync();
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var page = JsonSerializer.Deserialize<OneNotePage>(json, options);

            _logger.LogDebug("Created page with ID: {Id}", page?.Id);
            return page!;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create page");
            throw;
        }
    }

    public async Task<string> UploadInkMLPageAsync(
        string sectionId,
        string title,
        byte[] inkmlData,
        byte[] htmlData,
        Dictionary<string, string> metadata)
    {
        int maxRetries = 5;
        for (int retry = 0; retry < maxRetries; retry++)
        {
            try
            {
                _logger.LogDebug("Uploading InkML page '{Title}' to section {Id} (Attempt {Attempt}/{Max})",
                    title, sectionId, retry + 1, maxRetries);

                var request = await CreateAuthenticatedRequestAsync(
                    HttpMethod.Post,
                    $"{OneNoteBaseUrl}/sections/{sectionId}/pages");

                // Create multipart content for InkML upload
                using var content = new MultipartFormDataContent();

                // Add the InkML data part first
                var inkmlContent = new ByteArrayContent(inkmlData);
                inkmlContent.Headers.ContentType = new MediaTypeHeaderValue("application/inkml+xml");
                content.Add(inkmlContent, "presentation-onenote-inkml", "drawing.xml");

                // Inject <title> tag into the HTML so OneNote doesn't default to "Untitled Page"
                var htmlString = Encoding.UTF8.GetString(htmlData);
                if (htmlString.Contains("<head>", StringComparison.OrdinalIgnoreCase))
                {
                    htmlString = htmlString.Replace("<head>", $"<head>\n    <title>{title}</title>", StringComparison.OrdinalIgnoreCase);
                }
                else if (htmlString.Contains("<html>", StringComparison.OrdinalIgnoreCase))
                {
                    htmlString = htmlString.Replace("<html>", $"<html>\n  <head>\n    <title>{title}</title>\n  </head>", StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    // Fallback if rmc outputs no head/html blocks
                    htmlString = $"<html>\n  <head>\n    <title>{title}</title>\n  </head>\n  <body>\n{htmlString}\n  </body>\n</html>";
                }

                var modifiedHtmlData = Encoding.UTF8.GetBytes(htmlString);

                // Add the presentation part (HTML that references the InkML)
                var htmlContent = new ByteArrayContent(modifiedHtmlData);
                htmlContent.Headers.ContentType = new MediaTypeHeaderValue("text/html");
                content.Add(htmlContent, "presentation", "presentation.html");

                request.Content = content;

                var response = await _httpClient.SendAsync(request);
                
                if (!response.IsSuccessStatusCode)
                {
                    // Check if error is retryable
                    bool isRetryable = response.StatusCode == System.Net.HttpStatusCode.InternalServerError 
                                    || response.StatusCode == (System.Net.HttpStatusCode)429 // TooManyRequests
                                    || response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable
                                    || response.StatusCode == System.Net.HttpStatusCode.GatewayTimeout;

                    if (retry < maxRetries - 1 && isRetryable)
                    {
                        TimeSpan delay = TimeSpan.FromSeconds(Math.Pow(2, retry) + _jitterer.NextDouble() * 3);
                        
                        // Respect Retry-After header if present
                        if (response.Headers.RetryAfter != null)
                        {
                            if (response.Headers.RetryAfter.Delta.HasValue)
                                delay = response.Headers.RetryAfter.Delta.Value;
                            else if (response.Headers.RetryAfter.Date.HasValue)
                                delay = response.Headers.RetryAfter.Date.Value - DateTimeOffset.Now;
                        }

                        _logger.LogWarning("Graph API error ({Status}) on upload. Retrying in {Delay}s...", response.StatusCode, delay.TotalSeconds);
                        await Task.Delay(delay);
                        continue;
                    }

                    await ThrowDetailedErrorAsync(response);
                }

                // Extract page ID from response headers or body
                var location = response.Headers.Location?.ToString() ?? "";
                var pageId = ExtractPageIdFromLocation(location);

                _logger.LogInformation("Uploaded InkML page with ID: {Id}", pageId);
                return pageId;
            }
            catch (Exception ex)
            {
                bool isNetworkOrTimeout = ex is HttpRequestException
                    || ex is TaskCanceledException
                    || ex.Message.Contains("GatewayTimeout");

                if (retry < maxRetries - 1 && isNetworkOrTimeout)
                {
                    TimeSpan delay = TimeSpan.FromSeconds(Math.Pow(2, retry) + _jitterer.NextDouble() * 3);
                    _logger.LogWarning(ex, "Failed to upload page (network/timeout), retrying in {Delay}s...", delay.TotalSeconds);
                    await Task.Delay(delay);
                    continue;
                }

                _logger.LogError(ex, "Failed to upload InkML page after {Count} attempts", retry + 1);
                throw;
            }
        }

        throw new Exception("Unreachable code reached during page upload");
    }

    public async Task<OneNotePage> UpdatePageAsync(string pageId, string htmlContent)
    {
        try
        {
            _logger.LogDebug("Updating page {Id}", pageId);

            // OneNote uses PATCH with specific JSON format for updates
            var request = await CreateAuthenticatedRequestAsync(
                HttpMethod.Patch,
                $"{OneNoteBaseUrl}/pages/{pageId}/content");

            var patchContent = new[]
            {
                new
                {
                    target = "body",
                    action = "replace",
                    content = htmlContent
                }
            };

            request.Content = new StringContent(
                JsonSerializer.Serialize(patchContent),
                Encoding.UTF8,
                "application/json");

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) await ThrowDetailedErrorAsync(response);

            _logger.LogDebug("Updated page {Id}", pageId);

            // Fetch and return updated page
            return await GetPageAsync(pageId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update page");
            throw;
        }
    }

    public async Task<bool> DeletePageAsync(string pageId)
    {
        try
        {
            _logger.LogDebug("Deleting page {Id}", pageId);

            var request = await CreateAuthenticatedRequestAsync(
                HttpMethod.Delete,
                $"{OneNoteBaseUrl}/pages/{pageId}");

            var response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Deleted page {Id}", pageId);
                return true;
            }

            _logger.LogWarning("Failed to delete page {Id}: {Status}",
                pageId, response.StatusCode);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete page");
            return false;
        }
    }

    public async Task<OneNotePage> GetPageAsync(string pageId)
    {
        int maxRetries = 6;
        int[] delays = { 2, 4, 8, 15, 20, 25 }; // Incremental delays to cover ~60s+ indexing time
        
        for (int retry = 0; retry < maxRetries; retry++)
        {
            try
            {
                var request = await CreateAuthenticatedRequestAsync(
                    HttpMethod.Get,
                    $"{OneNoteBaseUrl}/pages/{pageId}");

                var response = await _httpClient.SendAsync(request);
                
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound && retry < maxRetries - 1)
                {
                    // Eventual consistency — page might be uploaded but not yet available for GET.
                    int delay = delays[retry];
                    _logger.LogWarning("Page {Id} not found yet (eventual consistency). Attempt {Attempt}/{Max}. Retrying in {Delay}s...", 
                        pageId, retry + 1, maxRetries, delay);
                    await Task.Delay(TimeSpan.FromSeconds(delay));
                    continue;
                }

                if (!response.IsSuccessStatusCode) await ThrowDetailedErrorAsync(response);

                var json = await response.Content.ReadAsStringAsync();
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<OneNotePage>(json, options)!;
            }
            catch (Exception ex)
            {
                if (retry == maxRetries - 1)
                {
                    _logger.LogError(ex, "Failed to get page {Id} after {Count} attempts", pageId, retry + 1);
                    throw;
                }
                
                _logger.LogWarning(ex, "Transient error getting page {Id}. Retrying...", pageId);
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
        }
        
        throw new Exception("Unreachable code reached during page retrieval");
    }

    public async Task<Stream> GetPageContentAsync(string pageId)
    {
        try
        {
            var request = await CreateAuthenticatedRequestAsync(
                HttpMethod.Get,
                $"{OneNoteBaseUrl}/pages/{pageId}/content");

            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) await ThrowDetailedErrorAsync(response);

            return await response.Content.ReadAsStreamAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get page content for {Id}", pageId);
            throw;
        }
    }

    // Helper method to wrap content in OneNote HTML structure
    private string WrapInOneNoteHtml(string title, string bodyContent)
    {
        return $@"
<!DOCTYPE html>
<html>
<head>
    <title>{System.Web.HttpUtility.HtmlEncode(title)}</title>
    <meta name='created' content='{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}' />
</head>
<body data-absolute-enabled='true' style='font-family:Calibri;font-size:11pt'>
    {bodyContent}
</body>
</html>";
    }

    // Helper method to create HTML presentation for InkML
    private string CreateInkMLPresentationHtml(string title, Dictionary<string, string> metadata)
    {
        var metadataHtml = string.Join("\n",
            metadata.Select(kvp =>
                $"<p><b>{System.Web.HttpUtility.HtmlEncode(kvp.Key)}:</b> " +
                $"{System.Web.HttpUtility.HtmlEncode(kvp.Value)}</p>"));

        return $@"
<!DOCTYPE html>
<html>
<head>
    <title>{System.Web.HttpUtility.HtmlEncode(title)}</title>
    <meta name='created' content='{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ss.fffZ}' />
</head>
<body data-absolute-enabled='true'>
    <h1>{System.Web.HttpUtility.HtmlEncode(title)}</h1>
    {metadataHtml}
    <object data-attachment='drawing.xml' type='application/inkml+xml' />
</body>
</html>";
    }

    private string ExtractPageIdFromLocation(string location)
    {
        // Extract page ID from location header
        // Format: https://graph.microsoft.com/v1.0/users/{user}/onenote/pages/{pageId}
        var parts = location.Split('/');
        return parts.Length > 0 ? parts[^1] : "";
    }

    private async Task ThrowDetailedErrorAsync(HttpResponseMessage response)
    {
        var errorContent = await response.Content.ReadAsStringAsync();
        throw new HttpRequestException($"Graph API Error ({response.StatusCode}): {errorContent}");
    }
}

// Supporting models for Graph API responses
public class ODataResponse<T>
{
    public List<T>? Value { get; set; }
}

public class Notebook
{
    public string? Id { get; set; }
    public string? DisplayName { get; set; }
    public DateTime CreatedDateTime { get; set; }
    public DateTime LastModifiedDateTime { get; set; }
    public string? Self { get; set; }
    public OneNoteLinks? Links { get; set; }
    public List<Section>? Sections { get; set; }
}

public class Section
{
    public string? Id { get; set; }
    public string? DisplayName { get; set; }
    public DateTime CreatedDateTime { get; set; }
    public DateTime LastModifiedDateTime { get; set; }
    public Notebook? ParentNotebook { get; set; }
    public OneNoteLinks? Links { get; set; }
}

public class OneNotePage
{
    public string? Id { get; set; }
    public string? Title { get; set; }
    public DateTime CreatedDateTime { get; set; }
    public DateTime LastModifiedDateTime { get; set; }
    public string? ContentUrl { get; set; }
    public Section? ParentSection { get; set; }
    public OneNoteLinks? Links { get; set; }
}

public class OneNoteLinks
{
    public OneNoteUrl? OneNoteClientUrl { get; set; }
    public OneNoteUrl? OneNoteWebUrl { get; set; }
}

public class OneNoteUrl
{
    public string? Href { get; set; }
}

// Custom authentication provider for Graph SDK
public class DelegateAuthenticationProvider : IAuthenticationProvider
{
    private readonly Func<HttpRequestMessage, Task> _authenticationDelegate;

    public DelegateAuthenticationProvider(Func<HttpRequestMessage, Task> authenticationDelegate)
    {
        _authenticationDelegate = authenticationDelegate;
    }

    public async Task AuthenticateRequestAsync(HttpRequestMessage request)
    {
        await _authenticationDelegate(request);
    }

    public Task AuthenticateRequestAsync(RequestInformation request, Dictionary<string, object>? additionalAuthenticationContext = null,
        CancellationToken cancellationToken = new CancellationToken())
    {
        throw new NotImplementedException();
    }
}