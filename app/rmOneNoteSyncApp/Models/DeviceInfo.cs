using System;

namespace rmOneNoteSyncApp.Models;

/// <summary>
/// Represents information about a connected reMarkable device.
/// This model is platform-agnostic and used across all implementations.
/// </summary>
public class DeviceInfo
{
    public string IpAddress { get; set; } = string.Empty;
    public string MacAddress { get; set; } = string.Empty;
    public string InterfaceName { get; set; } = string.Empty;
    public DateTime DetectedAt { get; set; }
    public bool IsWifiEnabled { get; set; }
    public DeviceConnectionType ConnectionType { get; set; }
    public string? DeviceVersion { get; set; }
    public string? DeviceSerial { get; set; }
}

public enum DeviceConnectionType
{
    Unknown,
    USB,
    WiFi,
    Both
}

public enum ConnectionState
{
    Disconnected,
    Detected,
    Authenticating,
    Connected,
    Configured,
    Error
}