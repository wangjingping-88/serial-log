using System.ComponentModel;
using System.Windows;
using SerialLog.Update;

namespace SerialLog.App.Views;

public partial class UpdateCheckWindow : Window
{
    private readonly Func<CancellationToken, Task<UpdateCheckResult>> _checkAsync;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _checkStarted;
    private bool _allowClose;

    public UpdateCheckWindow(Func<CancellationToken, Task<UpdateCheckResult>> checkAsync)
    {
        InitializeComponent();
        _checkAsync = checkAsync;
    }

    public UpdateCheckResult? Result { get; private set; }

    private async void Window_ContentRendered(object? sender, EventArgs e)
    {
        if (_checkStarted)
        {
            return;
        }

        _checkStarted = true;
        try
        {
            Result = await _checkAsync(_cancellation.Token);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            Result = UpdateCheckResult.Failed(exception.Message);
        }

        if (_cancellation.IsCancellationRequested)
        {
            return;
        }

        _allowClose = true;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelAndClose();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        _cancellation.Cancel();
        _allowClose = true;
    }

    private void CancelAndClose()
    {
        _cancellation.Cancel();
        _allowClose = true;
        DialogResult = false;
    }
}
