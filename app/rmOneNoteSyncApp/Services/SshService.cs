using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Renci.SshNet;
using Microsoft.Extensions.Logging;
using rmOneNoteSyncApp.Services.Interfaces;
using System.Text.Json;
using System.Net.Http;

namespace rmOneNoteSyncApp.Services;

/// <summary>
/// SSH service implementation that works across all platforms.
/// SSH.NET is a pure-managed implementation that doesn't require platform-specific code.
/// </summary>
public class SshService(ILogger<SshService> logger) : ISshService, IDisposable
{
    private SftpClient? _sftpClient;
    private const string Username = "root";
    private string? _currentIp;

    public event EventHandler<bool>? OnConnectionChanged;
    private SshClient? _sshClient;
    public bool IsConnected => _sshClient?.IsConnected ?? false;
    public string? CurrentIp => _currentIp;
    private const string ServerUrlFallback = "SERVER_URL_FALLBACK";
    private const string GetModel = "cat /sys/devices/soc0/machine";
    private const string GetVersion = "cat /etc/os-release | grep IMG_VERSION | cut -d'\"' -f2";
    private const string GetSerial = "cat /sys/devices/soc0/serial_number";
    public async Task<bool> ConnectAsync(string host, string password)
    {
        try
        {
            logger.LogDebug("Attempting SSH connection to {Host}", host);

            // Disconnect any existing connection
            await DisconnectAsync();

            // Create connection with timeout settings
            var connectionInfo = new ConnectionInfo(
                host,
                22, // SSH port
                Username,
                new PasswordAuthenticationMethod(Username, password))
            {
                Timeout = TimeSpan.FromSeconds(10)
            };

            // Connect SSH client for command execution
            _sshClient = new SshClient(connectionInfo);
            await _sshClient.ConnectAsync(CancellationToken.None);

            // Connect SFTP client for file transfers
            _sftpClient = new SftpClient(connectionInfo);
            await _sftpClient.ConnectAsync(CancellationToken.None);


            logger.LogDebug("SSH connection established successfully to {Host}", host);
            _currentIp = host;
            OnConnectionChanged?.Invoke(this, true);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to establish SSH connection");
            await DisconnectAsync();
            throw new SshConnectionException($"Failed to connect: {ex.Message}", ex);
        }
    }

    public async Task<string> ExecuteCommandAsync(string command)
    {
        if (_sshClient is not { IsConnected: true })
        {
            throw new InvalidOperationException("SSH client is not connected");
        }

        logger.LogDebug("Executing command: {Command}", command);

        return await Task.Run(() =>
        {
            using var sshCommand = _sshClient.CreateCommand(command);
            sshCommand.CommandTimeout = TimeSpan.FromSeconds(30);
            var result = sshCommand.Execute();

            if (sshCommand.ExitStatus != 0 && !string.IsNullOrEmpty(sshCommand.Error))
            {
                logger.LogWarning("Command returned non-zero exit code {ExitCode}: {Error}",
                    sshCommand.ExitStatus, sshCommand.Error);
            }

            return result;
        });
    }

