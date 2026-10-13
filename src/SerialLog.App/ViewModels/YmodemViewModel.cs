using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using Microsoft.Win32;
using SerialLog.App.Infrastructure;
using SerialLog.App.Views;
using SerialLog.Core.Firmware;
using SerialLog.Core.Serial;

namespace SerialLog.App.ViewModels;

public sealed class YmodemTargetProgress : ObservableObject
{
    private readonly object _progressLock = new();
    private string _status = "准备中";
    private long _acknowledged;
    private double _speed;
    private TimeSpan _elapsed;
    private bool _running;
    internal YmodemTargetProgress(SerialWindowViewModel target, long total, CancellationToken parent)
    {
        Target = target; TotalBytes = total; Label = target.FirmwareTargetLabel;
        Cancellation = CancellationTokenSource.CreateLinkedTokenSource(parent);
        CancelCommand = new RelayCommand(() => Cancellation.Cancel(), () => IsRunning);
    }
    internal SerialWindowViewModel Target { get; }
    internal CancellationTokenSource Cancellation { get; }
    internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string Label { get; }
    public long TotalBytes { get; }
    public bool IsRunning { get => _running; internal set { if (SetProperty(ref _running, value)) CancelCommand.RaiseCanExecuteChanged(); } }
    public string Status { get => _status; internal set => SetProperty(ref _status, value); }
    public double Percentage => TotalBytes == 0 ? 0 : 100.0 * _acknowledged / TotalBytes;
    public string ProgressText => $"{Percentage:0.0}% · {_acknowledged:N0}/{TotalBytes:N0} 字节 · {_speed / 1024:0.0} KB/s · {_elapsed:hh\\:mm\\:ss}";
    public RelayCommand CancelCommand { get; }
    internal void Report(YmodemProgress p)
    {
        lock (_progressLock)
        {
        _acknowledged = p.AcknowledgedBytes;
        if (p.Elapsed > TimeSpan.Zero) { _speed = p.BytesPerSecond; _elapsed = p.Elapsed; }
        Status = p.Stage; OnPropertyChanged(nameof(Percentage)); OnPropertyChanged(nameof(ProgressText));
        }
    }
    internal bool ReportWhileRunning(YmodemProgress p)
    {
        lock (_progressLock)
        {
            if (!IsRunning) return false;
            Report(p); return true;
        }
    }
    internal void Complete(string message, bool transferred = false)
    {
        lock (_progressLock)
        {
            IsRunning = false;
            if (transferred) _acknowledged = TotalBytes;
            Status = message;
            OnPropertyChanged(nameof(Percentage)); OnPropertyChanged(nameof(ProgressText));
        }
    }
}

/// <summary>一个工作区一个批次，同一固件并行传输；目标、规则和文件在开始时固定。</summary>
public sealed class YmodemViewModel : ObservableObject, IDisposable
{
    private readonly ObservableCollection<SerialWindowViewModel> _windows;
    private readonly CommandPanelViewModel _commands;
    private readonly Action<string> _setStatus;
    private string _filePath = "";
    private long _fileSize;
    private YmodemMode _mode = YmodemMode.Standard;
    private bool _isRunning;
    private string _status = "在左侧“目标窗口”勾选本地窗口；全部目标使用同一个固件文件。";
    private string? _selectedProfileId;
    private CancellationTokenSource? _cancellation;
    private Task _runTask = Task.CompletedTask;
    private bool _disposed;
    internal Func<SerialWindowViewModel, CancellationToken, Task<IBinarySerialTransport>>? TransportFactory { get; set; }

