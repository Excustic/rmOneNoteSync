using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using rmOneNoteSyncApp.Services.Interfaces;

namespace rmOneNoteSyncApp.ViewModels;

public partial class LogsViewModel : ViewModelBase
{
    private readonly ISshService _sshService;
    private readonly ILogger<LogsViewModel>? _logger;
    private readonly string _appLogPath;

    [ObservableProperty]
    private ObservableCollection<LogFile> _logFiles = new();

    [ObservableProperty]
    private LogFile? _selectedLogFile;

    [ObservableProperty]
    private string _logContent = "";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _autoRefresh;

    [ObservableProperty]
    private bool _isTailing = true;

    [ObservableProperty]
    private string _exportStatus = "";

    [ObservableProperty]
    private int _refreshInterval = 5; // seconds

    private IDisposable? _refreshTimer;

    public LogsViewModel(ISshService sshService)
    {
        _sshService = sshService;

        try
        {
            _logger = App.ServiceProvider?.GetService<ILogger<LogsViewModel>>();
        }
        catch { }

        // Set local app log path
        _appLogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "rmOneNoteSyncApp",
            "logs");

        // Load logs on initialization
        Task.Run(LoadLogsAsync);
    }

    [RelayCommand]
    private async Task LoadLogsAsync()
    {
        try
        {
            IsLoading = true;
            LogFiles.Clear();

            var allLogs = new List<LogFile>();

            // Load app logs from local system
            allLogs.AddRange(await LoadAppLogsAsync());

            // Load device logs if connected
            if (_sshService.IsConnected)
            {
                allLogs.AddRange(await LoadDeviceLogsAsync());
            }

            // Sort by Source (Device first), then by Name
            var sortedLogs = allLogs
                .OrderBy(l => l.Source == LogSource.Device ? 0 : 1)
                .ThenBy(l => l.Name)
                .ToList();

            foreach (var log in sortedLogs)
            {
                LogFiles.Add(log);
            }

            _logger?.LogInformation("Loaded {Count} log files", LogFiles.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load logs");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task<List<LogFile>> LoadAppLogsAsync()
    {
        return await Task.Run(() =>
        {
            var logs = new List<LogFile>();
            if (Directory.Exists(_appLogPath))
            {
                var logDir = new DirectoryInfo(_appLogPath);
                foreach (var file in logDir.GetFiles("*.log"))
                {
                    logs.Add(new LogFile
                    {
                        Name = file.Name,
                        FullPath = file.FullName,
                        Source = LogSource.Application,
                        Size = FormatFileSize(file.Length),
                        LastModified = file.LastWriteTime,
                        IsLocal = true
                    });
                }
            }
            return logs;
        });
    }

    private async Task<List<LogFile>> LoadDeviceLogsAsync()
    {
        var logs = new List<LogFile>();
        try
        {
            // List log files on device
            var result = await _sshService.ExecuteCommandAsync(
                "ls -la /home/root/onenote-sync/logs/*.log 2>/dev/null | awk '{print $9 \" \" $5 \" \" $6 \" \" $7 \" \" $8}'");

            if (!string.IsNullOrWhiteSpace(result))
            {
                var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        var path = parts[0];
                        var size = long.TryParse(parts[1], out var s) ? s : 0;

                        logs.Add(new LogFile
                        {
                            Name = Path.GetFileName(path),
                            FullPath = path,
                            Source = LogSource.Device,
                            Size = FormatFileSize(size),
                            IsLocal = false
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load device logs");
        }
        return logs;
    }

    partial void OnSelectedLogFileChanged(LogFile? value)
    {
        if (value != null)
        {
            Task.Run(() => LoadLogContentAsync(value));
        }
    }
    private async Task<string> ReadTailAsync(string filePath, int maxLines)
    {
        // A Queue perfectly handles the "first in, first out" rolling window
        var lines = new Queue<string>(maxLines);

        // FileShare.ReadWrite is crucial here so we don't lock out the logger!
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);

        string? line;
        while ((line = await reader.ReadLineAsync()) != null)
        {
            // If we hit our limit, push the oldest line out before adding the new one
            if (lines.Count >= maxLines)
            {
                lines.Dequeue();
            }
            lines.Enqueue(line);
        }

        return string.Join(Environment.NewLine, lines);
    }
    private async Task LoadLogContentAsync(LogFile logFile)
    {
        try
        {
            IsLoading = true;

            if (logFile.IsLocal)
            {
                // Efficiently read only the last 1000 lines without locking the file
                LogContent = await ReadTailAsync(logFile.FullPath, 1000);
            }
            else
            {
                // Read remote file via SSH
                var command = $"tail -n 1000 '{logFile.FullPath}'";
                LogContent = await _sshService.ExecuteCommandAsync(command);
            }

            _logger?.LogInformation("Loaded log content from {File}", logFile.Name);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load log content");
            LogContent = $"Error loading log: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RefreshCurrentLogAsync()
    {
        if (SelectedLogFile != null)
        {
            await LoadLogContentAsync(SelectedLogFile);
        }
    }

    [RelayCommand]
    private async Task ClearLogAsync()
    {
        if (SelectedLogFile == null) return;

        try
        {
            if (SelectedLogFile.IsLocal)
            {
                // Clear local file
                await File.WriteAllTextAsync(SelectedLogFile.FullPath, "");
            }
            else
            {
                // Clear remote file
                await _sshService.ExecuteCommandAsync($"echo '' > '{SelectedLogFile.FullPath}'");
            }

            LogContent = "";
            _logger?.LogInformation("Cleared log file: {File}", SelectedLogFile.Name);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to clear log");
        }
    }

    [RelayCommand]
    private async Task ExportLogAsync()
    {
        if (SelectedLogFile == null || string.IsNullOrEmpty(LogContent)) return;

        try
        {
            // Create export path
            var exportDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "rmOneNoteSyncLogs");

            Directory.CreateDirectory(exportDir);

            var exportPath = Path.Combine(exportDir,
                $"{SelectedLogFile.Name}_{DateTime.Now:yyyyMMdd_HHmmss}.log");

            await File.WriteAllTextAsync(exportPath, LogContent);

            ExportStatus = "Exported to Desktop!";
            _logger?.LogInformation("Exported log to: {Path}", exportPath);

            Task.Run(async () =>
            {
                await Task.Delay(3000);
                ExportStatus = "";
            });
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to export log");
            ExportStatus = "Export Failed";

            Task.Run(async () =>
            {
                await Task.Delay(3000);
                ExportStatus = "";
            });
        }
    }

    partial void OnAutoRefreshChanged(bool value)
    {
        if (value)
        {
            StartAutoRefresh();
        }
        else
        {
            StopAutoRefresh();
        }
    }

    private void StartAutoRefresh()
    {
        _refreshTimer?.Dispose();

        _refreshTimer = Observable
            .Interval(TimeSpan.FromSeconds(RefreshInterval))
            .Subscribe(_ =>
            {
                if (SelectedLogFile != null)
                {
                    Task.Run(() => LoadLogContentAsync(SelectedLogFile));
                }
            });
    }

    private void StopAutoRefresh()
    {
        _refreshTimer?.Dispose();
        _refreshTimer = null;
    }

    private string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        int order = 0;
        double size = bytes;

        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return $"{size:0.##} {sizes[order]}";
    }

    public void Dispose()
    {
        StopAutoRefresh();
    }
}

public class LogFile
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public LogSource Source { get; set; }
    public string Size { get; set; } = "";
    public DateTime LastModified { get; set; }
    public bool IsLocal { get; set; }

    public string DisplayName => $"{Name} ({Source})";
}

public enum LogSource
{
    Application,
    Device
}