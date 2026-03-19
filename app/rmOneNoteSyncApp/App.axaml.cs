using System;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using rmOneNoteSyncApp.ViewModels;
using rmOneNoteSyncApp.Views;

namespace rmOneNoteSyncApp;

public partial class App : Application
{
    /// <summary>
    /// Gets the current App instance
    /// </summary>
    public static new App Current => (App)Application.Current!;

    /// <summary>
    /// Gets or sets the service provider for dependency injection
    /// </summary>
    public static IServiceProvider? ServiceProvider { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Read the Informational Version and strip the Git Hash if it exists
            string versionInfo = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            string cleanVersion = versionInfo.Split('+')[0];

            desktop.MainWindow = new MainWindow();
            desktop.MainWindow.Title = $"reMarkable OneNote Sync v{cleanVersion}";
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            DataContext = new ApplicationViewModel();
        }

        base.OnFrameworkInitializationCompleted();
    }
}