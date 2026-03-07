using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Renci.SshNet;
using Microsoft.Extensions.Logging;
using rmOneNoteSyncApp.Services.Interfaces;

namespace rmOneNoteSyncApp.Services;

/// <summary>
/// SSH service implementation that works across all platforms.
/// SSH.NET is a pure-managed implementation that doesn't require platform-specific code.
/// </summary>
public class SshService(ILogger<SshService> logger) : ISshService, IDisposable
{
    public event EventHandler<bool>? OnConnectionChanged;
    private SshClient? _sshClient;
    private SftpClient? _sftpClient;
    private const string Username = "root";

    public bool IsConnected => _sshClient?.IsConnected ?? false;


    public async Task<bool> ConnectAsync(string host, string password)
    {
        try
        {
            logger.LogInformation("Attempting SSH connection to {Host}", host);
            
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
            
            logger.LogInformation("SSH connection established successfully");
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
            info["Model"] = (await ExecuteCommandAsync("cat /sys/devices/soc0/machine")).Trim();
            info["Version"] = (await ExecuteCommandAsync("cat /etc/os-release | grep IMG_VERSION | cut -d'\"' -f2")).Trim();
            info["Serial"] = (await ExecuteCommandAsync("cat /sys/devices/soc0/serial_number")).Trim();
            
            // Get storage info
            var dfOutput = await ExecuteCommandAsync("df -h /home");
            var lines = dfOutput.Split('\n');
            if (lines.Length > 1)
            {
                var parts = lines[1].Split([' '], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4)
                {
                    info["StorageUsed"] = parts[2];
                    info["StorageAvailable"] = parts[3];
                    info["StoragePercent"] = parts[4];
                }
            }
            
            // Check for existing sync installation
            try
            {
                var versionFile = await ExecuteCommandAsync("cat /home/root/onenote-sync/version.json");
                info["SyncVersion"] = versionFile.Trim();
            }
            catch
            {
                info["SyncVersion"] = "Not installed";
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
    
        logger.LogInformation("Downloading {RemotePath} to {LocalPath}", remotePath, localPath);
    
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
    
        logger.LogInformation("Download completed successfully");
    }

    public async Task<bool> EnableWifiOverSshAsync()
    {
        try
        {
            logger.LogInformation("Enabling WiFi over SSH for persistent connection");
            
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
                logger.LogInformation("Starting wpa_supplicant");
                await ExecuteCommandAsync(
                    "wpa_supplicant -B -i wlan0 -c /etc/wpa_supplicant/wpa_supplicant.conf");
            }
            
            // Get DHCP lease
            await ExecuteCommandAsync("dhclient wlan0 2>/dev/null || true");
            
            // Verify Wi-Fi has an IP address
            var wifiIp = await ExecuteCommandAsync(
                "ip addr show wlan0 | grep 'inet ' | awk '{print $2}' | cut -d/ -f1");
            
            var success = !string.IsNullOrWhiteSpace(wifiIp);
            logger.LogInformation("WiFi enabled: {Success}, IP: {IP}", 
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
        
        logger.LogInformation("Uploading {LocalPath} to {RemotePath}", localPath, remotePath);
        
        // Ensure remote directory exists
        var remoteDir = Path.GetDirectoryName(remotePath)?.Replace('\\', '/');
        if (!string.IsNullOrEmpty(remoteDir))
        {
            await Task.Run(() => CreateRemoteDirectory(remoteDir));
        }
        
        await Task.Run(() =>
        {
            using var fileStream = File.OpenRead(localPath);
            _sftpClient.UploadFile(fileStream, remotePath, true);
        });
        
        logger.LogInformation("Upload completed successfully");
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
                
                logger.LogInformation("SSH connection closed");
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
}

public class SshConnectionException : Exception
{
    public SshConnectionException(string message) : base(message) { }
    public SshConnectionException(string message, Exception innerException) 
        : base(message, innerException) { }
}