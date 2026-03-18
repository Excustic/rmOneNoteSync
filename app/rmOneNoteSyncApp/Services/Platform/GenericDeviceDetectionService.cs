using System.Net.NetworkInformation;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using rmOneNoteSyncApp.Services.Interfaces;

namespace rmOneNoteSyncApp.Services.Platform;

/// <summary>
/// Generic fallback implementation for unsupported platforms
/// Uses basic network interface detection without platform-specific optimizations
/// </summary>
public class GenericDeviceDetectionService : DeviceDetectionServiceBase
{
    public GenericDeviceDetectionService(ILogger<GenericDeviceDetectionService> logger, IDatabaseService databaseService)
        : base(logger, databaseService)
    {
        logger.LogWarning("Using generic device detection - platform-specific features unavailable");
    }

    protected override async Task<NetworkInterface?> FindRemarkableInterfaceAsync()
    {
        return await Task.Run(() =>
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();

            // Generic approach: just look for any interface with the reMarkable IP range
            foreach (var iface in interfaces)
            {
                if (iface.OperationalStatus != OperationalStatus.Up)
                    continue;

                // Skip obviously wrong interface types
                if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    iface.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    continue;

                if (HasRemarkableIpInRange(iface))
                {
                    _logger.LogDebug("Found reMarkable interface (generic): {Name} ({Type})",
                        iface.Name, iface.NetworkInterfaceType);
                    return iface;
                }
            }

            return null;
        });
    }

    public override async Task StartMonitoringAsync()
    {
        await base.StartMonitoringAsync();
        _logger.LogDebug("Started generic device monitoring (polling only)");
    }
}