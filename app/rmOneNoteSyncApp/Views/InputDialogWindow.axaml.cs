using Avalonia.Controls;
using rmOneNoteSyncApp.ViewModels;

namespace rmOneNoteSyncApp.Views;

public partial class InputDialogWindow : Window
{
    public InputDialogWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is InputDialogViewModel vm)
        {
            vm.CloseAction = (result) => Close(result);
        }
    }
}