    public YmodemViewModel(ObservableCollection<SerialWindowViewModel> windows, CommandPanelViewModel commands, Action<string> setStatus)
    {
        _windows = windows; _commands = commands; _setStatus = setStatus;
        var example = YmodemRuleProfiles.CreateUcchipExample(); Profiles.Add(example); _selectedProfileId = example.Id;
        BrowseCommand = new RelayCommand(Browse, () => !IsRunning);
        RulesCommand = new RelayCommand(EditRules);
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsRunning);
        CancelCommand = new RelayCommand(() => _cancellation?.Cancel(), () => IsRunning);
    }
    public ObservableCollection<YmodemRuleProfile> Profiles { get; } = [];
    public ObservableCollection<YmodemTargetProgress> TargetProgress { get; } = [];
    public RelayCommand BrowseCommand { get; }
    public RelayCommand RulesCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public RelayCommand CancelCommand { get; }
    public bool IsRunning => _isRunning;
    public bool CanEdit => !IsRunning;
    public bool CanSelectProfile => CanEdit && Mode == YmodemMode.Custom;
    public string FilePath
    {
        get => _filePath;
        set
        {
            if (IsRunning || !SetProperty(ref _filePath, value ?? "")) return;
            try { _fileSize = File.Exists(_filePath) ? new FileInfo(_filePath).Length : 0; } catch { _fileSize = 0; }
            OnPropertyChanged(nameof(FileSizeText));
        }
    }
    public YmodemMode Mode
    {
        get => _mode;
        set
        {
            if (!IsRunning && SetProperty(ref _mode, Enum.IsDefined(value) ? value : YmodemMode.Standard))
            { OnPropertyChanged(nameof(ModeIndex)); OnPropertyChanged(nameof(CanSelectProfile)); }
        }
    }
    public int ModeIndex { get => (int)Mode; set { if (value is 0 or 1) Mode = (YmodemMode)value; } }
    public string? SelectedProfileId
    {
        get => _selectedProfileId;
        set { if (!IsRunning && SetProperty(ref _selectedProfileId, value)) OnPropertyChanged(nameof(SelectedProfile)); }
    }
    public YmodemRuleProfile? SelectedProfile => Profiles.FirstOrDefault(p => p.Id == SelectedProfileId);
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string FileSizeText => $"文件大小：{_fileSize:N0} 字节";
    public void LoadProfiles(IEnumerable<YmodemRuleProfile> profiles, string? selectedId)
    {
        Profiles.Clear(); foreach (var profile in profiles) Profiles.Add(YmodemRuleProfiles.Clone(profile));
        _selectedProfileId = Profiles.Any(p => p.Id == selectedId) ? selectedId : Profiles.FirstOrDefault()?.Id;
        OnPropertyChanged(nameof(Profiles)); OnPropertyChanged(nameof(SelectedProfileId)); OnPropertyChanged(nameof(SelectedProfile));
    }
    private void EditRules()
    {
        var dialog = new YmodemRulesWindow(Profiles, SelectedProfileId)
        { Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow };
        if (dialog.ShowDialog() == true) LoadProfiles(dialog.Profiles, dialog.SelectedProfileId);
    }
    private void Browse()
    {
        var dialog = new OpenFileDialog { Title = "选择 Ymodem 固件", Filter = "固件文件 (*.bin)|*.bin", CheckFileExists = true };
        if (dialog.ShowDialog() == true) FilePath = dialog.FileName;
    }
    public Task StartAsync()
    {
        if (IsRunning || _disposed) return _runTask;
        _runTask = RunBatchAsync(); return _runTask;
    }
    private async Task RunBatchAsync()
    {
        var targets = _windows.Where(w => !w.IsRemote && w.IsSelectedForSend).ToArray();
        var path = FilePath;
        var profile = Mode == YmodemMode.Custom && SelectedProfile is { } selected ? YmodemRuleProfiles.Clone(selected) : null;
        FileStream? guard = null;
        try
        {
            if (targets.Length == 0) throw new InvalidOperationException("请在左侧“目标窗口”选择至少一个本地串口窗口。");
            if (profile is not null) YmodemRuleValidator.Validate(profile);
            if (!string.Equals(Path.GetExtension(path), ".bin", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("请选择 .bin 固件文件。");
            guard = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (guard.Length == 0) throw new InvalidDataException("固件文件为空。");
        }
        catch (Exception e) { guard?.Dispose(); Status = e.Message; _setStatus($"Ymodem：{Status}"); return; }
        using (guard)
        using (var batch = new CancellationTokenSource())
        {
            _cancellation = batch;
            foreach (var old in TargetProgress) old.Cancellation.Dispose();
            TargetProgress.Clear(); _fileSize = guard.Length; OnPropertyChanged(nameof(FileSizeText));
            var runnable = new List<YmodemTargetProgress>();
            foreach (var target in targets)
            {
                var row = new YmodemTargetProgress(target, guard.Length, batch.Token); TargetProgress.Add(row);
                string? error = !target.IsConnected || target.IsConnectionPending ? "目标串口未连接，请先连接。" :
                    target.IsFirmwareBusy ? "目标已有烧录任务。" :
                    _commands.HasRunningLoopForTarget(target.Id) ? "目标参与循环命令，请先停止相关循环。" :
                    profile?.RequiredBaudRate > 0 && profile.RequiredBaudRate != target.BaudRate ? $"方案要求波特率 {profile.RequiredBaudRate}，请调整后重试。" : null;
                if (error is not null) { row.Status = error; row.Completion.TrySetResult(); }
                else { target.SetFirmwareBusy(true); row.IsRunning = true; runnable.Add(row); }
            }
            if (runnable.Count == 0) { _cancellation = null; Status = "未开始：所选目标均不符合条件，请查看各窗口原因。"; _setStatus($"Ymodem：{Status}"); return; }
            SetRunning(true);
            try
            {
                Status = "计算固件摘要，准备并行烧录";
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(guard, batch.Token));
                await Task.WhenAll(runnable.Select(row => RunTargetAsync(row, path, hash, profile)));
                Status = batch.IsCancellationRequested ? "烧录批次已取消；各目标结果如下。" : "烧录批次已结束；各目标结果如下。";
            }
            catch (OperationCanceledException) { Status = "烧录批次已取消。"; }
            catch (Exception e) { Status = $"烧录批次未开始：{e.Message}"; }
            finally
            {
                foreach (var row in runnable.Where(r => !r.Completion.Task.IsCompleted))
                {
                    row.Target.SetFirmwareBusy(false); row.Complete(Status); row.Completion.TrySetResult();
                }
                _cancellation = null; SetRunning(false); _setStatus($"Ymodem：{Status}");
            }
        }
    }
    private async Task RunTargetAsync(YmodemTargetProgress row, string path, string hash, YmodemRuleProfile? profile)
    {
        var target = row.Target; var token = row.Cancellation.Token;
        var lastUi = Stopwatch.StartNew(); string lastStage = "";
        var finalMessage = "烧录失败：任务未完成";
        var fileTransferred = false;
        try
        {
            token.ThrowIfCancellationRequested();
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            target.AppendFirmwareDiagnostic($"开始 · {(profile?.Name ?? "标准 Ymodem")} · {Path.GetFileName(path)} · {file.Length} 字节 · SHA256={hash}");
            await using var transport = await (TransportFactory?.Invoke(target, token) ?? target.AcquireFirmwareTransportAsync(token));
            var progress = new Progress<YmodemProgress>(p =>
            {
                if (!row.IsRunning || _disposed) return;
                if ((p.Stage == "传输固件" || p.Stage.StartsWith("传输数据包")) && lastUi.ElapsedMilliseconds < 100) return;
                lastUi.Restart(); if (!row.ReportWhileRunning(p)) return;
                if (p.Stage != lastStage && !(p.Stage == "传输固件" || p.Stage.StartsWith("传输数据包"))) target.AppendFirmwareDiagnostic(p.Stage);
                lastStage = p.Stage;
            });
            var result = await new YmodemJobRunner().RunAsync(transport, file, Path.GetFileName(path), profile, progress, token);
            finalMessage = result.Message; fileTransferred = result.FileTransferred;
        }
        catch (OperationCanceledException) { finalMessage = "烧录已取消；设备可能仍处于 Bootloader。"; }
        catch (Exception e) { finalMessage = $"烧录失败：{e.Message}"; }
        finally
        {
            row.Complete(finalMessage, fileTransferred); target.SetFirmwareBusy(false);
            target.AppendFirmwareDiagnostic(row.Status); row.Completion.TrySetResult();
        }
    }
    public async Task CancelAndWaitAsync(string? targetId = null)
    {
        if (targetId is null) { _cancellation?.Cancel(); await _runTask; return; }
        var row = TargetProgress.FirstOrDefault(r => r.Target.Id == targetId && r.IsRunning);
        if (row is null) return;
        row.Cancellation.Cancel(); await row.Completion.Task;
    }
    private void SetRunning(bool value)
    {
        SetProperty(ref _isRunning, value, nameof(IsRunning));
        OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanSelectProfile));
        BrowseCommand.RaiseCanExecuteChanged(); StartCommand.RaiseCanExecuteChanged(); CancelCommand.RaiseCanExecuteChanged();
    }
    public void Dispose()
    {
        _disposed = true; _cancellation?.Cancel();
        if (!IsRunning) foreach (var row in TargetProgress) row.Cancellation.Dispose();
    }
}
