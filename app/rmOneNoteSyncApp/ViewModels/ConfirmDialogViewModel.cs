using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Controls;

namespace rmOneNoteSyncApp.ViewModels;

public partial class ConfirmDialogViewModel : ObservableObject
{
    private readonly Window _dialog;

    public string Message { get; }

    public ConfirmDialogViewModel(Window dialog, string message)
    {
        _dialog = dialog;
        Message = message;
    }

    [RelayCommand]
    private void Confirm() => _dialog.Close(true);

    [RelayCommand]
    private void Cancel() => _dialog.Close(false);
}