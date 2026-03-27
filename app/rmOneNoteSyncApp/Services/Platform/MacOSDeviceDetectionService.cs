using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using rmOneNoteSyncApp.Services;
using rmOneNoteSyncApp.Services.Interfaces;

namespace rmOneNoteSyncApp.Services.Platform;

/// <summary>
/// macOS-specific implementation for device detection
/// </summary>
public class MacOSDeviceDetectionService : DeviceDetectionServiceBase
{
    public MacOSDeviceDetectionService(ILogger<MacOSDeviceDetectionService> logger, IDatabaseService databaseService, ISshService sshService)
        : base(logger, databaseService, sshService)
    {
    }

    protected override async Task<NetworkInterface?> FindRemarkableInterfaceAsync()
    {
        return await Task.Run(() =>
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();

            // On macOS, the reMarkable typically shows up as a bridge or ethernet interface
            // First try to find by name pattern
            var candidateInterfaces = interfaces.Where(i =>
                i.OperationalStatus == OperationalStatus.Up &&
                (i.Name.StartsWith("en") || i.Name.StartsWith("bridge")) &&
                i.NetworkInterfaceType != NetworkInterfaceType.Loopback).ToList();

            // Check each candidate for the reMarkable IP range
            foreach (var iface in candidateInterfaces)
            {
                if (HasRemarkableIpInRange(iface))
                {
                    _logger.LogDebug("Found reMarkable interface on macOS: {Name}", iface.Name);
                    return iface;
                }
            }

            // Fallback: check all interfaces for IP range
            return interfaces.FirstOrDefault(i =>
                i.OperationalStatus == OperationalStatus.Up &&
                HasRemarkableIpInRange(i));
        });
    }

    public override async Task StartMonitoringAsync()
    {
        _ = base.StartMonitoringAsync();

        // macOS doesn't have as convenient USB monitoring as Windows/Linux
        // We rely on the base class polling mechanism
        // Could potentially use IOKit via P/Invoke for better detection, but that's complex

        _logger.LogDebug("Started macOS device monitoring (using polling)");
    }
}