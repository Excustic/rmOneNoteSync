using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace rmOneNoteSyncApp.ViewModels;

public partial class InputDialogViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _title = "Input Request";

    [ObservableProperty]
    private string _message = "Please enter a value:";

    [ObservableProperty]
    private string _inputText = "";

    [ObservableProperty]
    private string _watermark = "";

    public InputDialogViewModel()
    {
    }

    // A reference to the window's close method that returns the result
    public System.Action<string?>? CloseAction { get; set; }

    [RelayCommand]
    private void Confirm()
    {
        CloseAction?.Invoke(InputText);
    }

    [RelayCommand]
    private void Cancel()
    {
        CloseAction?.Invoke(null);
    }
}
