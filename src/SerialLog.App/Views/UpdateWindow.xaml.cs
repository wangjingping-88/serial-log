using System.Diagnostics;
using System.Windows;
using SerialLog.Update;

namespace SerialLog.App.Views;

public partial class UpdateWindow : Window
{
    private readonly IUpdateService _updateService;
    private readonly UpdateReleaseInfo _release;
    private readonly string _installDirectory;
    private readonly bool _canInstallInPlace;
    private CancellationTokenSource? _downloadCancellation;
    private bool _isDownloading;

    public UpdateWindow(
        IUpdateService updateService,
        UpdateReleaseInfo release,
        string currentVersion,
        string installDirectory,
        bool canInstallInPlace,
        string installRestrictionReason)
    {
        InitializeComponent();
        _updateService = updateService;
        _release = release;
        _installDirectory = installDirectory;
        _canInstallInPlace = canInstallInPlace;

        CurrentVersionText.Text = currentVersion;
        LatestVersionText.Text = $"v{release.Version}";
        ReleaseNotesTextBox.Text = release.ReleaseNotes;
        if (canInstallInPlace)
        {
            InstallModeText.Text =
                $"便携版更新包约 {FormatBytes(release.PackageAsset.Size)}。安装时会保存工作区、断开串口并重启应用，多机协作电脑建议同步更新。";
        }
        else
        {
            InstallModeText.Text = installRestrictionReason;
            InstallButton.Content = "打开发布页";
        }
    }

    public PreparedUpdate? PreparedUpdate { get; private set; }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_isDownloading)
        {
            _downloadCancellation?.Cancel();
        }

        _downloadCancellation?.Dispose();

        base.OnClosing(e);
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_canInstallInPlace)
        {
            OpenReleasePage();
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            "下载完成后 Serial Log 将保存当前工作区、断开串口与多机协作，然后自动重启。是否继续？",
            "确认安装更新",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        SetDownloadingState(true);
        _downloadCancellation?.Dispose();
        _downloadCancellation = new CancellationTokenSource();
        var progress = new Progress<UpdateDownloadProgress>(UpdateProgress);
        try
        {
            PreparedUpdate = await _updateService.DownloadAndPrepareAsync(
                _release,
                _installDirectory,
                Environment.ProcessId,
                progress,
                _downloadCancellation.Token);
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            ProgressText.Text = "下载已取消。";
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"更新准备失败，当前版本未发生变化。\n\n{exception.Message}",
                "更新失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetDownloadingState(false);
        }
    }

    private void UpdateProgress(UpdateDownloadProgress progress)
    {
        DownloadProgressBar.Visibility = Visibility.Visible;
        ProgressText.Visibility = Visibility.Visible;
        if (progress.TotalBytes is > 0)
        {
            DownloadProgressBar.IsIndeterminate = false;
            DownloadProgressBar.Value = Math.Clamp(
                progress.BytesReceived * 100d / progress.TotalBytes.Value,
                0,
                100);
        }
        else
        {
            DownloadProgressBar.IsIndeterminate = true;
        }

        var received = progress.BytesReceived > 0 ? FormatBytes(progress.BytesReceived) : string.Empty;
        var total = progress.TotalBytes is > 0 ? $" / {FormatBytes(progress.TotalBytes.Value)}" : string.Empty;
        var speed = progress.BytesPerSecond > 0 ? $"，{FormatBytes((long)progress.BytesPerSecond)}/s" : string.Empty;
        ProgressText.Text = $"{progress.Stage} {received}{total}{speed}".TrimEnd();
    }

    private void SetDownloadingState(bool downloading)
    {
        _isDownloading = downloading;
        InstallButton.IsEnabled = !downloading;
        CancelButton.Content = downloading ? "取消下载" : "稍后";
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isDownloading)
        {
            _downloadCancellation?.Cancel();
            return;
        }

        DialogResult = false;
    }

    private void OpenReleasePageButton_Click(object sender, RoutedEventArgs e)
    {
        OpenReleasePage();
    }

    private void OpenReleasePage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_release.ReleasePageUri.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"无法打开发布页。\n\n{exception.Message}", "打开失败");
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)Math.Max(bytes, 0);
        var unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }
}
