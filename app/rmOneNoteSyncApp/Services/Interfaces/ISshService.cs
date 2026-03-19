using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace rmOneNoteSyncApp.Services.Interfaces;

/// <summary>
/// SSH service interface for communication with the reMarkable device
/// </summary>
public interface ISshService : IDisposable
{
    /// <summary>
    /// Check if currently connected via SSH
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// The IP address of the current connection
    /// </summary>
    string? CurrentIp { get; }

    /// <summary>
    /// Establish SSH connection to the device
    /// </summary>
    /// <param name="host">IP address or hostname</param>
    /// <param name="password">Device password</param>
    /// <returns>True if connection successful</returns>
    Task<bool> ConnectAsync(string host, string password);

    /// <summary>
    /// Disconnect from the device
    /// </summary>
    Task DisconnectAsync();

    /// <summary>
    /// Execute a command on the device
    /// </summary>
    /// <param name="command">Command to execute</param>
    /// <returns>Command output</returns>
    Task<string> ExecuteCommandAsync(string command);

    /// <summary>
    /// Restarts the reMarkable sync services
    /// </summary>
    /// <returns>True if services were restarted successfully</returns>
    Task<bool> RestartServicesAsync();

    /// <summary>
    /// Upload a file to the device
    /// </summary>
    /// <param name="localPath">Local file path</param>
    /// <param name="remotePath">Remote destination path</param>
    Task UploadFileAsync(string localPath, string remotePath);

    /// <summary>
    /// Download a file from the device
    /// </summary>
    /// <param name="remotePath">Remote file path</param>
    /// <param name="localPath">Local destination path</param>
    Task DownloadFileAsync(string remotePath, string localPath);

    /// <summary>
    /// Enable WiFi over SSH for persistent connection
    /// </summary>
    /// <returns>True if WiFi was successfully enabled</returns>
    Task<bool> EnableWifiOverSshAsync();

    /// <summary>
    /// Get device information (model, version, storage, etc.)
    /// </summary>
    /// <returns>Dictionary of device properties</returns>
    Task<Dictionary<string, string>> GetDeviceInfoAsync();

    /// <summary>
    /// Check if a systemd service is active
    /// </summary>
    /// <param name="serviceName">Name of the service</param>
    /// <returns>True if service is active</returns>
    Task<bool> CheckServiceStatusAsync(string serviceName);

    /// <summary>
    /// Notifies whether a SSH connection was created/severed.
    /// </summary>
    event EventHandler<bool>? OnConnectionChanged;

    /// <summary>
    /// Updates the SERVER_URL_FALLBACK in /home/root/onenote-sync/http.conf
    /// </summary>
    /// <param name="ip">The host IP to inject</param>
    Task UpdateServerUrlFallbackAsync(string ip);

    /// <summary>
    /// Gets the MAC address of the reMarkable device.
    /// </summary>
    /// <returns>The MAC address of the reMarkable device.</returns>
    Task<string?> GetMacAddressAsync();
}