using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
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
            desktop.MainWindow = new MainWindow();
            desktop.MainWindow.Title = "reMarkable OneNote Sync v" + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            DataContext = new ApplicationViewModel();
        }

        base.OnFrameworkInitializationCompleted();
    }
}