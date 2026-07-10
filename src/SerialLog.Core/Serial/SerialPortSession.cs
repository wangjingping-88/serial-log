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
        get
        {
            lock (_serialPortLock)
            {
                return _serialPort?.IsOpen == true;
            }
        }
    }

    public event EventHandler<IReadOnlyList<ReceivedLogLine>>? LinesReceived;

    public event EventHandler<string>? StatusChanged;

    public void Open(string portName, int baudRate)
    {
        Close();
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
            serialPort.Dispose();
            lock (_serialPortLock)
            {
                _serialPort = null;
            }

            throw;
        }

        lock (_serialPortLock)
        {
            _serialPort = serialPort;
        }

        StatusChanged?.Invoke(this, "已连接");
    }

    public void Close()
    {
        SerialPort? serialPort;
        lock (_serialPortLock)
        {
            serialPort = _serialPort;
            _serialPort = null;
        }

        if (serialPort is null)
        {
            return;
        }

        serialPort.DataReceived -= OnDataReceived;
        if (serialPort.IsOpen)
        {
            serialPort.Close();
        }

        serialPort.Dispose();
        _parser.Reset();
        StatusChanged?.Invoke(this, "未连接");
    }

    public void ChangeBaudRate(int baudRate)
    {
        if (baudRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baudRate), "波特率必须大于 0。");
        }

        lock (_serialPortLock)
        {
            if (_serialPort?.IsOpen != true)
            {
                BaudRate = baudRate;
                return;
            }

            _serialPort.BaudRate = baudRate;
            BaudRate = baudRate;
        }

        StatusChanged?.Invoke(this, $"已更新波特率：{baudRate}");
    }

    public Task SendAsync(string payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_serialPortLock)
            {
                if (_serialPort?.IsOpen != true)
                {
                    throw new InvalidOperationException("串口未连接。");
                }

                _serialPort.Write(payload);
            }
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

    public void Dispose()
    {
        Close();
    }
}
