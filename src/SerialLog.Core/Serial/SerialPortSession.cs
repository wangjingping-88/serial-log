using System.IO.Ports;
using System.Text;
using SerialLog.Core.Commands;
using SerialLog.Core.Logging;

namespace SerialLog.Core.Serial;

public sealed class SerialPortSession : ICommandTarget, IDisposable
{
    private readonly IClock _clock;
    private readonly LogLineParser _parser = new();
    private SerialPort? _serialPort;

    public SerialPortSession(string id, IClock? clock = null)
    {
        Id = id;
        _clock = clock ?? new SystemClock();
    }

    public string Id { get; }

    public string? PortName { get; private set; }

    public int BaudRate { get; private set; } = 115200;

    public bool IsConnected => _serialPort?.IsOpen == true;

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
            _serialPort = null;
            throw;
        }

        _serialPort = serialPort;
        StatusChanged?.Invoke(this, "已连接");
    }

    public void Close()
    {
        if (_serialPort is null)
        {
            return;
        }

        _serialPort.DataReceived -= OnDataReceived;
        if (_serialPort.IsOpen)
        {
            _serialPort.Close();
        }

        _serialPort.Dispose();
        _serialPort = null;
        StatusChanged?.Invoke(this, "未连接");
    }

    public Task SendAsync(string payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_serialPort?.IsOpen != true)
        {
            throw new InvalidOperationException("串口未连接。");
        }

        _serialPort.Write(payload);
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> GetAvailablePorts()
    {
        return SerialPort.GetPortNames().OrderBy(name => name).ToArray();
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (_serialPort is null)
        {
            return;
        }

        try
        {
            var text = _serialPort.ReadExisting();
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
