using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using rmOneNoteSyncApp.Services;
using rmOneNoteSyncApp.Services.Interfaces;
using rmOneNoteSyncApp.Services.Platform;
using rmOneNoteSyncApp.ViewModels;
using Serilog;
using Serilog.Events;
using Microsoft.Data.Sqlite;
using Dapper;
using rmOneNoteSyncApp.Models;
using System.Text.Json;

namespace rmOneNoteSyncApp;

class Program
{
    private static FileStream? _lockFile;

    [STAThread]
    public static void Main(string[] args)
    {

        var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "rmOneNoteSyncApp");
        Directory.CreateDirectory(dir);
        try
        {
            _lockFile = File.Open(Path.Combine(dir, ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            _lockFile.Lock(0, 0);
        }
        catch
        {
            Console.WriteLine("Another instance of the application is already running. Exiting.");
            return;
        }

        try
        {
            // Build the host with dependency injection
            var host = Host.CreateDefaultBuilder(args)
                .ConfigureServices((context, services) =>
                {
                    // Configure Serilog for file logging
                    var logPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "rmOneNoteSyncApp",
                        "logs",
                        "app-.log");

                    // Load rotation settings from DB early
                    var earlyConfig = LoadEarlyLoggingConfig();

                    Log.Logger = new LoggerConfiguration()
                        .MinimumLevel.Debug()
                        .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
                        .Enrich.FromLogContext()
                        .WriteTo.Console()
                        .WriteTo.File(
                            logPath,
                            rollingInterval: RollingInterval.Day,
                            retainedFileCountLimit: earlyConfig.LogRetentionDays,
                            fileSizeLimitBytes: earlyConfig.LogFileSizeLimitMB * 1024L * 1024L,
                            rollOnFileSizeLimit: true,
                            shared: true,
                            flushToDiskInterval: TimeSpan.FromSeconds(1))
                        .CreateLogger();

                    // Use Serilog for logging
                    services.AddLogging(builder =>
                    {
                        builder.ClearProviders();
                        builder.AddSerilog();
                    });

                    // Register platform-specific services based on OS
                    RegisterPlatformServices(services);

                    // Register core services that work on all platforms
                    services.AddSingleton<IDatabaseService, SqliteDatabaseService>();
                    services.AddSingleton<ISshService, SshService>();
                    services.AddSingleton<IDeploymentService, DeploymentService>();
                    services.AddSingleton<ISyncService, SyncService>();
                    services.AddSingleton<IOneNoteAuthService, OneNoteAuthService>();
                    services.AddSingleton<IConfigurationProviderService, ConfigurationProviderService>();
                    services.AddSingleton<ISyncServerService, SyncServerService>();
                    services.AddSingleton<IOneNoteClient, OneNoteClient>();
                    services.AddSingleton<IRmConverterService, RmConverterService>();
                    services.AddSingleton<IStartupService, StartupService>();
                    services.AddSingleton<ISoftwareUpdateService, SoftwareUpdateService>();

                    // Register ViewModels
                    services.AddSingleton<MainViewModel>();
                    services.AddSingleton<DashboardViewModel>();
                    services.AddSingleton<FolderPickerViewModel>();
                    services.AddSingleton<SyncStatusViewModel>();
                    services.AddSingleton<SettingsViewModel>();
                    services.AddSingleton<LogsViewModel>();

                    services.AddHostedService<SyncServerHostedService>();

                    // Register the main application
                    services.AddSingleton<App>();
                })
                .Build();

            // Make services available globally for Avalonia
            App.ServiceProvider = host.Services;

            // Initialize database
            var dbService = host.Services.GetRequiredService<IDatabaseService>();
            var dbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "rmOneNoteSyncApp",
                "sync.db");
            dbService.Initialize(dbPath);

            // Build and run Avalonia application
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            _lockFile.Unlock(0, 0);
            _lockFile.Dispose();
        }
    }

    private static void RegisterPlatformServices(IServiceCollection services)
    {
        // Register the appropriate device detection service based on the platform
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            services.AddSingleton<IDeviceDetectionService, WindowsDeviceDetectionService>();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            services.AddSingleton<IDeviceDetectionService, LinuxDeviceDetectionService>();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            services.AddSingleton<IDeviceDetectionService, MacOSDeviceDetectionService>();
        }
        else
        {
            // Fallback to generic implementation for unknown platforms
            services.AddSingleton<IDeviceDetectionService, GenericDeviceDetectionService>();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static (int LogRetentionDays, int LogFileSizeLimitMB) LoadEarlyLoggingConfig()
    {
        // Default values
        int retention = 7;
        int sizeLimit = 10;

        try
        {
            var dbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "rmOneNoteSyncApp",
                "sync.db");

            if (File.Exists(dbPath))
            {
                using var connection = new SqliteConnection($"Data Source={dbPath}");
                // No await here as we're in a synchronous context or want simple blocking read at startup
                var json = connection.ExecuteScalar<string>(
                    "SELECT Json FROM Configuration ORDER BY UpdatedAt DESC LIMIT 1");

                if (!string.IsNullOrEmpty(json))
                {
                    var config = JsonSerializer.Deserialize<SyncConfiguration>(json);
                    if (config != null)
                    {
                        retention = config.LogRetentionDays;
                        sizeLimit = config.LogFileSizeLimitMB;
                    }
                }
            }
        }
        catch
        {
            // Fallback to defaults on any error (e.g. DB locked, schema mismatch)
        }

        return (retention, sizeLimit);
    }
}