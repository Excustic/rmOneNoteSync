using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using rmOneNoteSyncApp.Models;
using rmOneNoteSyncApp.Services.Interfaces;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Collections.Generic;

namespace rmOneNoteSyncApp.Services.Platform;

/// <summary>
/// Base implementation with common device detection logic
/// </summary>
public abstract class DeviceDetectionServiceBase : IDeviceDetectionService
{
    protected readonly ILogger _logger;
    protected readonly IDatabaseService _databaseService;
    private Timer? _pollingTimer;
    private DeviceInfo? _currentDevice;
    private bool _isMonitoring;
    private int _wifiScanAttempts = 0;
    private const int MAX_WIFI_SCAN_ATTEMPTS = 3;
    private DateTime? _lastSeenAt;
    private static readonly TimeSpan DISCONNECT_TIMEOUT_WIFI = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DISCONNECT_TIMEOUT_USB = TimeSpan.FromSeconds(3);
    private bool _requiresManualScan;
    public bool RequiresManualScan => _requiresManualScan;

    protected const string REMARKABLE_USB_IP = "10.11.99.1";

    public event EventHandler<DeviceConnectionEventArgs>? DeviceConnectionChanged;
    private bool _isConnected;
    public bool IsConnected => _currentDevice != null && _isConnected;
    public DeviceInfo? CurrentDevice => _currentDevice;

    protected DeviceDetectionServiceBase(ILogger logger, IDatabaseService databaseService)
    {
        _logger = logger;
        _databaseService = databaseService;
    }

    public void ResetWifiScanAttempts()
    {
        _wifiScanAttempts = 0;
        _requiresManualScan = false;
        _logger.LogDebug("WiFi scan attempts reset manually.");
    }

    public virtual async Task StartMonitoringAsync()
    {
        if (_isMonitoring) return;

        _isMonitoring = true;

        // Start polling timer - check every 2 seconds
        var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync())
        {
            // _logger.LogDebug("Checking device connection...");
            await CheckConnectionAsync();
        }

