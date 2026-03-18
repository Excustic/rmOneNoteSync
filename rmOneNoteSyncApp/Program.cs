using System;
using System.IO;
using System.Runtime.InteropServices;
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

namespace rmOneNoteSyncApp;

class Program
{
    [STAThread]
    public static void Main(string[] args)
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

                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Debug()
                    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
                    .Enrich.FromLogContext()
                    .WriteTo.Console()
                    .WriteTo.File(
                        logPath,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 7,
                        fileSizeLimitBytes: 10_000_000, // 10MB per file
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
}