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

            // Grab the exact string from the csproj (ignoring the .NET commit hash)
            string versionInfo = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            string currentVersion = versionInfo.Split('+')[0];

            bool isUpdateAvailable = latestTag != currentVersion;

            return (isUpdateAvailable, release.tag_name, release.html_url);
        }
        catch
        {
            return (false, "", "");
        }
    }

    private class GitHubRelease
    {
        public string tag_name { get; set; }
        public string html_url { get; set; }
    }
}