        _logger.LogDebug("Started device monitoring");
    }

    public virtual async Task StopMonitoringAsync()
    {
        _isMonitoring = false;

        _pollingTimer?.Dispose();
        _pollingTimer = null;

        _logger.LogDebug("Stopped device monitoring");
    }

    public async Task<bool> CheckConnectionAsync()
    {
        try
        {
            var config = await _databaseService.GetConfigurationAsync();
            var networkInterface = await FindRemarkableInterfaceAsync();
            string? targetIp = !string.IsNullOrEmpty(_currentDevice?.IpAddress) && _currentDevice?.ConnectionType == DeviceConnectionType.WiFi
                ? _currentDevice.IpAddress
                : config?.LastNetworkIp;

            // Show temporary disconnection until proven otherwise.
            var tempConnected = _isConnected;
            _isConnected = true; // Assume connected until proven otherwise.
            if (networkInterface != null && await PingDeviceAsync(REMARKABLE_USB_IP))
            {
                // Verify we can ping the device on the USB IP
                if (_currentDevice == null || _currentDevice.ConnectionType != DeviceConnectionType.USB)
                {
                    // New USB connection
                    _currentDevice = new DeviceInfo
                    {
                        IpAddress = REMARKABLE_USB_IP,
                        InterfaceName = networkInterface.Name,
                        MacAddress = GetMacAddress(networkInterface),
                        DetectedAt = DateTime.UtcNow,
                        ConnectionType = DeviceConnectionType.USB
                    };


                    // Perform ArpScan to gather WiFi fallback IP.
                    if (config?.LastNetworkIp == null)
                        _ = ArpScan(config);
                }
                _wifiScanAttempts = 0;
                _lastSeenAt = DateTime.UtcNow;
                _requiresManualScan = false;
            }

            // 2. Check last known WiFi IP or current connection persistence
            else if (!string.IsNullOrEmpty(targetIp) && await PingDeviceAsync(targetIp))
            {
                _wifiScanAttempts = 0;
                _lastSeenAt = DateTime.UtcNow;
                _requiresManualScan = false;

                if (_currentDevice == null || _currentDevice.ConnectionType != DeviceConnectionType.WiFi || _currentDevice.IpAddress != targetIp)
                {
                    var macAddress = config?.DeviceMacAddress ?? "Unknown";
                    _currentDevice = new DeviceInfo
                    {
                        IpAddress = targetIp,
                        InterfaceName = "WLAN",
                        MacAddress = macAddress,
                        DetectedAt = DateTime.UtcNow,
                        ConnectionType = DeviceConnectionType.WiFi
                    };

                }
            }
            else
            {
                _isConnected = false;
            }

            // Trigger UI event
            if (tempConnected != _isConnected)
            {
                DeviceConnectionChanged?.Invoke(this, new DeviceConnectionEventArgs
                {
                    IsConnected = _isConnected,
                    Device = _currentDevice
                });
            }
            // 3. ARP Scan - try to find IP of the device in the local network as last resort.
            if (!_isConnected)
            {
                if (await ArpScan(config))
                {
                    if (_currentDevice == null || _currentDevice.ConnectionType != DeviceConnectionType.WiFi || _currentDevice.IpAddress != config.LastNetworkIp)
                    {
                        _currentDevice = new DeviceInfo
                        {
                            IpAddress = config.LastNetworkIp,
                            InterfaceName = "WLAN",
                            MacAddress = config.DeviceMacAddress,
                            DetectedAt = DateTime.UtcNow,
                            ConnectionType = DeviceConnectionType.WiFi
                        };
                    }
                }
            }
            return _isConnected;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking device connection");
            return false;
        }
    }
    protected async Task<bool> ArpScan(SyncConfiguration config)
    {
        // 3. ARP Scan (Priority 3 - Only after grace period fails OR no current device)
        if (config is { EnableWifiSync: true } && !string.IsNullOrEmpty(config.DeviceMacAddress) && !_requiresManualScan)
        {
            if (_wifiScanAttempts < MAX_WIFI_SCAN_ATTEMPTS)
            {
                _wifiScanAttempts++;
                _logger.LogDebug("Attempting WiFi scan ({Attempt}/{Max})", _wifiScanAttempts, MAX_WIFI_SCAN_ATTEMPTS);

                // Now returns an IPAddress object directly
                IPAddress? wifiIp = await ArpScanner.FindIpByMacAddressAsync(config.DeviceMacAddress, _logger);

                if (wifiIp != null && await PingDeviceAsync(wifiIp.ToString()))
                {
                    _wifiScanAttempts = 0;
                    _lastSeenAt = DateTime.UtcNow;

                    string ipString = wifiIp.ToString();

                    // Save this IP to the database for future starts
                    if (config.LastNetworkIp != ipString)
                    {
                        config.LastNetworkIp = ipString;
                        await _databaseService.SaveConfigurationAsync(config);
                    }
                    return true;
                }

                if (_wifiScanAttempts >= MAX_WIFI_SCAN_ATTEMPTS)
                {
                    _requiresManualScan = true;
                    _logger.LogWarning("WiFi scan failed after {Max} attempts. User intervention required.", MAX_WIFI_SCAN_ATTEMPTS);
                    DeviceConnectionChanged?.Invoke(this, new DeviceConnectionEventArgs
                    {
                        IsConnected = false,
                        Device = _currentDevice
                    });
                }
            }
        }
        return false;
    }
    protected abstract Task<NetworkInterface?> FindRemarkableInterfaceAsync();

    protected bool HasRemarkableIpInRange(NetworkInterface iface)
    {
        try
        {
            var ipProps = iface.GetIPProperties();
            return ipProps.UnicastAddresses.Any(addr =>
                addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                IsInRemarkableSubnet(addr.Address));
        }
        catch
        {
            return false;
        }
    }

    private bool IsInRemarkableSubnet(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 &&
               bytes[0] == 10 &&
               bytes[1] == 11 &&
               bytes[2] == 99;
    }

    private async Task<bool> PingDeviceAsync(string ipAddress)
    {
        try
        {
            using var ping = new Ping();
            Debug.WriteLine($"Pinging device at {ipAddress}");
            var reply = await ping.SendPingAsync(ipAddress, ipAddress == REMARKABLE_USB_IP ? 2000 : 15000);
            Debug.WriteLine($"Ping result: {reply.Status}");
            return reply.Status == IPStatus.Success;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string?> GetLocalIpAddressForDevice(string deviceIp)
    {
        try
        {
            // Simple approach: get all local IPs and find one with the same prefix (first 3 octets)
            // This is a common heuristic for home networks.
            var prefix = string.Join(".", deviceIp.Split('.').Take(3)) + ".";

            var host = await Dns.GetHostEntryAsync(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    var ipStr = ip.ToString();
                    if (ipStr.StartsWith(prefix))
                    {
                        return ipStr;
                    }
                }
            }

            // Fallback: just return the first IPv4 address
            return host.AddressList
                .FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?
                .ToString();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error detecting local IP address");
            return null;
        }
    }

    // public string GetHostIpAddress()
    // {
    //     try
    //     {
    //         // Find the IP address on the same subnet as the reMarkable
    //         // The reMarkable is typically at 10.11.99.1, so we need our 10.11.99.x address
    //         var interfaces = NetworkInterface.GetAllNetworkInterfaces()
    //             .Where(i => i.OperationalStatus == OperationalStatus.Up &&
    //                     i.NetworkInterfaceType != NetworkInterfaceType.Loopback);

    //         foreach (var iface in interfaces)
    //         {
    //             var props = iface.GetIPProperties();
    //             foreach (var addr in props.UnicastAddresses)
    //             {
    //                 if (addr.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
    //                 {
    //                     var ip = addr.Address.ToString();
    //                     // Check if it's in the reMarkable subnet
    //                     if (!ip.StartsWith("10.11.99.")) continue;
    //                     _logger.LogDebug("Found host IP for reMarkable communication: {IP}", ip);
    //                     return ip;
    //                 }
    //             }
    //         }

    //         // Fallback to any local IP
    //         var localIp = Dns.GetHostEntry(Dns.GetHostName())
    //             .AddressList
    //             .FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
    //             ?.ToString() ?? "127.0.0.1";

    //         _logger.LogWarning("Could not find IP in reMarkable subnet, using {IP}", localIp);
    //         return localIp;
    //     }
    //     catch (Exception ex)
    //     {
    //         _logger.LogError(ex, "Failed to determine host IP address");
    //         return "127.0.0.1";
    //     }
    // }

    private string GetMacAddress(NetworkInterface iface)
    {
        try
        {
            var mac = iface.GetPhysicalAddress();
            var bytes = mac.GetAddressBytes();
            return string.Join(":", bytes.Select(b => b.ToString("X2")));
        }
        catch
        {
            return "Unknown";
        }
    }

    public virtual void Dispose()
    {
        StopMonitoringAsync().GetAwaiter().GetResult();
    }
}

