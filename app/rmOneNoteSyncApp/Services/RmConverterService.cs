using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using rmOneNoteSyncApp.Services.Interfaces;

namespace rmOneNoteSyncApp.Services;

public class RmConverterService : IRmConverterService
{
    private readonly ILogger<RmConverterService> _logger;
    private readonly string _rmcExecutablePath;

    public RmConverterService(ILogger<RmConverterService> logger)
    {
        _logger = logger;

        string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rmOneNoteSyncApp");
        Directory.CreateDirectory(appData);

        string binaryName = OperatingSystem.IsWindows() ? "rmc.exe" : "rmc";
        _rmcExecutablePath = Path.Combine(appData, binaryName);

        // The binary bundled by GitHub Actions sits right next to the app executable
        string bundledPath = Path.Combine(AppContext.BaseDirectory, binaryName);

        if (File.Exists(bundledPath))
        {
            // Always overwrite to ensure updates to the app also update the local rmc binary
            File.Copy(bundledPath, _rmcExecutablePath, overwrite: true);

            // Restore executable permissions on Linux/macOS after copying
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(_rmcExecutablePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
    }

    public async Task<ConversionResult> ConvertToInkMLAsync(string rmFilePath)
    {
        try
        {
            if (!File.Exists(rmFilePath))
            {
                return new ConversionResult { Success = false, ErrorMessage = $"Source file not found: {rmFilePath}" };
            }

            // Create a temporary output directory
            string tempDir = Path.Combine(Path.GetTempPath(), "rm_sync_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            string outputBaseName = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(rmFilePath));

            // rmc cli parameters: -t inkml <INPUT> -o <OUTPUT_BASE>
            var startInfo = new ProcessStartInfo
            {
                FileName = _rmcExecutablePath,
                Arguments = $"-t inkml \"{rmFilePath}\" -o \"{outputBaseName}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            _logger.LogDebug("Starting conversion: {Exe} {Args}", _rmcExecutablePath, startInfo.Arguments);

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return new ConversionResult { Success = false, ErrorMessage = "Failed to start rmc process." };
            }

            string stdout = await process.StandardOutput.ReadToEndAsync();
            string stderr = await process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                _logger.LogError("rmc conversion failed: {Error}", stderr);
                return new ConversionResult { Success = false, ErrorMessage = stderr };
            }

            // The converter outputs .xml for InkML and .html for text
            string xmlPath = outputBaseName + ".xml";
            string htmlPath = outputBaseName + ".html";

            return new ConversionResult
            {
                Success = true,
                InkMLPath = File.Exists(xmlPath) ? xmlPath : null,
                HtmlPath = File.Exists(htmlPath) ? htmlPath : null
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception during .rm conversion");
            return new ConversionResult { Success = false, ErrorMessage = ex.Message };
        }
    }
}
