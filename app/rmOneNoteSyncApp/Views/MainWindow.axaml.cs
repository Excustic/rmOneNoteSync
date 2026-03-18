using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using rmOneNoteSyncApp.ViewModels;

namespace rmOneNoteSyncApp.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = App.ServiceProvider?.GetRequiredService<MainViewModel>();
    }
}