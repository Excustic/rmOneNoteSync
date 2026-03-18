using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using System.ComponentModel;
using rmOneNoteSyncApp.ViewModels;

namespace rmOneNoteSyncApp.Views;

public partial class LogsView : UserControl
{
    private TextBox? _logTextBox;
    private ScrollViewer? _logScrollViewer;
    private Button? _scrollUpButton;
    private Button? _scrollDownButton;
    private LogsViewModel? _viewModel;

    public LogsView()
    {
        InitializeComponent();
        
        // Find controls
        _logTextBox = this.FindControl<TextBox>("LogTextBox");
        _logScrollViewer = this.FindControl<ScrollViewer>("LogScrollViewer");
        _scrollUpButton = this.FindControl<Button>("ScrollUpButton");
        _scrollDownButton = this.FindControl<Button>("ScrollDownButton");
        
        // Wire up buttons
        if (_scrollUpButton != null)
        {
            _scrollUpButton.Click += (s, e) => _logScrollViewer?.ScrollToHome();
        }
        
        if (_scrollDownButton != null)
        {
            _scrollDownButton.Click += (s, e) => _logScrollViewer?.ScrollToEnd();
        }
        
        this.DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_viewModel != null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (this.DataContext is LogsViewModel vm)
        {
            _viewModel = vm;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LogsViewModel.LogContent))
        {
            if (_viewModel != null && _viewModel.IsTailing)
            {
                // Defer to UI thread layout pass
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    _logScrollViewer?.ScrollToEnd();
                });
            }
        }
    }
}