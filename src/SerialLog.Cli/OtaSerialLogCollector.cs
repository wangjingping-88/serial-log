using SerialLog.Core.Logging;
using SerialLog.Core.Serial;

namespace SerialLog.Cli;

public sealed class OtaSerialLogCollector : IDisposable
{
    private readonly EcoLinkOtaDeviceConfig[] _devices;
    private readonly string _runDirectory;
    private readonly object _lock = new();
    private readonly Dictionary<string, List<string>> _lines =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SerialPortSession> _sessions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, StreamWriter> _writers =
        new(StringComparer.OrdinalIgnoreCase);

    public OtaSerialLogCollector(
        IEnumerable<EcoLinkOtaDeviceConfig> devices,
        string runDirectory)
    {
        _devices = devices
            .Where(device => device.Enabled && device.MonitorLogs)
            .ToArray();
        _runDirectory = runDirectory;
    }

    public async Task OpenAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        foreach (var device in _devices)
        {
            var deadline = DateTimeOffset.Now + timeout;
            Exception? latestError = null;
            while (DateTimeOffset.Now < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var session = new SerialPortSession(device.Name);
                try
                {
                    session.Open(device.PortName, device.BaudRate);
                    var writer = new StreamWriter(
                        Path.Combine(_runDirectory, $"{device.Name}.log"),
                        append: false)
                    {
                        AutoFlush = true
                    };
                    lock (_lock)
                    {
                        _lines[device.Name] = [];
                        _sessions[device.Name] = session;
                        _writers[device.Name] = writer;
                    }

                    session.LinesReceived += (_, lines) =>
                        OnLinesReceived(device.Name, lines);
                    latestError = null;
                    break;
                }
                catch (Exception ex) when (ex is IOException
                    or UnauthorizedAccessException
                    or InvalidOperationException)
                {
                    latestError = ex;
                    session.Dispose();
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                }
            }

            if (latestError is not null)
            {
                throw new IOException(
                    $"无法打开{device.Name}日志串口{device.PortName}：{latestError.Message}",
                    latestError);
            }
        }
    }

    public Task SendTextAsync(
        string deviceName,
        string text,
        CancellationToken cancellationToken)
    {
        SerialPortSession session;
        lock (_lock)
        {
            if (!_sessions.TryGetValue(deviceName, out session!))
            {
                throw new InvalidOperationException(
                    $"设备{deviceName}的串口没有打开。");
            }
        }

        return session.SendAsync(text, cancellationToken);
    }

    public IReadOnlyDictionary<string, IReadOnlyList<string>> Snapshot()
    {
        lock (_lock)
        {
            return _lines.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlyList<string>)pair.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    public IReadOnlyList<string> LatestLines(int count)
    {
        lock (_lock)
        {
            return _lines
                .SelectMany(pair => pair.Value
                    .TakeLast(count)
                    .Select(line => $"{pair.Key}: {line}"))
                .ToArray();
        }
    }

    private void OnLinesReceived(
        string deviceName,
        IReadOnlyList<ReceivedLogLine> receivedLines)
    {
        lock (_lock)
        {
            foreach (var line in receivedLines)
            {
                _writers[deviceName].WriteLine(line.FormattedText);
                _lines[deviceName].Add(line.Text);
            }
        }
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }

        foreach (var writer in _writers.Values)
        {
            writer.Dispose();
        }
    }
}
