using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using SerialLog.Update;

namespace SerialLog.App.Views;

public partial class UpdateCheckWindow : Window
{
    private readonly Func<CancellationToken, Task<UpdateCheckResult>> _checkAsync;
    private readonly string _currentVersion;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _checkStarted;
    private bool _checkCompleted;
    private bool _allowClose;

    public UpdateCheckWindow(
        Func<CancellationToken, Task<UpdateCheckResult>> checkAsync,
        string currentVersion)
    {
        InitializeComponent();
        _checkAsync = checkAsync;
        _currentVersion = currentVersion;
    }

    public UpdateCheckResult? Result { get; private set; }

    public bool OpenReleasePageRequested { get; private set; }

    public bool WasCanceled { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source &&
            source.CompositionTarget is not null)
        {
            source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
        }
    }

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

        switch (Result.Status)
        {
            case UpdateCheckStatus.NoUpdate:
                ShowResult(
                    "已是最新版本",
                    $"当前版本 {_currentVersion} 已是最新版本。",
                    "无需执行任何操作。");
                return;
            case UpdateCheckStatus.Failed:
                ShowResult(
                    "检查更新失败",
                    "未能获取最新版本信息。",
                    Result.ErrorMessage ?? "请检查网络连接后重试。",
                    showReleaseButton: true);
                return;
            case UpdateCheckStatus.Skipped:
            case UpdateCheckStatus.UpdateAvailable:
                _allowClose = true;
                DialogResult = true;
                return;
        }
    }

    private void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_checkCompleted)
        {
            CancelAndClose();
            return;
        }

        _allowClose = true;
        DialogResult = true;
    }

    private void SecondaryButton_Click(object sender, RoutedEventArgs e)
    {
        OpenReleasePageRequested = true;
        _allowClose = true;
        DialogResult = true;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || _checkCompleted)
        {
            return;
        }

        WasCanceled = true;
        _cancellation.Cancel();
        _allowClose = true;
    }

    private void CancelAndClose()
    {
        WasCanceled = true;
        _cancellation.Cancel();
        _allowClose = true;
        DialogResult = false;
    }

    private void ShowResult(
        string title,
        string detail,
        string hint,
        bool showReleaseButton = false)
    {
        _checkCompleted = true;
        TitleText.Text = title;
        DetailText.Text = detail;
        HintText.Text = hint;
        CheckingProgressBar.Visibility = Visibility.Collapsed;
        SecondaryButton.Visibility = showReleaseButton ? Visibility.Visible : Visibility.Collapsed;
        PrimaryButton.Content = showReleaseButton ? "关闭" : "确定";
        Activate();
        InvalidateVisual();
        UpdateLayout();
    }
}
