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
    private string? _model;
    public string? Model
    {
        get => _model; internal set
        {
            string m = value?.ToLower() ?? "";
            string variant = "unknown";
            if (m.Contains("ferrari"))
                variant = "ferrari";
            else if (m.Contains("chiappa"))
                variant = "chiappa";
            else if (m.Contains('1'))
                variant = "rm1";
            else if (m.Contains('2'))
                variant = "rm2";
            _model = variant;
        }
    }
    public string? SyncVersion { get; internal set; }

    public string DeviceDisplayName => (Model ?? "unknown") switch
    {
        "ferrari" => "reMarkable Paper Pro",
        "chiappa" => "reMarkable Move",
        "rm1" => "reMarkable 1",
        "rm2" => "reMarkable 2",
        _ => Model ?? "Unknown"
    };
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