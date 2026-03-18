using System;
using System.Threading.Tasks;
using rmOneNoteSyncApp.Models;

namespace rmOneNoteSyncApp.Services;

/// <summary>
/// Platform-agnostic interface for device detection.
/// Each platform (Windows, Linux, macOS, iOS, Android) will implement this differently.
/// </summary>
public interface IDeviceDetectionService : IDisposable
{
    /// <summary>
    /// Event raised when device connection status changes
    /// </summary>
    event EventHandler<DeviceConnectionEventArgs>? DeviceConnectionChanged;

    /// <summary>
    /// Current connection status
    /// </summary>
    bool IsConnected { get; }

    /// <summary>
    /// Information about the currently connected device
    /// </summary>
    DeviceInfo? CurrentDevice { get; }

    /// <summary>
    /// True if the service has given up on automatic scanning and requires user intervention.
    /// </summary>
    bool RequiresManualScan { get; }

    /// <summary>
    /// Start monitoring for device connections
    /// </summary>
    Task StartMonitoringAsync();

    /// <summary>
    /// Stop monitoring for device connections
    /// </summary>
    Task StopMonitoringAsync();

    /// <summary>
    /// Manually check for device connection
    /// </summary>
    Task<bool> CheckConnectionAsync();

    /// <summary>
    /// Resets the scan attempts counter to trigger a fresh scan cycle.
    /// </summary>
    void ResetWifiScanAttempts();

    Task<string?> GetLocalIpAddressForDevice(string deviceIp);
}

public class DeviceConnectionEventArgs : EventArgs
{
    public bool IsConnected { get; set; }
    public DeviceInfo? Device { get; set; }
    public string? ErrorMessage { get; set; }
}