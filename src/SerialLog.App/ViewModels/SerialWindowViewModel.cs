using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Windows;
using Microsoft.Win32;
using SerialLog.App.Infrastructure;
using SerialLog.Core.Collaboration;
using SerialLog.Core.Commands;
using SerialLog.Core.Logging;
using SerialLog.Core.Serial;

namespace SerialLog.App.ViewModels;

public sealed class SerialWindowViewModel : ObservableObject, ICommandTarget, IDisposable
{
    private const int MaxBufferedLines = 5000;
    private static readonly string[] CommonBaudRateOptions =
    [
        "1200",
        "2400",
        "4800",
        "9600",
        "19200",
        "38400",
        "57600",
        "115200",
        "230400",
        "460800",
        "921600",
        "1000000",
        "2000000"
    ];

    private readonly SerialPortSession _session;
    private readonly IClock _clock;
    private readonly Func<IEnumerable<string>> _portNameProvider;
    private RollingLogFileWriter? _writer;
    private string _title;
    private string? _portName;
    private int _baudRate = 115200;
    private bool _isSelectedForSend = true;
    private bool _autoSaveEnabled;
    private string _statusText = "未连接";
    private string _saveStatusText = "未保存";
    private string _searchKeyword = string.Empty;
    private string _logRootDirectory = @"D:\serial-log-data\logs";
    private string? _activeLogDirectory;
    private long _lineCount;
    private bool _shouldStayConnected;
    private DateTimeOffset _lastReconnectAttempt = DateTimeOffset.MinValue;
    private int _pageIndex;
    private string _ownerPcId = string.Empty;
    private string _ownerPcName = string.Empty;
    private string _ownerPcColor = string.Empty;
    private bool _isRemote;
    private bool _isRemoteOnline;
    private string _remoteWindowId = string.Empty;
    private Func<string, string, CancellationToken, Task>? _remoteCommandSender;

    public SerialWindowViewModel(
        string id,
        string title,
        IClock? clock = null,
        Func<IEnumerable<string>>? portNameProvider = null,
        bool refreshPortsOnCreate = true)
    {
        Id = id;
        _title = title;
        _clock = clock ?? new SystemClock();
        _portNameProvider = portNameProvider ?? SerialPort.GetPortNames;
        _session = new SerialPortSession(id, _clock);
        _session.LinesReceived += OnLinesReceived;
        _session.StatusChanged += (_, status) => RunOnUi(() => StatusText = status);
        RefreshPortsCommand = new RelayCommand(RefreshPorts);
        ConnectCommand = new RelayCommand(Connect);
        DisconnectCommand = new RelayCommand(Disconnect);
        ToggleConnectionCommand = new RelayCommand(ToggleConnection);
        ClearCommand = new RelayCommand(Clear);
        ExportCommand = new RelayCommand(Export);
        if (refreshPortsOnCreate)
        {
            RefreshPorts();
        }
    }

    public event EventHandler<IReadOnlyList<ReceivedLogLine>>? LinesReceived;

    public string Id { get; }

    public static string CreateRemoteId(string pcId, string windowId)
    {
        return $"remote:{pcId}:{windowId}";
    }

    public static SerialWindowViewModel CreateRemote(
        CollaborationClientSnapshot client,
        CollaborationWindowSnapshot snapshot,
        Func<string, string, CancellationToken, Task> sendCommandAsync,
        IClock? clock = null)
    {
        var window = new SerialWindowViewModel(
            CreateRemoteId(client.PcId, snapshot.Id),
            snapshot.Title,
            clock,
            refreshPortsOnCreate: false);
        window.UpdateRemoteSnapshot(client, snapshot, sendCommandAsync);
        return window;
    }

    public int PageIndex
    {
        get => _pageIndex;
        set => SetProperty(ref _pageIndex, Math.Max(0, value));
    }

    public ObservableCollection<string> AvailablePorts { get; } = [];

    public IReadOnlyList<string> BaudRateOptions => CommonBaudRateOptions;

    public ObservableCollection<LogLineViewModel> Lines { get; } = [];

    public RelayCommand RefreshPortsCommand { get; }

    public RelayCommand ConnectCommand { get; }

    public RelayCommand DisconnectCommand { get; }

    public RelayCommand ToggleConnectionCommand { get; }

    public RelayCommand ClearCommand { get; }

    public RelayCommand ExportCommand { get; }

