using System;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using rmOneNoteSyncApp.Services.Interfaces;

namespace rmOneNoteSyncApp.Services.Platform;

/// <summary>
/// Windows-specific implementation using WMI for better USB device detection
/// </summary>
public class WindowsDeviceDetectionService(ILogger<WindowsDeviceDetectionService> logger, IDatabaseService databaseService) : DeviceDetectionServiceBase(logger, databaseService)
{
    private ManagementEventWatcher? _usbWatcher;

    protected override async Task<NetworkInterface?> FindRemarkableInterfaceAsync()
    {
        return await Task.Run(() =>
        {
            // On Windows, we look for RNDIS adapters
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();

            // Check by description first (most reliable)
            var byDescription = interfaces.FirstOrDefault(i =>
                i.OperationalStatus == OperationalStatus.Up &&
                (i.Description.Contains("RNDIS", StringComparison.OrdinalIgnoreCase) ||
                 i.Description.Contains("USB Ethernet", StringComparison.OrdinalIgnoreCase)));

            if (byDescription != null)
                return byDescription;

            // Fallback to checking IP range
            return interfaces.FirstOrDefault(i =>
                i.OperationalStatus == OperationalStatus.Up &&
                HasRemarkableIpInRange(i));
        });
    }

    public override async Task StartMonitoringAsync()
    {
        await base.StartMonitoringAsync();

        // Set up WMI watcher for USB device events
        try
        {
            var query = new WqlEventQuery(
                "SELECT * FROM Win32_DeviceChangeEvent WHERE EventType = 2 OR EventType = 3");

            _usbWatcher = new ManagementEventWatcher(query);
            _usbWatcher.EventArrived += async (sender, e) =>
            {
                // USB device connected or disconnected
                await Task.Delay(1000); // Give the network adapter time to initialize
                await CheckConnectionAsync();
            };

            _usbWatcher.Start();
            _logger.LogDebug("Started Windows USB device monitoring");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start WMI monitoring, falling back to polling only");
        }
    }

    public override async Task StopMonitoringAsync()
    {
        _usbWatcher?.Stop();
        _usbWatcher?.Dispose();
        _usbWatcher = null;

        await base.StopMonitoringAsync();
    }

    public override void Dispose()
    {
        _usbWatcher?.Dispose();
        base.Dispose();
    }
}