static class ArpScanner
{
    public static async Task<IPAddress?> FindIpByMacAddressAsync(string macAddress, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(macAddress)) return null;

        string normalizedMac = NormalizeMacAddress(macAddress);
        logger.LogDebug("Starting non-privileged active sweep for MAC: {Mac}", normalizedMac);

        // 1. Get the local IP to figure out our subnet
        var localIp = GetLocalIPv4();
        if (localIp == null)
        {
            logger.LogError("Could not determine local IPv4 address.");
            return null;
        }

        // 2. Force the OS to update its ARP cache by pinging the entire subnet
        await SweepSubnetAsync(localIp, logger);

        // 3. Now read the OS ARP cache (which is now fresh)
        try
        {
            var arpOutput = await ExecuteArpCommandAsync();
            if (string.IsNullOrWhiteSpace(arpOutput)) return null;

            var lines = arpOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (NormalizeMacAddress(line).Contains(normalizedMac))
                {
                    string? ipStr = ExtractIpAddress(line);
                    if (IPAddress.TryParse(ipStr, out var ip))
                    {
                        logger.LogDebug("Match found! MAC {Mac} is at IP {Ip}", normalizedMac, ip);
                        return ip;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error occurred while reading ARP table.");
        }

        logger.LogWarning("Device with MAC {Mac} not found after active sweep.", normalizedMac);
        return null;
    }

    private static async Task SweepSubnetAsync(IPAddress localIp, ILogger logger)
    {
        byte[] ipBytes = localIp.GetAddressBytes();
        var pingTasks = new List<Task>();

        logger.LogDebug("Sweeping subnet {Ip}.1 - 254 to force ARP resolutions...", $"{ipBytes[0]}.{ipBytes[1]}.{ipBytes[2]}");

        // Fire off 254 pings concurrently. 
        // We use a very short timeout because we don't care if they succeed, 
        // we just want the OS to broadcast the ARP request.
        for (int i = 1; i <= 254; i++)
        {
            ipBytes[3] = (byte)i;
            var targetIp = new IPAddress(ipBytes);

            pingTasks.Add(Task.Run(async () =>
            {
                using var ping = new Ping();
                try
                {
                    // 100ms timeout is plenty for a local network ARP resolution
                    await ping.SendPingAsync(targetIp, 100);
                }
                catch { /* Ignore ping failures */ }
            }));
        }

        await Task.WhenAll(pingTasks);
    }

    private static IPAddress? GetLocalIPv4()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus == OperationalStatus.Up &&
                ni.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            {
                foreach (var ip in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ip.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        return ip.Address;
                    }
                }
            }
        }
        return null;
    }

    private static string NormalizeMacAddress(string input) =>
        string.IsNullOrWhiteSpace(input) ? string.Empty : input.Replace(":", "").Replace("-", "").ToLowerInvariant();

    private static string? ExtractIpAddress(string arpLine)
    {
        var match = Regex.Match(arpLine, @"\b(?:\d{1,3}\.){3}\d{1,3}\b");
        return match.Success ? match.Value : null;
    }

    private static async Task<string> ExecuteArpCommandAsync()
    {
        using var process = new Process();
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.CreateNoWindow = true;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            process.StartInfo.FileName = "arp";
            process.StartInfo.Arguments = "-a";
        }
        else // Linux & macOS
        {
            process.StartInfo.FileName = "arp";
            process.StartInfo.Arguments = "-a -n"; // -n prevents slow DNS lookups
        }

        process.Start();
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output;
    }
}