    public string Title
    {
        get => _title;
        set
        {
            if (SetProperty(ref _title, value))
            {
                _writer = null;
            }
        }
    }

    public string? PortName
    {
        get => _portName;
        set => SetProperty(ref _portName, value);
    }

    public int BaudRate
    {
        get => _baudRate;
        set
        {
            if (value <= 0)
            {
                return;
            }

            if (SetProperty(ref _baudRate, value))
            {
                OnPropertyChanged(nameof(BaudRateText));
                ApplyBaudRateToConnectedPort(value);
            }
        }
    }

    public string BaudRateText
    {
        get => BaudRate.ToString(CultureInfo.InvariantCulture);
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) &&
                parsed > 0)
            {
                BaudRate = parsed;
                return;
            }

            StatusText = "波特率必须是大于 0 的整数";
            OnPropertyChanged();
        }
    }

    public bool IsSelectedForSend
    {
        get => _isSelectedForSend;
        set => SetProperty(ref _isSelectedForSend, value);
    }

    public bool AutoSaveEnabled
    {
        get => _autoSaveEnabled;
        set
        {
            if (SetProperty(ref _autoSaveEnabled, value))
            {
                SaveStatusText = value ? "自动保存已开" : "未保存";
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        set
        {
            if (SetProperty(ref _statusText, value))
            {
                OnPropertyChanged(nameof(ConnectionIndicatorBrush));
            }
        }
    }

    public string SaveStatusText
    {
        get => _saveStatusText;
        set => SetProperty(ref _saveStatusText, value);
    }

    public string SearchKeyword
    {
        get => _searchKeyword;
        set
        {
            if (SetProperty(ref _searchKeyword, value))
            {
                UpdateMatches();
            }
        }
    }

    public long LineCount
    {
        get => _lineCount;
        private set => SetProperty(ref _lineCount, value);
    }

    public bool IsRemote => _isRemote;

    public bool IsLocalSerial => !IsRemote;

    public string RemoteWindowId => _remoteWindowId;

    public bool IsConnected => IsRemote ? _isRemoteOnline : _session.IsConnected;

    public string ConnectionActionText => IsConnected ? "断开" : "连接";

    public string OwnerPcId
    {
        get => _ownerPcId;
        set
        {
            if (SetProperty(ref _ownerPcId, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasOwnerBadge));
            }
        }
    }

    public string OwnerPcName
    {
        get => _ownerPcName;
        set
        {
            if (SetProperty(ref _ownerPcName, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(OwnerBadgeText));
                OnPropertyChanged(nameof(HasOwnerBadge));
            }
        }
    }

    public string OwnerPcColor
    {
        get => _ownerPcColor;
        set
        {
            if (SetProperty(ref _ownerPcColor, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(OwnerBorderBrush));
                OnPropertyChanged(nameof(OwnerHeaderBrush));
            }
        }
    }

    public string OwnerBadgeText => OwnerPcName;

    public bool HasOwnerBadge => !string.IsNullOrWhiteSpace(OwnerPcName);

    public string OwnerBorderBrush => string.IsNullOrWhiteSpace(OwnerPcColor) ? "#D7DEE8" : OwnerPcColor;

    public string OwnerHeaderBrush => CreateOwnerHeaderBrush(OwnerPcColor);

    public string ConnectionIndicatorBrush
    {
        get
        {
            if (StatusText.Contains("失败", StringComparison.OrdinalIgnoreCase) ||
                StatusText.Contains("占用", StringComparison.OrdinalIgnoreCase) ||
                StatusText.Contains("被占用", StringComparison.OrdinalIgnoreCase) ||
                StatusText.Contains("未检测", StringComparison.OrdinalIgnoreCase) ||
                StatusText.Contains("无效", StringComparison.OrdinalIgnoreCase) ||
                StatusText.Contains("必须", StringComparison.OrdinalIgnoreCase))
            {
                return "#F59E0B";
            }

            if (IsConnected || StatusText.Contains("已连接", StringComparison.OrdinalIgnoreCase))
            {
                return "#16A34A";
            }

            return "#DC2626";
        }
    }

    private static string CreateOwnerHeaderBrush(string color)
    {
        if (string.IsNullOrWhiteSpace(color))
        {
            return "#F6F8FB";
        }

        var hex = color.Trim();
        if (hex.StartsWith("#", StringComparison.Ordinal))
        {
            hex = hex[1..];
        }

        if (hex.Length == 8)
        {
            hex = hex[2..];
        }

        if (hex.Length != 6 || hex.Any(ch => !Uri.IsHexDigit(ch)))
        {
            return "#F6F8FB";
        }

        return "#1A" + hex.ToUpperInvariant();
    }

    private void EnsureWriter()
    {
        if (!AutoSaveEnabled || string.IsNullOrWhiteSpace(Title))
        {
            return;
        }

        _activeLogDirectory ??= LogSessionPathFactory.CreateSessionDirectory(_logRootDirectory, _clock.Now);
        _writer ??= new RollingLogFileWriter(_activeLogDirectory, Title, 50L * 1024 * 1024, _clock);
    }

    public async Task SendAsync(string payload, CancellationToken cancellationToken)
    {
        if (IsRemote)
        {
            if (_remoteCommandSender is null || string.IsNullOrWhiteSpace(_remoteWindowId))
            {
                throw new InvalidOperationException("远程窗口未绑定协作发送器。");
            }

            await _remoteCommandSender(_remoteWindowId, payload, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _session.SendAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    public void Connect()
    {
        Connect(null);
    }

    public void Connect(string? sharedLogSessionDirectory)
    {
        if (IsRemote)
        {
            StatusText = IsConnected ? "远程已连接" : "远程未连接";
            return;
        }

        _shouldStayConnected = true;
        if (IsConnected)
        {
            StatusText = "已连接";
            return;
        }

        if (string.IsNullOrWhiteSpace(PortName))
        {
            StatusText = "请选择端口";
            return;
        }

        BeginNewLogSession(sharedLogSessionDirectory);
        try
        {
            _session.Open(PortName, BaudRate);
            NotifyConnectionStateChanged();
        }
        catch (Exception ex)
        {
            StatusText = ConnectionStatusText.FromException(PortName, ex);
            NotifyConnectionStateChanged();
        }
    }

    public void Disconnect()
    {
        if (IsRemote)
        {
            return;
        }

        _shouldStayConnected = false;
        _session.Close();
        NotifyConnectionStateChanged();
    }

    public void ToggleConnection()
    {
        if (IsConnected)
        {
            Disconnect();
            return;
        }

        Connect();
    }

    public void TryAutoReconnect()
    {
        if (IsRemote)
        {
            return;
        }

        if (!_shouldStayConnected || IsConnected || string.IsNullOrWhiteSpace(PortName))
        {
            return;
        }

        var now = DateTimeOffset.Now;
        if (now - _lastReconnectAttempt < TimeSpan.FromSeconds(3))
        {
            return;
        }

        _lastReconnectAttempt = now;
        StatusText = "自动重连中";
        Connect();
    }

    public void Clear()
    {
        Lines.Clear();
        LineCount = 0;
    }

    public void Export()
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出当前窗口日志",
            Filter = "Log files (*.log)|*.log|Text files (*.txt)|*.txt|All files (*.*)|*.*",
            FileName = $"{Title}_{DateTime.Now:yyyyMMdd_HHmmss}.log"
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        File.WriteAllLines(dialog.FileName, Lines.Select(line => line.Text));
        SaveStatusText = $"已导出 {Path.GetFileName(dialog.FileName)}";
    }

    public void RefreshPorts()
    {
        RefreshPorts(updateErrorStatus: true);
    }

    public void AutoRefreshPorts()
    {
        RefreshPorts(updateErrorStatus: false);
    }

    private void RefreshPorts(bool updateErrorStatus)
    {
        if (IsRemote)
        {
            return;
        }

        var selectedPort = PortName;
        string[] portNames;
        try
        {
            portNames = _portNameProvider()
                .OrderBy(name => name)
                .ToArray();
        }
        catch (Exception ex)
        {
            AvailablePorts.Clear();
            if (!string.IsNullOrWhiteSpace(selectedPort))
            {
                AvailablePorts.Add(selectedPort);
            }

            if (updateErrorStatus)
            {
                StatusText = $"刷新端口失败：{ex.Message}";
            }

            return;
        }

        var selectedPortReported = !string.IsNullOrWhiteSpace(selectedPort) &&
            portNames.Any(port => string.Equals(port, selectedPort, StringComparison.OrdinalIgnoreCase));

        AvailablePorts.Clear();
        foreach (var port in portNames)
        {
            AvailablePorts.Add(port);
        }

        if (string.IsNullOrWhiteSpace(selectedPort))
        {
            return;
        }

        if (!AvailablePorts.Any(port => string.Equals(port, selectedPort, StringComparison.OrdinalIgnoreCase)))
        {
            AvailablePorts.Insert(0, selectedPort);
        }

        if (!string.Equals(PortName, selectedPort, StringComparison.OrdinalIgnoreCase))
        {
            PortName = selectedPort;
        }

        if (IsConnected && !selectedPortReported)
        {
            StatusText = $"端口 {selectedPort} 未检测到";
        }
        else if (IsConnected && StatusText.Contains("未检测到", StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "已连接";
        }
    }

    public void ApplyLogRoot(string logRootDirectory)
    {
        _logRootDirectory = logRootDirectory;
        _writer = null;
        _activeLogDirectory = null;
    }

    public void BeginNewLogSession(string? sessionDirectory = null)
    {
        _activeLogDirectory = string.IsNullOrWhiteSpace(sessionDirectory)
            ? LogSessionPathFactory.CreateSessionDirectory(_logRootDirectory, _clock.Now)
            : sessionDirectory;
        _writer = null;
        if (AutoSaveEnabled)
        {
            SaveStatusText = $"日志目录：{_activeLogDirectory}";
        }
    }

    public void UpdateRemoteSnapshot(
        CollaborationClientSnapshot client,
        CollaborationWindowSnapshot snapshot,
        Func<string, string, CancellationToken, Task>? sendCommandAsync = null)
    {
        _isRemote = true;
        _remoteWindowId = snapshot.Id;
        _remoteCommandSender = sendCommandAsync ?? _remoteCommandSender;
        Title = snapshot.Title;
        PortName = snapshot.PortName;
        BaudRate = snapshot.BaudRate;
        OwnerPcId = client.PcId;
        OwnerPcName = client.PcName;
        OwnerPcColor = client.PcColor;
        LineCount = snapshot.LineCount;
        SetRemoteOnline(snapshot.IsConnected);
        AvailablePorts.Clear();
        if (!string.IsNullOrWhiteSpace(snapshot.PortName))
        {
            AvailablePorts.Add(snapshot.PortName);
        }

        OnPropertyChanged(nameof(IsRemote));
        OnPropertyChanged(nameof(IsLocalSerial));
        OnPropertyChanged(nameof(RemoteWindowId));
    }

    public void SetRemoteOnline(bool isOnline)
    {
        if (!IsRemote)
        {
            return;
        }

        _isRemoteOnline = isOnline;
        StatusText = isOnline ? "远程已连接" : "远程未连接";
        NotifyConnectionStateChanged();
    }

    public void AppendRemoteLine(ReceivedLogLine line)
    {
        RunOnUi(() => AddLine(line));
    }

    private void OnLinesReceived(object? sender, IReadOnlyList<ReceivedLogLine> lines)
    {
        LinesReceived?.Invoke(this, lines);
        RunOnUi(() =>
        {
            foreach (var line in lines)
            {
                AddLine(line);
            }
        });
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action);
    }

    private void AddLine(ReceivedLogLine line)
    {
        var item = new LogLineViewModel(line);
        item.IsMatch = IsMatch(item.Text);
        Lines.Add(item);
        LineCount++;

        while (Lines.Count > MaxBufferedLines)
        {
            Lines.RemoveAt(0);
        }

        if (AutoSaveEnabled)
        {
            try
            {
                EnsureWriter();
                _writer?.WriteLine(line);
                SaveStatusText = "自动保存中";
            }
            catch (Exception ex)
            {
                SaveStatusText = $"保存失败：{ex.Message}";
            }
        }
    }

    private void UpdateMatches()
    {
        foreach (var line in Lines)
        {
            line.IsMatch = IsMatch(line.Text);
        }
    }

    private bool IsMatch(string text)
    {
        return !string.IsNullOrWhiteSpace(SearchKeyword)
            && text.Contains(SearchKeyword, StringComparison.OrdinalIgnoreCase);
    }

    private void NotifyConnectionStateChanged()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(ConnectionActionText));
        OnPropertyChanged(nameof(ConnectionIndicatorBrush));
    }

    private void ApplyBaudRateToConnectedPort(int baudRate)
    {
        if (IsRemote || !IsConnected)
        {
            return;
        }

        try
        {
            _session.ChangeBaudRate(baudRate);
        }
        catch (Exception ex)
        {
            StatusText = $"更新波特率失败：{ex.Message}";
        }
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}
