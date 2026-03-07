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
namespace rmOneNoteSyncApp.Services
{
    public class DeploymentService(
        ILogger<DeploymentService> logger,
        IConfigurationProviderService configProvider) : IDeploymentService
    {
        private readonly ILogger<DeploymentService> _logger = logger;
        private readonly IConfigurationProviderService _configProvider = configProvider;
        private const string REMOTE_BASE_PATH = "/home/root/onenote-sync";

        public event EventHandler<DeploymentProgressEventArgs>? DeploymentProgress;

        public async Task<DeploymentResult> CheckInstallationAsync(ISshService sshService)
        {
            DeploymentResult result = new DeploymentResult();

            try
            {
                ReportProgress("Checking existing installation...", 0.1, DeploymentStage.Checking);

                // Check if directory exists
                string dirCheck = await sshService.ExecuteCommandAsync($"test -d {REMOTE_BASE_PATH} && echo 'exists'");
                if (!dirCheck.Contains("exists"))
                {
                    result.IsInstalled = false;
                    return result;
                }

                // Check version file
                try
                {
                    string versionContent = await sshService.ExecuteCommandAsync($"cat {REMOTE_BASE_PATH}/version.json");
                    if (string.IsNullOrEmpty(versionContent))
                    {
                        throw new ArgumentNullException();
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
                result.ComponentStatus["watcher"] = await CheckServiceAsync(sshService, "onenote-sync-watcher");
                result.ComponentStatus["httpclient"] = await CheckServiceAsync(sshService, "onenote-sync-httpclient");
                result.ComponentStatus["cache"] = await CheckFileExistsAsync(sshService, $"{REMOTE_BASE_PATH}/cache/.sync_cache");

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

        public async Task<DeploymentResult> DeployAsync(ISshService sshService)
        {
            DeploymentResult result = new DeploymentResult();

            try
            {
                ReportProgress("Starting deployment...", 0, DeploymentStage.PreparingFiles);

                // Step 1: Prepare filesystem
                await PrepareFilesystemAsync(sshService);
                ReportProgress("Filesystem prepared", 0.2, DeploymentStage.PreparingFiles);

                // Step 2: Create directory structure
                await CreateDirectoryStructureAsync(sshService);
                ReportProgress("Directory structure created", 0.3, DeploymentStage.PreparingFiles);

                // Step 3: Download binaries
                string localExtractedDir = await DownloadAndExtractLatestReleaseAsync();
                ReportProgress("Binaries downloaded and extracted", 0.4, DeploymentStage.DownloadingBinaries);

                // Step 4: Upload binaries
                await UploadBinariesAsync(sshService, localExtractedDir);
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
                await UploadConfigurationAsync(sshService);
                ReportProgress("Configuration uploaded", 0.6, DeploymentStage.ConfiguringServices);

                // Step 6: Install systemd services
                await InstallSystemdServicesAsync(sshService);
                ReportProgress("Services installed", 0.8, DeploymentStage.ConfiguringServices);

                // Step 7: Start services
                await StartServicesAsync(sshService);
                ReportProgress("Services started", 0.9, DeploymentStage.StartingServices);

                // Step 8: Verify installation
                DeploymentResult checkResult = await CheckInstallationAsync(sshService);
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

        private static async Task PrepareFilesystemAsync(ISshService sshService)
        {
            // Make filesystem writable
            _ = await sshService.ExecuteCommandAsync("mount -o remount,rw /");

            // Unmount /etc if it's separately mounted
            try
            {
                _ = await sshService.ExecuteCommandAsync("umount /etc -l");
            }
            catch
            {
                // /etc might not be separately mounted, that's OK
            }
        }

        private static async Task CreateDirectoryStructureAsync(ISshService sshService)
        {
            string[] directories = new[]
            {
                REMOTE_BASE_PATH,
                $"{REMOTE_BASE_PATH}/bin",
                $"{REMOTE_BASE_PATH}/cache",
                $"{REMOTE_BASE_PATH}/logs",
                $"{REMOTE_BASE_PATH}/debug"
            };

            foreach (string? dir in directories)
            {
                _ = await sshService.ExecuteCommandAsync($"mkdir -p {dir}");
            }
        }

        private async Task<string> DownloadAndExtractLatestReleaseAsync()
        {
            ReportProgress("Fetching latest release from GitHub...", 0.1, DeploymentStage.DownloadingBinaries);

            using HttpClient httpClient = new HttpClient();
            // GitHub API requires a User-Agent header to work
            httpClient.DefaultRequestHeaders.Add("User-Agent", "rmOneNoteSyncApp-Installer");

            // 1. Get latest release info
            string releaseUrl = "https://api.github.com/repos/Excustic/rmOneNoteSyncClient/releases/tags/latest";
            GitHubRelease? releaseInfo = await httpClient.GetFromJsonAsync<GitHubRelease>(releaseUrl);

            if (releaseInfo?.Assets == null || releaseInfo.Assets.Count == 0)
            {
                throw new Exception("No release assets found on GitHub.");
            }

            // 2. Find the zip asset (since you are uploading a zip file to the release)
            GitHubAsset asset = releaseInfo.Assets.FirstOrDefault(static a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) ?? throw new Exception("Could not find a .zip binary in the latest GitHub release.");
            ReportProgress($"Downloading {asset.Name}...", 0.4, DeploymentStage.DownloadingBinaries);

            // 3. Create a unique temporary directory
            string tempExtractDir = Path.Combine(Path.GetTempPath(), $"rmOneNoteSync_{Guid.NewGuid()}");
            _ = Directory.CreateDirectory(tempExtractDir);

            string localZipPath = Path.Combine(tempExtractDir, asset.Name);

            // 4. Download the zip file
            using (Stream downloadStream = await httpClient.GetStreamAsync(asset.BrowserDownloadUrl))
            using (FileStream fileStream = File.Create(localZipPath))
            {
                await downloadStream.CopyToAsync(fileStream);
            }

            ReportProgress("Extracting files...", 0.6, DeploymentStage.DownloadingBinaries);

            // 5. Extract the zip file and delete the original zip archive
            ZipFile.ExtractToDirectory(localZipPath, tempExtractDir, overwriteFiles: true);
            File.Delete(localZipPath);

            // Return the path to the folder containing your extracted binaries
            return tempExtractDir;
        }

        private static async Task UploadBinariesAsync(ISshService sshService, string localExtractedDir)
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

                await sshService.UploadFileAsync(localFile, remotePath);
                _ = await sshService.ExecuteCommandAsync($"chmod +x {remotePath}");
            }
        }

        private async Task UploadConfigurationAsync(ISshService sshService)
        {
            // Create configuration files
            string watcherConfig = "WATCH_PATH=/home/root/.local/share/remarkable/xochitl\n" +
                                   "LOG_PATH=/home/root/onenote-sync/logs/watcher.log\n" +
                                   "CACHE_PATH=/home/root/onenote-sync/cache/.sync_cache";

            string versionJson = @"{
          ""version"": ""1.1.0"",
          ""installed_date"": """ + DateTime.UtcNow.ToString("o") + @""",
          ""components"": {
            ""watcher"": ""1.0.0"",
            ""httpclient"": ""1.1.0"",
            ""cache_format"": ""2""
          }
        }";

            // Write configs via SSH
            _ = await sshService.ExecuteCommandAsync($"echo '{watcherConfig}' > {REMOTE_BASE_PATH}/watcher.conf");
            _ = await sshService.ExecuteCommandAsync($"echo '{versionJson}' > {REMOTE_BASE_PATH}/version.json");
            
            // Generate httpclient.conf via ConfigurationProviderService bypassng service restart
            _ = await _configProvider.UpdateDeviceConfigurationAsync(restartService: false);
        }

        private static async Task InstallSystemdServicesAsync(ISshService sshService)
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
            _ = await sshService.ExecuteCommandAsync($"echo '{watcherService}' > /etc/systemd/system/onenote-sync-watcher.service");
            _ = await sshService.ExecuteCommandAsync($"echo '{httpclientService}' > /etc/systemd/system/onenote-sync-httpclient.service");

            // Reload systemd
            _ = await sshService.ExecuteCommandAsync("systemctl daemon-reload");

            // Enable services
            _ = await sshService.ExecuteCommandAsync("systemctl enable onenote-sync-watcher");
            _ = await sshService.ExecuteCommandAsync("systemctl enable onenote-sync-httpclient");
        }

        private static async Task StartServicesAsync(ISshService sshService)
        {
            _ = await sshService.ExecuteCommandAsync("systemctl start onenote-sync-watcher");
            _ = await sshService.ExecuteCommandAsync("systemctl start onenote-sync-httpclient");
        }

        private static async Task<bool> CheckServiceAsync(ISshService sshService, string serviceName)
        {
            try
            {
                string status = await sshService.ExecuteCommandAsync($"systemctl is-active {serviceName}");
                return status.Trim() == "active";
            }
            catch
            {
                return false;
            }
        }

        private static async Task<bool> CheckFileExistsAsync(ISshService sshService, string path)
        {
            try
            {
                string result = await sshService.ExecuteCommandAsync($"test -f {path} && echo 'exists'");
                return result.Contains("exists");
            }
            catch
            {
                return false;
            }
        }

        private static string ExtractVersionFromJson(string json)
        {
            // Simple extraction - in production use proper JSON parsing
            int versionStart = json.IndexOf("\"version\":") + 11;
            int versionEnd = json.IndexOf("\"", versionStart);
            return json[versionStart..versionEnd];
        }

        private void ReportProgress(string message, double progress, DeploymentStage stage)
        {
            _logger.LogInformation("{Stage}: {Message} ({Progress:P})", stage, message, progress);
            DeploymentProgress?.Invoke(this, new DeploymentProgressEventArgs
            {
                Message = message,
                Progress = progress,
                Stage = stage
            });
        }

        public async Task<DeploymentResult> UpdateAsync(ISshService sshService)
        {
            // For updates, we backup config, deploy new version, restore config
            string backupPath = Path.GetTempFileName();

            try
            {
                _ = await BackupConfigurationAsync(sshService, backupPath);
                DeploymentResult result = await DeployAsync(sshService);
                _ = await RestoreConfigurationAsync(sshService, backupPath);
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

        public async Task<DeploymentResult> UninstallAsync(ISshService sshService)
        {
            DeploymentResult result = new DeploymentResult();

            try
            {
                // Stop services
                _ = await sshService.ExecuteCommandAsync("systemctl stop onenote-sync-watcher");
                _ = await sshService.ExecuteCommandAsync("systemctl stop onenote-sync-httpclient");

                // Disable services
                _ = await sshService.ExecuteCommandAsync("systemctl disable onenote-sync-watcher");
                _ = await sshService.ExecuteCommandAsync("systemctl disable onenote-sync-httpclient");

                // Remove service files
                _ = await sshService.ExecuteCommandAsync("rm -f /etc/systemd/system/onenote-sync-*.service");

                // Remove installation directory
                _ = await sshService.ExecuteCommandAsync($"rm -rf {REMOTE_BASE_PATH}");

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

        public async Task<bool> BackupConfigurationAsync(ISshService sshService, string localPath)
        {
            try
            {
                await sshService.DownloadFileAsync($"{REMOTE_BASE_PATH}/watcher.conf", localPath + ".watcher");
                await sshService.DownloadFileAsync($"{REMOTE_BASE_PATH}/httpclient.conf", localPath + ".httpclient");
                return true;
            }
            catch
            {
                return false;
            }
        }

        public async Task<bool> RestoreConfigurationAsync(ISshService sshService, string localPath)
        {
            try
            {
                if (File.Exists(localPath + ".watcher"))
                {
                    await sshService.UploadFileAsync(localPath + ".watcher", $"{REMOTE_BASE_PATH}/watcher.conf");
                }

                if (File.Exists(localPath + ".httpclient"))
                {
                    await sshService.UploadFileAsync(localPath + ".httpclient", $"{REMOTE_BASE_PATH}/httpclient.conf");
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