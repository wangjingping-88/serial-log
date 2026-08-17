using System.IO.Ports;
using System.Text;
using SerialLog.Core.Commands;
using SerialLog.Core.Logging;

namespace SerialLog.Core.Serial;

public sealed class SerialPortSession : ICommandTarget, IDisposable
{
    private readonly IClock _clock;
    private readonly LogLineParser _parser = new();
    private readonly object _receiveLock = new();
    private readonly object _serialPortLock = new();
    private SerialPort? _serialPort;
    private long _connectionGeneration;
    private int _isConnected;

    private static readonly TimeSpan DriverCloseTimeout = TimeSpan.FromSeconds(3);

    public SerialPortSession(string id, IClock? clock = null)
    {
        Id = id;
        _clock = clock ?? new SystemClock();
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

    public void Open(string portName, int baudRate)
    {
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
        try
        {
            serialPort.Open();
        }
        catch
        {
            serialPort.DataReceived -= OnDataReceived;
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
            Volatile.Write(ref _isConnected, 1);
        }

        StatusChanged?.Invoke(this, "已连接");
    }

    public void Close()
    {
        Interlocked.Increment(ref _connectionGeneration);
        SerialPort? serialPort;
        lock (_serialPortLock)
        {
            serialPort = _serialPort;
            _serialPort = null;
            Volatile.Write(ref _isConnected, 0);
        }

        if (serialPort is null)
        {
            return;
        }

        serialPort.DataReceived -= OnDataReceived;
        ScheduleDriverCleanup(serialPort, PortName);
        _parser.Reset();
        StatusChanged?.Invoke(this, "未连接");
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

            serialPort.Write(payload);
        }, cancellationToken);
    }

    public IReadOnlyList<string> GetAvailablePorts()
    {
        return SerialPort.GetPortNames().OrderBy(name => name).ToArray();
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        SerialPort? serialPort;
        lock (_serialPortLock)
        {
            serialPort = _serialPort;
        }

        if (serialPort is null)
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
            StatusChanged?.Invoke(this, $"接收失败：{ex.Message}");
        }
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

            lines.AddRange(_parser.Append(buffer.AsSpan(0, read), _clock.Now));
        }

        return lines;
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
        StatusChanged?.Invoke(this, $"{displayName} 驱动关闭超时，已隔离，应用可继续使用。");
    }

    public void Dispose()
    {
        Close();
    }
}
