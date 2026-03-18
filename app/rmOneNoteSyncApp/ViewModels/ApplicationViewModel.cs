using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.Input;
using rmOneNoteSyncApp.Views;

namespace rmOneNoteSyncApp.ViewModels;

public partial class ApplicationViewModel: ViewModelBase
{
    private readonly IClassicDesktopStyleApplicationLifetime? _desktop = App.Current.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;

    [RelayCommand]
    private void ExitApplication()
    {
        _desktop?.Shutdown();
    }

    [RelayCommand]
    private void OpenApplication()
    {
        if (_desktop == null) return;
        var mainWindow = _desktop.MainWindow = new MainWindow();
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Show();
        mainWindow.BringIntoView();
        mainWindow.Focus();
    }
}