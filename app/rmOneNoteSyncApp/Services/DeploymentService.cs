using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using rmOneNoteSyncApp.Services.Interfaces;
using rmOneNoteSyncApp.Models;
using System.Formats.Tar;
using System.Reflection;
using System.Text.Json;
namespace rmOneNoteSyncApp.Services
{
    public class DeploymentService(
        ILogger<DeploymentService> logger,
        IConfigurationProviderService configProvider,
        IDeviceDetectionService deviceDetectionService,
        ISshService sshService) : IDeploymentService
    {
        private readonly ILogger<DeploymentService> _logger = logger;
        private readonly IConfigurationProviderService _configProvider = configProvider;
        private readonly IDeviceDetectionService _deviceDetectionService = deviceDetectionService;
        private readonly ISshService _sshService = sshService;
        public static readonly string REMOTE_BASE_PATH = "/home/root/onenote-sync";

        public event EventHandler<DeploymentProgressEventArgs>? DeploymentProgress;

        public async Task<DeploymentResult> CheckInstallationAsync()
        {
            DeploymentResult result = new();

            try
            {
                ReportProgress("Checking existing installation...", 0.1, DeploymentStage.Checking);

                // Check if directory exists
                string dirCheck = await _sshService.ExecuteCommandAsync($"test -d {REMOTE_BASE_PATH} && echo 'exists'");
                if (!dirCheck.Contains("exists"))
                {
                    result.IsInstalled = false;
                    return result;
                }

                // Check version file
                try
                {
                    string versionContent = await _sshService.ExecuteCommandAsync($"cat {REMOTE_BASE_PATH}/version.json");
                    if (string.IsNullOrEmpty(versionContent))
                    {
                        throw new ArgumentNullException(nameof(versionContent));
                    }

                    result.InstalledVersion = ExtractVersionFromJson(versionContent);
                    result.IsInstalled = true;
                }
                catch
                {
                    result.IsInstalled = true;
                    result.InstalledVersion = "Unknown";
                }

                // Check component status
                result.ComponentStatus["watcher"] = await CheckServiceAsync("onenote-sync-watcher");
                result.ComponentStatus["httpclient"] = await CheckServiceAsync("onenote-sync-httpclient");
                result.ComponentStatus["cache"] = await CheckFileExistsAsync($"{REMOTE_BASE_PATH}/cache/.sync_cache");

                result.Success = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to check installation");
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        public async Task<DeploymentResult> DeployAsync()
        {
            DeploymentResult result = new();

            try
            {
                ReportProgress("Starting deployment...", 0, DeploymentStage.PreparingFiles);

                // Step 1: Prepare filesystem
                await PrepareFilesystemAsync();
                ReportProgress("Filesystem prepared", 0.2, DeploymentStage.PreparingFiles);

                // Step 2: Create directory structure
                await CreateDirectoryStructureAsync();
                ReportProgress("Directory structure created", 0.3, DeploymentStage.PreparingFiles);

                // Step 3: Download binaries
                DeviceInfo? device;
                if ((device = _deviceDetectionService.CurrentDevice) == null)
                {
                    throw new InvalidOperationException("No device selected");
                }
                string localExtractedDir = await DownloadAndExtractLatestReleaseAsync(device);
                ReportProgress("Binaries downloaded and extracted", 0.4, DeploymentStage.DownloadingBinaries);

                // Step 4: Upload binaries
                await UploadBinariesAsync(localExtractedDir);
                ReportProgress("Binaries uploaded", 0.5, DeploymentStage.UploadingBinaries);

                try
                {
                    if (Directory.Exists(localExtractedDir))
                    {
                        Directory.Delete(localExtractedDir, true);
                    }
                }
                catch { /* Ignore cleanup errors */ }

                // Step 5: Upload configuration files
                await UploadConfigurationAsync();
                ReportProgress("Configuration uploaded", 0.6, DeploymentStage.ConfiguringServices);

                // Step 6: Install systemd services
                await InstallSystemdServicesAsync();
                ReportProgress("Services installed", 0.8, DeploymentStage.ConfiguringServices);

                // Step 7: Start services
                await StartServicesAsync();
                ReportProgress("Services started", 0.9, DeploymentStage.StartingServices);

                // Step 8: Verify installation
                DeploymentResult checkResult = await CheckInstallationAsync();
                result.Success = checkResult.Success && checkResult.IsInstalled;
                result.IsInstalled = checkResult.IsInstalled;
                result.InstalledVersion = checkResult.InstalledVersion;
                result.ComponentStatus = checkResult.ComponentStatus;

                ReportProgress("Deployment complete!", 1.0, DeploymentStage.Complete);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Deployment failed");
                result.Success = false;
                result.ErrorMessage = ex.Message;
                ReportProgress($"Deployment failed: {ex.Message}", 0, DeploymentStage.Complete);
            }

            return result;
        }

        private async Task PrepareFilesystemAsync()
        {
            // Make filesystem writable
            _ = await _sshService.ExecuteCommandAsync("mount -o remount,rw /");

            // Unmount /etc if it's separately mounted
            try
            {
                _ = await _sshService.ExecuteCommandAsync("umount /etc -l");
            }
            catch
            {
                // /etc might not be separately mounted, that's OK
            }
        }

        private async Task CreateDirectoryStructureAsync()
        {
            string[] directories =
            [
                REMOTE_BASE_PATH,
                $"{REMOTE_BASE_PATH}/bin",
                $"{REMOTE_BASE_PATH}/cache",
                $"{REMOTE_BASE_PATH}/logs",
                $"{REMOTE_BASE_PATH}/debug"
            ];

            foreach (string? dir in directories)
            {
                _ = await _sshService.ExecuteCommandAsync($"mkdir -p {dir}");
            }
        }

        private async Task<string> DownloadAndExtractLatestReleaseAsync(DeviceInfo device)
        {
            ReportProgress("Fetching latest release from GitHub...", 0.1, DeploymentStage.DownloadingBinaries);

            using HttpClient httpClient = new();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "rmOneNoteSyncApp-Installer");

            // 1. Fixed URL for the new repo
            string releaseUrl = "https://api.github.com/repos/Excustic/rmOneNoteSync/releases/latest";
            GitHubRelease? releaseInfo = await httpClient.GetFromJsonAsync<GitHubRelease>(releaseUrl);

            if (releaseInfo?.Assets == null || releaseInfo.Assets.Count == 0)
            {
                throw new Exception("No release assets found on GitHub.");
            }

            // 2. Map the device to the exact tar.gz asset
            string expectedAssetName = GetDaemonAssetName(device.Model);

            GitHubAsset asset = releaseInfo.Assets.FirstOrDefault(a => a.Name.Equals(expectedAssetName, StringComparison.OrdinalIgnoreCase))
                ?? throw new Exception($"Could not find binary '{expectedAssetName}' for your device in the latest release.");

            ReportProgress($"Downloading {asset.Name}...", 0.4, DeploymentStage.DownloadingBinaries);

            string tempExtractDir = Path.Combine(Path.GetTempPath(), $"rmOneNoteSync_{Guid.NewGuid()}");
            Directory.CreateDirectory(tempExtractDir);
            string localTarPath = Path.Combine(tempExtractDir, asset.Name);

            // 3. Download the tar.gz file
            using (Stream downloadStream = await httpClient.GetStreamAsync(asset.BrowserDownloadUrl))
            using (FileStream fileStream = File.Create(localTarPath))
            {
                await downloadStream.CopyToAsync(fileStream);
            }

            ReportProgress("Extracting files...", 0.6, DeploymentStage.DownloadingBinaries);

            // 4. Extract the .tar.gz using native .NET 9 libraries
            using (FileStream fileStream = File.OpenRead(localTarPath))
            using (GZipStream gzipStream = new(fileStream, CompressionMode.Decompress))
            {
                TarFile.ExtractToDirectory(gzipStream, tempExtractDir, overwriteFiles: true);
            }

            File.Delete(localTarPath);
            return tempExtractDir;
        }

        // Helper method to map the device model string to your GitHub Action artifacts
        private static string GetDaemonAssetName(string? model)
        {
            return $"rm-daemon-{model}.tar.gz";
        }
        private async Task UploadBinariesAsync(string localExtractedDir)
        {
            // Find the 'bin' directory if it exists inside the extracted zip, otherwise use root
            string? binPath = Directory.GetDirectories(localExtractedDir, "bin", SearchOption.AllDirectories).FirstOrDefault();

            IEnumerable<string> extractedFiles = binPath != null
                ? Directory.GetFiles(binPath)
                : Directory.GetFiles(localExtractedDir, "*", SearchOption.AllDirectories)
                    .Where(static f => !f.EndsWith(".conf") && !f.EndsWith(".json") && !f.Contains("debug"));

            foreach (string localFile in extractedFiles)
            {
                string fileName = Path.GetFileName(localFile);
                string remotePath = $"{REMOTE_BASE_PATH}/bin/{fileName}";

                await _sshService.UploadFileAsync(localFile, remotePath);
                _ = await _sshService.ExecuteCommandAsync($"chmod +x {remotePath}");
            }
        }

        private async Task UploadConfigurationAsync()
        {
            string watcherConfig = "WATCH_PATH=/home/root/.local/share/remarkable/xochitl\n" +
                                   "LOG_PATH=/home/root/onenote-sync/logs/watcher.log\n" +
                                   "CACHE_PATH=/home/root/onenote-sync/cache/.sync_cache";

            // 1. Grab the clean version (e.g., "0.6.0") just like we did for the UI
            string versionInfo = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            string cleanVersion = versionInfo.Split('+')[0];

            // 2. Use Raw String Literals to cleanly inject variables into JSON without escaping quotes
            string versionJson = $$"""
            {
              "version": "{{cleanVersion}}",
              "installed_date": "{{DateTime.UtcNow:o}}",
              "cache_format": "4"
            }
            """;

            // Write configs via SSH
            _ = await _sshService.ExecuteCommandAsync($"echo '{watcherConfig}' > {REMOTE_BASE_PATH}/watcher.conf");
            _ = await _sshService.ExecuteCommandAsync($"echo '{versionJson}' > {REMOTE_BASE_PATH}/version.json");

            // Generate httpclient.conf via ConfigurationProviderService bypassng service restart
            _ = await _configProvider.UpdateDeviceConfigurationAsync(restartService: false);
        }

        private async Task InstallSystemdServicesAsync()
        {
            string watcherService = @"[Unit]
            Description=reMarkable Sync Watcher
            After=home.mount

            [Service]
            Type=simple
            ExecStart=/home/root/onenote-sync/bin/watcher
            Restart=on-failure
            RestartSec=10
            User=root

            [Install]
            WantedBy=multi-user.target";

            string httpclientService = @"[Unit]
            Description=reMarkable Sync HTTP Client
            After=home.mount network.target
            Wants=onenote-sync-watcher.service

            [Service]
            Type=simple
            ExecStart=/home/root/onenote-sync/bin/httpclient
            Restart=on-failure
            RestartSec=30
            User=root

            [Install]
            WantedBy=multi-user.target";

            // Install service files
            _ = await _sshService.ExecuteCommandAsync($"echo '{watcherService}' > /etc/systemd/system/onenote-sync-watcher.service");
            _ = await _sshService.ExecuteCommandAsync($"echo '{httpclientService}' > /etc/systemd/system/onenote-sync-httpclient.service");

            // Reload systemd
            _ = await _sshService.ExecuteCommandAsync("systemctl daemon-reload");

            // Enable services
            _ = await _sshService.ExecuteCommandAsync("systemctl enable onenote-sync-watcher");
            _ = await _sshService.ExecuteCommandAsync("systemctl enable onenote-sync-httpclient");
        }

        private async Task StartServicesAsync()
        {
            _ = await _sshService.ExecuteCommandAsync("systemctl start onenote-sync-watcher");
            _ = await _sshService.ExecuteCommandAsync("systemctl start onenote-sync-httpclient");
        }

        private async Task<bool> CheckServiceAsync(string serviceName)
        {
            try
            {
                string status = await _sshService.ExecuteCommandAsync($"systemctl is-active {serviceName}");
                return status.Trim() == "active";
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> CheckFileExistsAsync(string path)
        {
            try
            {
                string result = await _sshService.ExecuteCommandAsync($"test -f {path} && echo 'exists'");
                return result.Contains("exists");
            }
            catch
            {
                return false;
            }
        }

        private static string ExtractVersionFromJson(string json)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json.Trim());
                return doc.RootElement.GetProperty("version").GetString() ?? "Unknown";
            }
            catch
            {
                return "Unknown";
            }
        }

        private void ReportProgress(string message, double progress, DeploymentStage stage)
        {
            _logger.LogDebug("{Stage}: {Message} ({Progress:P})", stage, message, progress);
            DeploymentProgress?.Invoke(this, new DeploymentProgressEventArgs
            {
                Message = message,
                Progress = progress,
                Stage = stage
            });
        }

        public async Task<DeploymentResult> UpdateAsync()
        {
            // For updates, we backup config, deploy new version, restore config
            string backupPath = Path.GetTempFileName();

            try
            {
                _ = await BackupConfigurationAsync(backupPath);
                DeploymentResult result = await DeployAsync();
                _ = await RestoreConfigurationAsync(backupPath);
                return result;
            }
            finally
            {
                if (File.Exists(backupPath))
                {
                    File.Delete(backupPath);
                }
            }
        }

        public async Task<DeploymentResult> UninstallAsync()
        {
            DeploymentResult result = new();

            try
            {
                // Stop services
                _ = await _sshService.ExecuteCommandAsync("systemctl stop onenote-sync-watcher");
                _ = await _sshService.ExecuteCommandAsync("systemctl stop onenote-sync-httpclient");

                // Disable services
                _ = await _sshService.ExecuteCommandAsync("systemctl disable onenote-sync-watcher");
                _ = await _sshService.ExecuteCommandAsync("systemctl disable onenote-sync-httpclient");

                // Remove service files
                _ = await _sshService.ExecuteCommandAsync("rm -f /etc/systemd/system/onenote-sync-*.service");

                // Remove installation directory
                _ = await _sshService.ExecuteCommandAsync($"rm -rf {REMOTE_BASE_PATH}");

                result.Success = true;
                result.IsInstalled = false;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }

            return result;
        }

        public async Task<bool> BackupConfigurationAsync(string localPath)
        {
            try
            {
                await _sshService.DownloadFileAsync($"{REMOTE_BASE_PATH}/watcher.conf", localPath + ".watcher");
                await _sshService.DownloadFileAsync($"{REMOTE_BASE_PATH}/httpclient.conf", localPath + ".httpclient");
                return true;
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> RestoreConfigurationAsync(string localPath)
        {
            try
            {
                if (File.Exists(localPath + ".watcher"))
                {
                    await _sshService.UploadFileAsync(localPath + ".watcher", $"{REMOTE_BASE_PATH}/watcher.conf");
                }

                if (File.Exists(localPath + ".httpclient"))
                {
                    await _sshService.UploadFileAsync(localPath + ".httpclient", $"{REMOTE_BASE_PATH}/httpclient.conf");
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    // Helper classes for parsing GitHub API JSON
    internal class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";

        [JsonPropertyName("assets")]
        public List<GitHubAsset> Assets { get; set; } = [];
    }

    internal class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";

        [JsonPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; } = "";
    }
}