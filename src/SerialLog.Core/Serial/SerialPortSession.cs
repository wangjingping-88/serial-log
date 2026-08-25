using System.IO.Ports;
using System.Text;
using SerialLog.Core.Commands;
using SerialLog.Core.Logging;

namespace SerialLog.Core.Serial;

public sealed class SerialPortSession : ICommandTarget, IDisposable
{
    internal static readonly TimeSpan DefaultReceiveSilenceTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan DefaultReceiveHealthCheckInterval = TimeSpan.FromSeconds(5);
    private readonly IClock _clock;
    private readonly TimeSpan _receiveSilenceTimeout;
    private readonly TimeSpan _receiveHealthCheckInterval;
    private readonly LogLineParser _parser = new();
    private readonly object _receiveLock = new();
    private readonly object _serialPortLock = new();
    private SerialPort? _serialPort;
    private Timer? _receiveHealthTimer;
    private DateTimeOffset _lastReceiveActivity;
    private long _connectionGeneration;
    private int _isConnected;
    private int _isDisposed;

    private static readonly TimeSpan DriverCloseTimeout = TimeSpan.FromSeconds(3);

    public SerialPortSession(
        string id,
        IClock? clock = null,
        TimeSpan? receiveSilenceTimeout = null,
        TimeSpan? receiveHealthCheckInterval = null)
    {
        Id = id;
        _clock = clock ?? new SystemClock();
        _receiveSilenceTimeout = receiveSilenceTimeout ?? DefaultReceiveSilenceTimeout;
        _receiveHealthCheckInterval = receiveHealthCheckInterval ?? DefaultReceiveHealthCheckInterval;
        if (_receiveSilenceTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(receiveSilenceTimeout), "接收静默超时必须大于 0。");
        }

