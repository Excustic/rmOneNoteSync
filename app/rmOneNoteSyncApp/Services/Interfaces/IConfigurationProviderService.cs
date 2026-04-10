using System.Collections.Generic;
using System.Threading.Tasks;

namespace rmOneNoteSyncApp.Services.Interfaces;

public interface IConfigurationProviderService
{
    Task<bool> UpdateDeviceConfigurationAsync(bool restartService = true);
    Task<bool> RegisterEndpointAsync();
    Task<bool> UpdateWhitelistAsync();
    Task<bool> UpdateDaemonSettingsAsync(bool restartService = true);
    Task<int> GetServerPortAsync();
    Task<(List<string> Whitelist, List<string> SyncFolders)> FetchWhitelistFromDeviceAsync();
    string ConfigPath { get; }
}