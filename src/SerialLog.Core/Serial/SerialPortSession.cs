using System.IO.Ports;
using System.Text;
using SerialLog.Core.Commands;
using SerialLog.Core.Logging;

namespace SerialLog.Core.Serial;

public sealed class SerialPortSession : ICommandTarget, IDisposable
{
    private readonly IClock _clock;
    private readonly LogLineParser _parser = new();
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
        StatusChanged?.Invoke(this, "未连接");
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
            var text = serialPort.ReadExisting();
            var lines = _parser.Append(text, _clock.Now);
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

    public void Dispose()
    {
        Close();
    }
}