    public async Task<Dictionary<string, string>> GetDeviceInfoAsync()
    {
        var info = new Dictionary<string, string>();

        try
        {
            // Get device model and version
            info["Model"] = (await ExecuteCommandAsync(GetModel)).Trim();
            info["Version"] = (await ExecuteCommandAsync(GetVersion)).Trim();
            info["Serial"] = (await ExecuteCommandAsync(GetSerial)).Trim();

            // Check for existing sync installation (HTTP First)
            try
            {
                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                string versionJson = await httpClient.GetStringAsync($"http://{_currentIp}:8000/version");
                
                if (!string.IsNullOrWhiteSpace(versionJson))
                {
                    using JsonDocument doc = JsonDocument.Parse(versionJson);
                    info["SyncVersion"] = doc.RootElement.GetProperty("version").GetString() ?? "Unknown";
                }
            }
            catch
            {
                // Fallback: Check if installation directory exists via SSH if daemon is stopped
                var dirCheck = await ExecuteCommandAsync("test -d /home/root/onenote-sync && echo 'exists'");
                info["SyncVersion"] = dirCheck.Contains("exists") ? "Unknown (Service Stopped)" : "Not installed";
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error getting device info");
        }

        return info;
    }

    public async Task DownloadFileAsync(string remotePath, string localPath)
    {
        if (_sftpClient == null || !_sftpClient.IsConnected)
        {
            throw new InvalidOperationException("SFTP client is not connected");
        }

        logger.LogDebug("Downloading {RemotePath} to {LocalPath}", remotePath, localPath);

        // Ensure local directory exists
        var localDir = Path.GetDirectoryName(localPath);
        if (!string.IsNullOrEmpty(localDir))
        {
            Directory.CreateDirectory(localDir);
        }

        await Task.Run(() =>
        {
            using var fileStream = File.Create(localPath);
            _sftpClient.DownloadFile(remotePath, fileStream);
        });

        logger.LogDebug("Download completed successfully");
    }

    public async Task<bool> EnableWifiOverSshAsync()
    {
        try
        {
            logger.LogDebug("Enabling WiFi over SSH for persistent connection");

            // Check if WiFi interface exists
            var interfaces = await ExecuteCommandAsync("ip link show");
            if (!interfaces.Contains("wlan0"))
            {
                logger.LogWarning("WiFi interface not found");
                return false;
            }

            // Enable the Wi-Fi interface
            await ExecuteCommandAsync("ip link set wlan0 up");

            // Ensure wpa_supplicant is running
            var wpaCheck = await ExecuteCommandAsync("pgrep wpa_supplicant");
            if (string.IsNullOrWhiteSpace(wpaCheck))
            {
                logger.LogDebug("Starting wpa_supplicant");
                await ExecuteCommandAsync(
                    "wpa_supplicant -B -i wlan0 -c /etc/wpa_supplicant/wpa_supplicant.conf");
            }

            // Get DHCP lease
            await ExecuteCommandAsync("dhclient wlan0 2>/dev/null || true");

            // Verify Wi-Fi has an IP address
            var wifiIp = await ExecuteCommandAsync(
                "ip addr show wlan0 | grep 'inet ' | awk '{print $2}' | cut -d/ -f1");

            var success = !string.IsNullOrWhiteSpace(wifiIp);
            logger.LogDebug("WiFi enabled: {Success}, IP: {IP}",
                success, wifiIp.Trim());

            return success;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to enable WiFi over SSH");
            return false;
        }
    }

    public async Task UploadFileAsync(string localPath, string remotePath)
    {
        if (_sftpClient is not { IsConnected: true })
        {
            throw new InvalidOperationException("SFTP client is not connected");
        }

        logger.LogDebug("Uploading {LocalPath} to {RemotePath}", localPath, remotePath);

        // Ensure remote directory exists
        var remoteDir = Path.GetDirectoryName(remotePath)?.Replace('\\', '/');
        if (!string.IsNullOrEmpty(remoteDir))
        {
            await Task.Run(() => CreateRemoteDirectory(remoteDir));
        }

        await Task.Run(() =>
        {
            try
            {
                using var fileStream = File.OpenRead(localPath);
                _sftpClient.UploadFile(fileStream, remotePath, true);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to upload file");
            }
        });

        logger.LogDebug("Upload completed successfully");
    }

    private void CreateRemoteDirectory(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var currentPath = "";

        foreach (var part in parts)
        {
            currentPath = currentPath + "/" + part;
            if (!_sftpClient!.Exists(currentPath))
            {
                _sftpClient.CreateDirectory(currentPath);
            }
        }
    }

    public async Task<bool> CheckServiceStatusAsync(string serviceName)
    {
        try
        {
            var status = await ExecuteCommandAsync($"systemctl is-active {serviceName}");
            return status.Trim() == "active";
        }
        catch
        {
            return false;
        }
    }

    public async Task UpdateServerUrlFallbackAsync(string ip)
    {
        if (_sshClient is not { IsConnected: true })
        {
            logger.LogWarning("Cannot update server fallback: SSH not connected");
            return;
        }

        try
        {
            var configPath = DeploymentService.REMOTE_BASE_PATH;
            var confFile = Path.Combine(configPath, "httpclient.conf");
            var newLine = $"{ServerUrlFallback}=http://{ip}:8080";

            logger.LogDebug("Updating {ServerUrlFallback} to {IP} on device", ServerUrlFallback, ip);

            // Ensure directory exists
            await ExecuteCommandAsync($"mkdir -p {configPath}");

            // Check if file exists and contains the key
            var checkCmd = $"grep -q '{ServerUrlFallback}=.*' {confFile}";
            var fileExists = await Task.Run(() =>
            {
                using var cmd = _sshClient.CreateCommand(checkCmd);
                cmd.Execute();
                return cmd.ExitStatus == 0;
            });

            if (fileExists)
            {
                // Update existing line
                await ExecuteCommandAsync($"sed -i 's|{ServerUrlFallback}=.*|{newLine}|' {confFile}");
            }
            else
            {
                // Append to file
                await ExecuteCommandAsync($"echo '{newLine}' >> {confFile}");
            }

            logger.LogDebug("Successfully updated {ServerUrlFallback}", ServerUrlFallback);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update {ServerUrlFallback} on device", ServerUrlFallback);
        }
    }

    public async Task DisconnectAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                _sftpClient?.Disconnect();
                _sftpClient?.Dispose();
                _sftpClient = null;

                _sshClient?.Disconnect();
                _sshClient?.Dispose();
                _sshClient = null;
                _currentIp = null;

                logger.LogDebug("SSH connection closed");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during SSH disconnect");
            }
            OnConnectionChanged?.Invoke(this, false);
        });
    }

    public void Dispose()
    {
        DisconnectAsync().GetAwaiter().GetResult();
        GC.SuppressFinalize(this);
    }

    public async Task<string?> GetMacAddressAsync()
    {
        string? macAddressOutput = null;
        try
        {
            macAddressOutput = await ExecuteCommandAsync("cat /sys/class/net/wlan0/address");
            if (!string.IsNullOrWhiteSpace(macAddressOutput))
            {
                logger.LogDebug("Fetched WLAN MAC Address: {MAC}", macAddressOutput);
                return macAddressOutput.Trim();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch device MAC address");
        }
        return macAddressOutput;
    }

    public async Task<bool> RestartServiceAsync()
    {
        // Stop the services first
        await ExecuteCommandAsync("systemctl stop onenote-sync-watcher");
        await ExecuteCommandAsync("systemctl stop onenote-sync-httpclient");

        // Wait a moment for services to fully stop
        await Task.Delay(2000);

        // Start the services again
        await ExecuteCommandAsync("systemctl start onenote-sync-watcher");
        await ExecuteCommandAsync("systemctl start onenote-sync-httpclient");

        // Wait for services to start
        await Task.Delay(2000);

        // Check service status
        var watcherStatus = await CheckServiceStatusAsync("onenote-sync-watcher");
        var httpClientStatus = await CheckServiceStatusAsync("onenote-sync-httpclient");

        return watcherStatus && httpClientStatus;
    }
}

public class SshConnectionException : Exception
{
    public SshConnectionException(string message) : base(message) { }
    public SshConnectionException(string message, Exception innerException)
        : base(message, innerException) { }
}