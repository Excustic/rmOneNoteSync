using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Threading.Tasks;
using rmOneNoteSyncApp.Services.Interfaces;

namespace rmOneNoteSyncApp.Services;

public class SoftwareUpdateService : ISoftwareUpdateService
{
    private const string RepoApiUrl = "https://api.github.com/repos/Excustic/rmOneNoteSync/releases/latest";
    private readonly HttpClient _httpClient;

    public SoftwareUpdateService()
    {
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "rmOneNoteSyncApp-Updater");
    }

    public async Task<(bool UpdateAvailable, string LatestVersion, string ReleaseUrl)> CheckForUpdatesAsync()
    {
        try
        {
            var release = await _httpClient.GetFromJsonAsync<GitHubRelease>(RepoApiUrl);
            if (release == null || string.IsNullOrEmpty(release.tag_name)) return (false, "", "");

            string latestTag = release.tag_name.Replace("v", "");
            string currentVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

            // Basic string comparison (You can use Version.TryParse for more robust comparison)
            bool isUpdateAvailable = latestTag != currentVersion && latestTag != "0.0.0-PLACEHOLDER";

            return (isUpdateAvailable, release.tag_name, release.html_url);
        }
        catch
        {
            return (false, "", ""); // Fail silently if offline or API rate limited
        }
    }

    private class GitHubRelease
    {
        public string tag_name { get; set; }
        public string html_url { get; set; }
    }
}