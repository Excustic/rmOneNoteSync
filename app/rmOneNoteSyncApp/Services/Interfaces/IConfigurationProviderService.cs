using System.Collections.Generic;
using System.Threading.Tasks;

namespace rmOneNoteSyncApp.Services.Interfaces;

public interface IConfigurationProviderService
{
    Task<string> GetConfigurationJsonAsync(string deviceId);
    Task<bool> UpdateDeviceConfigurationAsync(bool restartService = true);
    Task<int> GetServerPortAsync();
    string ConfigPath { get; }
}