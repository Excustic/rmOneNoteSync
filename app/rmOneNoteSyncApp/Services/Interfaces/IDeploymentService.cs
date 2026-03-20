using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using rmOneNoteSyncApp.Models;

namespace rmOneNoteSyncApp.Services.Interfaces;

/// <summary>
/// Service for deploying sync components to the reMarkable device
/// </summary>
public interface IDeploymentService
{
    event EventHandler<DeploymentProgressEventArgs>? DeploymentProgress;

    Task<DeploymentResult> CheckInstallationAsync();
    Task<DeploymentResult> DeployAsync(DeviceInfo device);
    Task<DeploymentResult> UpdateAsync(DeviceInfo device);
    Task<DeploymentResult> UninstallAsync();
    Task<bool> BackupConfigurationAsync(string localPath);
    Task<bool> RestoreConfigurationAsync(string localPath);
}

public class DeploymentProgressEventArgs : EventArgs
{
    public string Message { get; set; } = "";
    public double Progress { get; set; }
    public DeploymentStage Stage { get; set; }
}

public enum DeploymentStage
{
    Idle,
    Checking,
    PreparingFiles,
    DownloadingBinaries,
    UploadingBinaries,
    ConfiguringServices,
    StartingServices,
    Verifying,
    Complete
}

public class DeploymentResult
{
    public bool Success { get; set; }
    public bool IsInstalled { get; set; }
    public string? InstalledVersion { get; set; }
    public string? ErrorMessage { get; set; }
    public Dictionary<string, bool> ComponentStatus { get; set; } = new();
}