        if (_receiveHealthCheckInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(receiveHealthCheckInterval), "接收健康检查间隔必须大于 0。");
        }
    }

    public string Id { get; }

    public string? PortName { get; private set; }

    public int BaudRate { get; private set; } = 115200;

    public bool IsConnected
    {
        get => Volatile.Read(ref _isConnected) == 1;
    }

    public event EventHandler<IReadOnlyList<ReceivedLogLine>>? LinesReceived;

    public event EventHandler<string>? StatusChanged;

    public event EventHandler? ConnectionStateChanged;

    public void Open(string portName, int baudRate)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) == 1, this);
        Close();
        var generation = Volatile.Read(ref _connectionGeneration);
        _parser.Reset();
        PortName = portName;
        BaudRate = baudRate;
        var serialPort = new SerialPort(portName, baudRate)
        {
            Encoding = Encoding.UTF8,
            NewLine = "\n",
            ReadTimeout = 500,
            WriteTimeout = 500
        };
        serialPort.DataReceived += OnDataReceived;
        serialPort.ErrorReceived += OnErrorReceived;
        try
        {
            serialPort.Open();
        }
        catch
        {
            serialPort.DataReceived -= OnDataReceived;
            serialPort.ErrorReceived -= OnErrorReceived;
            ScheduleDriverCleanup(serialPort, portName);

            throw;
        }

        lock (_serialPortLock)
        {
            if (generation != Volatile.Read(ref _connectionGeneration))
            {
                ScheduleDriverCleanup(serialPort, portName);
                throw new OperationCanceledException("串口连接已取消。");
            }

            _serialPort = serialPort;
            _lastReceiveActivity = _clock.Now;
            Volatile.Write(ref _isConnected, 1);
            _receiveHealthTimer = new Timer(
                _ => CheckReceiveHealth(serialPort, generation),
                null,
                _receiveHealthCheckInterval,
                _receiveHealthCheckInterval);
        }

        StatusChanged?.Invoke(this, "已连接");
        ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Close()
    {
        Interlocked.Increment(ref _connectionGeneration);
        SerialPort? serialPort;
        Timer? receiveHealthTimer;
        lock (_serialPortLock)
        {
            serialPort = _serialPort;
            _serialPort = null;
            receiveHealthTimer = _receiveHealthTimer;
            _receiveHealthTimer = null;
            Volatile.Write(ref _isConnected, 0);
        }

        receiveHealthTimer?.Dispose();

        if (serialPort is null)
        {
            return;
        }

        serialPort.DataReceived -= OnDataReceived;
        serialPort.ErrorReceived -= OnErrorReceived;
        ScheduleDriverCleanup(serialPort, PortName);
        _parser.Reset();
        StatusChanged?.Invoke(this, "未连接");
        ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ChangeBaudRate(int baudRate)
    {
        if (baudRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baudRate), "波特率必须大于 0。");
        }

        SerialPort? serialPort;
        lock (_serialPortLock)
        {
            serialPort = _serialPort;
            if (serialPort is null || !IsConnected)
            {
                BaudRate = baudRate;
                return;
            }
        }

        serialPort.BaudRate = baudRate;
        BaudRate = baudRate;

        StatusChanged?.Invoke(this, $"已更新波特率：{baudRate}");
    }

    public Task SendAsync(string payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            SerialPort? serialPort;
            lock (_serialPortLock)
            {
                serialPort = _serialPort;
                if (serialPort is null || !IsConnected)
                {
                    throw new InvalidOperationException("串口未连接。");
                }
            }

            try
            {
                serialPort.Write(payload);
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
            {
                TransitionToFaulted(serialPort, $"串口发送失败：{ex.Message}");
                throw;
            }
        }, cancellationToken);
    }

    public IReadOnlyList<string> GetAvailablePorts()
    {
        return SerialPort.GetPortNames().OrderBy(name => name).ToArray();
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (sender is not SerialPort serialPort || !IsCurrentConnection(serialPort))
        {
            return;
        }

        try
        {
            IReadOnlyList<ReceivedLogLine> lines;
            lock (_receiveLock)
            {
                lines = ReadAvailableLines(serialPort);
            }
            if (lines.Count > 0)
            {
                LinesReceived?.Invoke(this, lines);
            }
        }
        catch (Exception ex)
        {
            TransitionToFaulted(serialPort, $"串口接收失败：{ex.Message}");
        }
    }

    private void OnErrorReceived(object sender, SerialErrorReceivedEventArgs e)
    {
        if (sender is not SerialPort serialPort || !IsCurrentConnection(serialPort))
        {
            return;
        }

        var message = $"串口驱动报告错误：{e.EventType}";
        if (e.EventType is SerialError.RXOver or SerialError.Overrun)
        {
            TransitionToFaulted(serialPort, message);
            return;
        }

        ReportDiagnostic(message);
    }

    private IReadOnlyList<ReceivedLogLine> ReadAvailableLines(SerialPort serialPort)
    {
        var lines = new List<ReceivedLogLine>();
        while (serialPort.IsOpen && serialPort.BytesToRead > 0)
        {
            var available = serialPort.BytesToRead;
            var buffer = new byte[Math.Min(available, 8192)];
            var read = serialPort.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            MarkReceiveActivity(serialPort);
            lines.AddRange(_parser.Append(buffer.AsSpan(0, read), _clock.Now));
        }

        return lines;
    }

    private bool IsCurrentConnection(SerialPort serialPort)
    {
        lock (_serialPortLock)
        {
            return ReferenceEquals(_serialPort, serialPort) && IsConnected;
        }
    }

    private void MarkReceiveActivity(SerialPort serialPort)
    {
        lock (_serialPortLock)
        {
            if (ReferenceEquals(_serialPort, serialPort) && IsConnected)
            {
                _lastReceiveActivity = _clock.Now;
            }
        }
    }

    private void CheckReceiveHealth(SerialPort serialPort, long generation)
    {
        DateTimeOffset lastReceiveActivity;
        lock (_serialPortLock)
        {
            if (!ReferenceEquals(_serialPort, serialPort) ||
                !IsConnected ||
                generation != Volatile.Read(ref _connectionGeneration))
            {
                return;
            }

            lastReceiveActivity = _lastReceiveActivity;
        }

        if (!SerialReceiveHealthPolicy.HasTimedOut(
                lastReceiveActivity,
                _clock.Now,
                _receiveSilenceTimeout))
        {
            return;
        }

        TransitionToFaulted(
            serialPort,
            $"接收超时（{_receiveSilenceTimeout.TotalSeconds:0} 秒无数据）");
    }

    private void TransitionToFaulted(SerialPort serialPort, string reason)
    {
        Timer? receiveHealthTimer;
        lock (_serialPortLock)
        {
            if (!ReferenceEquals(_serialPort, serialPort) || !IsConnected)
            {
                return;
            }

            Interlocked.Increment(ref _connectionGeneration);
            _serialPort = null;
            receiveHealthTimer = _receiveHealthTimer;
            _receiveHealthTimer = null;
            Volatile.Write(ref _isConnected, 0);
        }

        receiveHealthTimer?.Dispose();
        serialPort.DataReceived -= OnDataReceived;
        serialPort.ErrorReceived -= OnErrorReceived;
        ReportDiagnostic($"{reason}，连接已标记为断开，将自动重连");
        StatusChanged?.Invoke(this, $"{reason}，等待自动重连");
        ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
        ScheduleDriverCleanup(serialPort, PortName);
    }

    private void ReportDiagnostic(string message)
    {
        if (Volatile.Read(ref _isDisposed) == 1)
        {
            return;
        }

        LinesReceived?.Invoke(
            this,
            [new ReceivedLogLine(_clock.Now, $"[SerialLog] {message}")]);
    }

    private void ScheduleDriverCleanup(SerialPort serialPort, string? portName)
    {
        var cleanupTask = Task.Factory.StartNew(
            () =>
            {
                try
                {
                    if (serialPort.IsOpen)
                    {
                        serialPort.Close();
                    }
                }
                catch
                {
                    // 驱动已与应用逻辑隔离，清理仅尽力执行。
                }
                finally
                {
                    try
                    {
                        serialPort.Dispose();
                    }
                    catch
                    {
                        // 部分 USB 串口驱动在设备移除时可能持续阻塞。
                    }
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        _ = WatchDriverCleanupAsync(cleanupTask, portName);
    }

    private async Task WatchDriverCleanupAsync(Task cleanupTask, string? portName)
    {
        if (await Task.WhenAny(cleanupTask, Task.Delay(DriverCloseTimeout)).ConfigureAwait(false) == cleanupTask)
        {
            return;
        }

        var displayName = string.IsNullOrWhiteSpace(portName) ? "串口" : portName;
        var message = $"{displayName} 驱动关闭超时，旧连接已隔离";
        ReportDiagnostic(message);
        if (!IsConnected)
        {
            StatusChanged?.Invoke(this, $"{message}，等待自动重连");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
        {
            return;
        }

        Close();
    }
}
