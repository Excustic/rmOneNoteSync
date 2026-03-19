using System.Threading.Tasks;

namespace rmOneNoteSyncApp.Services.Interfaces;

public interface ISoftwareUpdateService
{
    Task<(bool UpdateAvailable, string LatestVersion, string ReleaseUrl)> CheckForUpdatesAsync();
}