using System.Collections.ObjectModel;
using System.IO;
using System.IO.Ports;
using System.Windows;
using Microsoft.Win32;
using SerialLog.App.Infrastructure;
using SerialLog.Core.Commands;
using SerialLog.Core.Logging;
using SerialLog.Core.Serial;

namespace SerialLog.App.ViewModels;

public sealed class SerialWindowViewModel : ObservableObject, ICommandTarget, IDisposable
{
    private const int MaxBufferedLines = 5000;
    private readonly SerialPortSession _session;
    private readonly IClock _clock;
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

    public SerialWindowViewModel(string id, string title, IClock? clock = null)
    {
        Id = id;
        _title = title;
        _clock = clock ?? new SystemClock();
        _session = new SerialPortSession(id, _clock);
        _session.LinesReceived += OnLinesReceived;
        _session.StatusChanged += (_, status) => StatusText = status;
        RefreshPortsCommand = new RelayCommand(RefreshPorts);
        ConnectCommand = new RelayCommand(Connect);
        DisconnectCommand = new RelayCommand(Disconnect);
        ToggleConnectionCommand = new RelayCommand(ToggleConnection);
        ClearCommand = new RelayCommand(Clear);
        ExportCommand = new RelayCommand(Export);
        RefreshPorts();
    }

    public string Id { get; }

    public ObservableCollection<string> AvailablePorts { get; } = [];

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
        set => SetProperty(ref _baudRate, value);
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

    public bool IsConnected => _session.IsConnected;

    public string ConnectionActionText => IsConnected ? "断开" : "连接";

    public string ConnectionIndicatorBrush
    {
        get
        {
            if (IsConnected || StatusText.Contains("已连接", StringComparison.OrdinalIgnoreCase))
            {
                return "#16A34A";
            }

            if (StatusText.Contains("失败", StringComparison.OrdinalIgnoreCase) ||
                StatusText.Contains("占用", StringComparison.OrdinalIgnoreCase) ||
                StatusText.Contains("被占用", StringComparison.OrdinalIgnoreCase))
            {
                return "#F59E0B";
            }

            return "#DC2626";
        }
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
        await _session.SendAsync(payload, cancellationToken);
    }

    public void Connect()
    {
        Connect(null);
    }

    public void Connect(string? sharedLogSessionDirectory)
    {
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
        var selectedPort = PortName;
        AvailablePorts.Clear();
        foreach (var port in SerialPort.GetPortNames().OrderBy(name => name))
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

    private void OnLinesReceived(object? sender, IReadOnlyList<ReceivedLogLine> lines)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            foreach (var line in lines)
            {
                AddLine(line);
            }
        });
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

    public void Dispose()
    {
        _session.Dispose();
    }
}
