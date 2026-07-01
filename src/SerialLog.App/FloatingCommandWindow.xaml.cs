using System.Windows;
using System.Windows.Input;
using SerialLog.App.ViewModels;

namespace SerialLog.App;

public partial class FloatingCommandWindow : Window
{
    private bool _closedByRestore;

    public FloatingCommandWindow()
    {
        InitializeComponent();
    }

    public void CloseFromMainWindow()
    {
        _closedByRestore = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        if (!_closedByRestore && DataContext is MainViewModel viewModel && viewModel.RestoreCommandPanelCommand.CanExecute(null))
        {
            viewModel.RestoreCommandPanelCommand.Execute(null);
        }

        base.OnClosed(e);
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
