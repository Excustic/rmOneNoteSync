using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace rmOneNoteSyncApp.Services.Interfaces;

/// <summary>
/// Service for deploying sync components to the reMarkable device
/// </summary>
public interface IDeploymentService
{
    event EventHandler<DeploymentProgressEventArgs>? DeploymentProgress;
    
    Task<DeploymentResult> CheckInstallationAsync(ISshService sshService);
    Task<DeploymentResult> DeployAsync(ISshService sshService);
    Task<DeploymentResult> UpdateAsync(ISshService sshService);
    Task<DeploymentResult> UninstallAsync(ISshService sshService);
    Task<bool> BackupConfigurationAsync(ISshService sshService, string localPath);
    Task<bool> RestoreConfigurationAsync(ISshService sshService, string localPath);
}

public class DeploymentProgressEventArgs : EventArgs
{
    public string Message { get; set; } = "";
    public double Progress { get; set; }
    public DeploymentStage Stage { get; set; }
}

public enum DeploymentStage
{
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