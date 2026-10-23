using System.Buffers;
using System.IO.Ports;
using System.Text;
using System.Runtime.CompilerServices;
using SerialLog.Core.Commands;
using SerialLog.Core.Logging;

namespace SerialLog.Core.Serial;

public sealed class SerialPortSession : ICommandTarget, IDisposable
{
    private static readonly TimeSpan DefaultReceiveHealthCheckInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan DriverWarningReportInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DriverReopenSettleDelay = TimeSpan.FromMilliseconds(500);
    private const int SerialReadBufferSize = 1024 * 1024;
    private const int ReceiveReadChunkSize = 64 * 1024;
    private readonly IClock _clock;
    private TimeSpan? _receiveSilenceTimeout;
    private readonly TimeSpan _receiveHealthCheckInterval;
    private readonly LogLineParser _parser = new();
    private readonly object _openLock = new();
    private readonly object _receiveLock = new();
    private readonly object _serialPortLock = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private SerialBinaryLease? _binaryLease;
    private SerialPort? _serialPort;
    private readonly ConditionalWeakTable<SerialPort, SerialPortRuntime> _runtimes = new();
    private SerialReceivePump? _receiveHealthTimer;
    private Task _driverCleanupTask = Task.CompletedTask;
    private DateTimeOffset _lastReceiveActivity;
    private DateTimeOffset _lastDriverWarningAt = DateTimeOffset.MinValue;
    private long _connectionGeneration;
    private int _hasScheduledDriverCleanup;
    private int _isReceiveHealthCheckRunning;
    private int _isConnected;
    private int _isReceiveVerified;
    private int _isDisposed;
    private long _lastReceiveTrace;

    private static readonly TimeSpan DriverCloseTimeout = TimeSpan.FromSeconds(3);

    public SerialPortSession(
        string id,
        IClock? clock = null,
        TimeSpan? receiveSilenceTimeout = null,
        TimeSpan? receiveHealthCheckInterval = null)
    {
        Id = id;
        _clock = clock ?? new SystemClock();
        _receiveSilenceTimeout = receiveSilenceTimeout;
        _receiveHealthCheckInterval = receiveHealthCheckInterval ?? DefaultReceiveHealthCheckInterval;
        if (_receiveSilenceTimeout is { } silenceTimeout && silenceTimeout <= TimeSpan.Zero)
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

    public bool IsReceiveVerified
    {
        get => Volatile.Read(ref _isReceiveVerified) == 1;
    }

    public event EventHandler<IReadOnlyList<ReceivedLogLine>>? LinesReceived;

    public event EventHandler<string>? StatusChanged;

    public event EventHandler? ConnectionStateChanged;

    public void Open(string portName, int baudRate)
    {
        lock (_openLock)
        {
            OpenCore(portName, baudRate);
        }
    }

    private void OpenCore(string portName, int baudRate)
    {
        if (_binaryLease is not null) throw new InvalidOperationException("烧录租约释放前不能重新打开串口。");
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) == 1, this);
        Close();
        var generation = Volatile.Read(ref _connectionGeneration);
        WaitForDriverCleanup(portName);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) == 1, this);
        _parser.Reset();
        PortName = portName;
        BaudRate = baudRate;
        WriteDiagnostic(null, $"打开请求；port={portName}; baud={baudRate}; runtime={Environment.Version}; portsAssembly={typeof(SerialPort).Assembly.GetName().Version}; coreBuild={typeof(SerialPortSession).Module.ModuleVersionId}; appDirectory={AppContext.BaseDirectory}");
        var serialPort = new SerialPort(portName, baudRate)
        {
            Encoding = Encoding.UTF8,
            NewLine = "\n",
            ReadBufferSize = SerialReadBufferSize,
            ReadTimeout = 500,
            WriteTimeout = 500
        };
        serialPort.DataReceived += OnDataReceived;
        serialPort.ErrorReceived += OnErrorReceived;
        serialPort.PinChanged += OnPinChanged;
        try
        {
            serialPort.Open();
            _runtimes.Add(serialPort, new SerialPortRuntime(serialPort,
                message => SerialDiagnosticLog.Write($"session={Id}; port={portName}; generation={generation}; {message}")));
        }
        catch (Exception ex)
        {
            WriteDiagnostic(serialPort, $"打开失败；{ex}");
            serialPort.DataReceived -= OnDataReceived;
            serialPort.ErrorReceived -= OnErrorReceived;
            serialPort.PinChanged -= OnPinChanged;
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
            _lastDriverWarningAt = DateTimeOffset.MinValue;
            Volatile.Write(ref _isReceiveVerified, 0);
            Volatile.Write(ref _isConnected, 1);
            _receiveHealthTimer = CreateReceiveHealthTimer(serialPort, generation);
        }

        WriteDiagnostic(serialPort, "打开完成");
        NotifyStatus("串口已打开，等待数据");
        NotifyConnectionState();
    }

    public void Close()
    {
        var origin = new System.Diagnostics.StackTrace(1, false).GetFrame(0)?.GetMethod();
        Interlocked.Increment(ref _connectionGeneration);
        SerialPort? serialPort;
        SerialReceivePump? receiveHealthTimer;
        lock (_serialPortLock)
        {
            serialPort = _serialPort;
            _serialPort = null;
            receiveHealthTimer = _receiveHealthTimer;
            _receiveHealthTimer = null;
            Volatile.Write(ref _isConnected, 0);
            Volatile.Write(ref _isReceiveVerified, 0);
        }

        receiveHealthTimer?.Dispose();
        _binaryLease?.Fail(new IOException("串口连接已关闭。"));

        if (serialPort is null)
        {
            return;
        }

        serialPort.DataReceived -= OnDataReceived;
        serialPort.ErrorReceived -= OnErrorReceived;
        serialPort.PinChanged -= OnPinChanged;
        WriteDiagnostic(serialPort, $"主动关闭；caller={origin?.DeclaringType?.Name}.{origin?.Name}");
        ScheduleDriverCleanup(serialPort, PortName);
        lock (_receiveLock) _parser.Reset();
        NotifyStatus("未连接");
        NotifyConnectionState();
    }

    public void ChangeBaudRate(int baudRate)
    {
        if (baudRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baudRate), "波特率必须大于 0。");
        }

        if (!_writeGate.Wait(TimeSpan.FromSeconds(3))) throw new IOException("串口写入繁忙，无法修改波特率。");
        try
        {
            SerialPort? serialPort;
            lock (_serialPortLock)
            {
                if (_binaryLease is not null) throw new InvalidOperationException("烧录期间不能修改波特率。");
                serialPort = _serialPort;
                if (serialPort is null || !IsConnected)
                {
                    BaudRate = baudRate;
                    return;
                }
            }
            // SerialPort.IsOpen=false 时 Setter 只改托管缓存，不能假称驱动参数已更新。
            if (!serialPort.IsOpen || _runtimes.TryGetValue(serialPort, out var runtime) && runtime.IsPolling)
                throw new InvalidOperationException("串口处于事件监视恢复模式，请手动断开重连后修改波特率。");
            serialPort.BaudRate = baudRate;
            BaudRate = baudRate;
            WriteDiagnostic(serialPort, $"波特率已更改；baud={baudRate}");
        }
        finally { _writeGate.Release(); }

        NotifyStatus($"已更新波特率：{baudRate}");
    }

    public void ChangeReceiveSilenceTimeout(TimeSpan? timeout)
    {
        if (timeout is { } value && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "接收静默超时必须大于 0。");
        }

        SerialReceivePump? previousTimer;
        lock (_serialPortLock)
        {
            _receiveSilenceTimeout = timeout;
            _lastReceiveActivity = _clock.Now;
            previousTimer = _receiveHealthTimer;
            if (_serialPort is not null && IsConnected)
            {
                var generation = Interlocked.Increment(ref _connectionGeneration);
                _receiveHealthTimer = CreateReceiveHealthTimer(_serialPort, generation);
            }
            else
            {
                _receiveHealthTimer = null;
            }
        }

        previousTimer?.Dispose();
    }

    public Task SendAsync(string payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(async () =>
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                SerialPort? serialPort;
                lock (_serialPortLock)
                {
                    if (_binaryLease is not null) throw new InvalidOperationException("串口正在烧录，普通发送已禁用。");
                    serialPort = _serialPort;
                    if (serialPort is null || !IsConnected) throw new InvalidOperationException("串口未连接。");
                }
                try { WriteBytes(serialPort, Encoding.UTF8.GetBytes(payload)); }
                catch (TimeoutException ex)
                {
                    WriteDiagnostic(serialPort, $"发送超时，未自动重发，需核对设备接收结果；{ex}");
                    throw;
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                {
                    TransitionToFaulted(serialPort, $"串口发送失败：{ex.Message}", exception: ex);
                    throw;
                }
            }
            finally { _writeGate.Release(); }
        }, cancellationToken);
    }

    public async Task<IBinarySerialTransport> AcquireBinaryLeaseAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_receiveLock)
            lock (_serialPortLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_binaryLease is not null) throw new InvalidOperationException("串口已有烧录任务。");
                if (_serialPort is null || !IsConnected) throw new IOException("串口未连接。");
                _parser.Reset();
                var serialPort = _serialPort;
                SerialBinaryLease? lease = null;
                lease = new SerialBinaryLease(async (bytes, ct) =>
                {
                    await _writeGate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        if (!ReferenceEquals(_binaryLease, lease) || !IsCurrentConnection(serialPort))
                            throw new IOException("烧录串口连接已失效。");
                        var copy = bytes.ToArray();
                        var writeTask = Task.Run(() => WriteBytes(serialPort, copy));
                        try { await writeTask.WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
                        {
                            // 写入尚未结束时隔离旧句柄，不能将它交回日志或普通发送。
                            if (!writeTask.IsCompleted)
                            {
                                TransitionToFaulted(serialPort, "烧录写入中断，旧驱动已隔离");
                                _ = writeTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                            }
                            throw;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
                    {
                        TransitionToFaulted(serialPort, $"烧录串口写入失败：{ex.Message}", exception: ex);
                        throw;
                    }
                    finally { _writeGate.Release(); }
                }, ReleaseBinaryLeaseAsync, ObserveFirmwareText, SwitchFirmwareReceiveModeAsync);
                _binaryLease = lease;
                return lease;
            }
        }
        finally { _writeGate.Release(); }
    }

    private async ValueTask ReleaseBinaryLeaseAsync(SerialBinaryLease lease)
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        IReadOnlyList<ReceivedLogLine> tailLines = [];
        try
        {
            lock (_receiveLock)
            lock (_serialPortLock)
            {
                if (!ReferenceEquals(_binaryLease, lease)) return;
                _binaryLease = null;
                if (!lease.HasObservedText) _parser.Reset();
                _lastReceiveActivity = _clock.Now;
                // 最终 ACK 后已到达的启动文本不能丢失；剔除协议控制字节。
                var tail = lease.SwitchMode(BinaryReceiveMode.LogOnly);
                if (IsConnected && tail.Length > 0) tailLines = _parser.Append(tail, _clock.Now);
            }
        }
        finally { _writeGate.Release(); }
        if (tailLines.Count > 0) PublishLines(tailLines);
    }

    private void ObserveFirmwareText(ReadOnlyMemory<byte> bytes)
    {
        IReadOnlyList<ReceivedLogLine> lines;
        lock (_receiveLock) lines = IsConnected ? _parser.Append(bytes.Span, _clock.Now) : [];
        if (lines.Count > 0) PublishLines(lines);
    }

    private ValueTask SwitchFirmwareReceiveModeAsync(SerialBinaryLease lease, BinaryReceiveMode mode)
    {
        IReadOnlyList<ReceivedLogLine> lines = [];
        lock (_receiveLock)
        {
            if (!ReferenceEquals(_binaryLease, lease)) throw new IOException("烧录串口租约已失效。");
            if (mode == BinaryReceiveMode.LogOnly && !lease.HasObservedText) _parser.Reset();
            var text = lease.SwitchMode(mode);
            // 模式切换与物理接收串行；不会创建第二个串口读取者。
            if (IsConnected && text.Length > 0) lines = _parser.Append(text, _clock.Now);
        }
        if (lines.Count > 0) PublishLines(lines);
        return ValueTask.CompletedTask;
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

        lock (_serialPortLock)
        {
            if (ReferenceEquals(serialPort, _serialPort)) _receiveHealthTimer?.Signal();
        }
    }

    private bool DrainAvailableData(
        SerialPort serialPort,
        string failureReason,
        long? expectedGeneration = null)
    {
        IReadOnlyList<ReceivedLogLine> lines;
        bool firstReceive;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            lock (_receiveLock)
            {
                // 等待接收锁期间连接可能已替换，旧回调不得消费新连接的解析器/租约。
                if (!IsCurrentConnection(serialPort) || expectedGeneration is not null && expectedGeneration != Volatile.Read(ref _connectionGeneration)) return true;
                lines = ReadAvailableLines(serialPort, out firstReceive);
            }
        }
        catch (Exception ex)
        {
            TransitionToFaulted(serialPort, $"{failureReason}：{ex.Message}", expectedGeneration, ex);
            return false;
        }
        PublishRuntimeNotices(serialPort);
        var parsed = System.Diagnostics.Stopwatch.GetTimestamp();
        // 显示/保存/协作订阅者异常不是串口故障，不应关闭正常的物理连接。
        if (lines.Count > 0 && IsCurrentConnection(serialPort)) PublishLines(lines);
        if (firstReceive && IsCurrentConnection(serialPort))
        {
            NotifyStatus("已连接");
            NotifyConnectionState();
        }
        if (SerialDiagnosticLog.DetailedReceiveEnabled && lines.Count > 0 &&
            (firstReceive || System.Diagnostics.Stopwatch.GetElapsedTime(_lastReceiveTrace).TotalSeconds >= 5))
        {
            _lastReceiveTrace = System.Diagnostics.Stopwatch.GetTimestamp();
            SerialDiagnosticLog.Write($"session={Id}; port={serialPort.PortName}; 接收耗时；readParseMs={System.Diagnostics.Stopwatch.GetElapsedTime(started, parsed).TotalMilliseconds:F1}; publishMs={System.Diagnostics.Stopwatch.GetElapsedTime(parsed).TotalMilliseconds:F1}; lines={lines.Count}; poolThread={Thread.CurrentThread.IsThreadPoolThread}");
        }
        return true;
    }

    private void OnPinChanged(object sender, SerialPinChangedEventArgs e)
    {
        if (sender is SerialPort port && IsCurrentConnection(port))
            WriteDiagnostic(port, $"引脚事件；event={e.EventType}");
    }

    private void OnErrorReceived(object sender, SerialErrorReceivedEventArgs e)
    {
        if (sender is not SerialPort serialPort || !IsCurrentConnection(serialPort))
        {
            return;
        }

        var now = _clock.Now;
        var shouldReport = false;
        lock (_serialPortLock)
        {
            if (!ReferenceEquals(_serialPort, serialPort) || !IsConnected)
            {
                return;
            }

            shouldReport = SerialReceiveHealthPolicy.ShouldReportDriverError(
                _lastDriverWarningAt,
                now,
                DriverWarningReportInterval);
            if (shouldReport)
            {
                _lastDriverWarningAt = now;
            }
        }

        if (!shouldReport)
        {
            return;
        }

        WriteDiagnostic(serialPort, $"驱动警告；event={e.EventType}");

        var suffix = SerialReceiveHealthPolicy.IsRecoverableDriverError(e.EventType)
            ? "，可能有少量数据丢失，连接保持"
            : "，连接保持";
        ReportDiagnostic($"串口驱动报告错误：{e.EventType}{suffix}");
    }

    private IReadOnlyList<ReceivedLogLine> ReadAvailableLines(SerialPort serialPort, out bool firstReceive)
    {
        firstReceive = false;
        _runtimes.TryGetValue(serialPort, out var runtime);
        if (runtime is null && !serialPort.IsOpen)
        {
            throw new IOException("串口未打开，且没有可验证的底层流");
        }

        var lines = new List<ReceivedLogLine>();
        var buffer = ArrayPool<byte>.Shared.Rent(ReceiveReadChunkSize);
        try
        {
            var drainedBytes = 0;
            while (IsCurrentConnection(serialPort) && drainedBytes < 256 * 1024)
            {
                var available = runtime?.BytesToRead() ?? serialPort.BytesToRead;
                if (available <= 0) break;
                var count = Math.Min(available, buffer.Length);
                var read = runtime?.Read(buffer, count) ?? serialPort.Read(buffer, 0, count);
                if (read <= 0)
                {
                    break;
                }

                var receivedAt = _clock.Now;
                drainedBytes += read;
                if (_binaryLease is { IsLogOnly: false } lease) lease.Receive(buffer.AsSpan(0, read));
                else lines.AddRange(_parser.Append(buffer.AsSpan(0, read), receivedAt));
                firstReceive |= MarkReceiveActivity(serialPort, lines, receivedAt);
                // 首批完整行及时交付，不等待下一次 Read 或持续流达到 256 KiB。
                // 剩余数据由事件唤醒或 50 ms 补捞；二进制仍沿唯一接收通道处理。
                if (lines.Count > 0) break;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
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

    private bool MarkReceiveActivity(SerialPort serialPort, List<ReceivedLogLine> lines, DateTimeOffset receivedAt)
    {
        var firstReceive = false;
        lock (_serialPortLock)
        {
            if (ReferenceEquals(_serialPort, serialPort) && IsConnected)
            {
                _lastReceiveActivity = receivedAt;
                if (!IsReceiveVerified)
                {
                    Volatile.Write(ref _isReceiveVerified, 1);
                    firstReceive = true;
                }
            }
        }

        if (firstReceive)
        {
            // 与当前接收内容同批发布，不能在解析之前调用显示/保存/协作订阅者。
            lines.Insert(0, new ReceivedLogLine(receivedAt, "[SerialLog] 串口接收已验证"));
        }
        return firstReceive;
    }

    private void CheckReceiveHealth(SerialPort serialPort, long generation)
    {
        if (Interlocked.Exchange(ref _isReceiveHealthCheckRunning, 1) == 1)
        {
            return;
        }

        try
        {
            lock (_serialPortLock)
            {
                if (!ReferenceEquals(_serialPort, serialPort) ||
                    !IsConnected ||
                    generation != Volatile.Read(ref _connectionGeneration))
                {
                    return;
                }
            }

            // DataReceived 由串口驱动触发，设备复位或烧录后驱动可能遗漏通知。
            // 周期读取既能补捞已进入缓冲区的数据，也能通过 IsOpen/BytesToRead 异常识别失效句柄。
            if (!DrainAvailableData(serialPort, "串口接收状态检查失败", generation))
            {
                return;
            }

            DateTimeOffset lastReceiveActivity;
            TimeSpan? receiveSilenceTimeout;
            lock (_serialPortLock)
            {
                if (!ReferenceEquals(_serialPort, serialPort) ||
                    !IsConnected ||
                    generation != Volatile.Read(ref _connectionGeneration))
                {
                    return;
                }

                lastReceiveActivity = _lastReceiveActivity;
                receiveSilenceTimeout = _binaryLease is null ? _receiveSilenceTimeout : null;
            }

            if (receiveSilenceTimeout is null)
            {
                return;
            }

            if (!SerialReceiveHealthPolicy.HasTimedOut(
                    lastReceiveActivity,
                    _clock.Now,
                    receiveSilenceTimeout.Value))
            {
                return;
            }

            TransitionToFaulted(
                serialPort,
                $"已启用无数据自动重连：连续 {receiveSilenceTimeout.Value.TotalSeconds:0} 秒未收到数据",
                generation);
        }
        finally
        {
            Volatile.Write(ref _isReceiveHealthCheckRunning, 0);
        }
    }

    private SerialReceivePump CreateReceiveHealthTimer(SerialPort serialPort, long generation)
    {
        var interval = _runtimes.TryGetValue(serialPort, out var runtime) && runtime.IsPolling
            ? TimeSpan.FromMilliseconds(50) : _receiveHealthCheckInterval;
        return new SerialReceivePump(() => CheckReceiveHealth(serialPort, generation), interval,
            ex => WriteDiagnostic(serialPort, $"接收工作线程异常；{ex}"));
    }

    private void TransitionToFaulted(
        SerialPort serialPort,
        string reason,
        long? expectedGeneration = null,
        Exception? exception = null)
    {
        SerialReceivePump? receiveHealthTimer;
        lock (_serialPortLock)
        {
            if (!ReferenceEquals(_serialPort, serialPort) ||
                !IsConnected ||
                expectedGeneration is not null &&
                expectedGeneration.Value != Volatile.Read(ref _connectionGeneration))
            {
                return;
            }

            Interlocked.Increment(ref _connectionGeneration);
            _serialPort = null;
            receiveHealthTimer = _receiveHealthTimer;
            _receiveHealthTimer = null;
            Volatile.Write(ref _isConnected, 0);
            Volatile.Write(ref _isReceiveVerified, 0);
        }

        receiveHealthTimer?.Dispose();
        WriteDiagnostic(serialPort, $"故障断开；reason={reason}; exception={exception}");
        var duringFirmware = _binaryLease is not null;
        _binaryLease?.Fail(new IOException(reason));
        serialPort.DataReceived -= OnDataReceived;
        serialPort.ErrorReceived -= OnErrorReceived;
        serialPort.PinChanged -= OnPinChanged;
        ScheduleDriverCleanup(serialPort, PortName);
        ReportDiagnostic($"{reason}，连接已标记为断开，{(duringFirmware ? "请手动重连" : "将自动重连")}");
        NotifyStatus($"{reason}，{(duringFirmware ? "请手动重连" : "等待自动重连")}");
        NotifyConnectionState();
    }

    private void ReportDiagnostic(string message)
    {
        if (Volatile.Read(ref _isDisposed) == 1)
        {
            return;
        }

        PublishLines([new ReceivedLogLine(_clock.Now, $"[SerialLog] {message}")]);
    }

    private void WriteBytes(SerialPort port, byte[] bytes)
    {
        if (_runtimes.TryGetValue(port, out var runtime)) runtime.Write(bytes);
        else port.Write(bytes, 0, bytes.Length);
        PublishRuntimeNotices(port);
    }

    private void PublishRuntimeNotices(SerialPort port)
    {
        if (!_runtimes.TryGetValue(port, out var runtime)) return;
        var notices = runtime.TakeNotices().ToArray();
        if (notices.Length == 0) return;
        lock (_serialPortLock)
        {
            if (!ReferenceEquals(port, _serialPort) || !IsConnected) return;
            if (runtime.IsPolling) _receiveHealthTimer?.Signal();
        }
        foreach (var notice in notices) ReportDiagnostic(notice);
    }

    private void WriteDiagnostic(SerialPort? port, string message)
    {
        try
        {
            var state = port is not null && _runtimes.TryGetValue(port, out var runtime) ? runtime.Describe() : "无底层流快照";
            SerialDiagnosticLog.Write($"session={Id}; port={port?.PortName ?? PortName}; baud={BaudRate}; generation={Volatile.Read(ref _connectionGeneration)}; {message}; {state}");
        }
        catch (Exception ex) { SerialDiagnosticLog.Write($"session={Id}; {message}; 状态采集失败={ex.GetType().Name}: {ex.Message}"); }
    }

    private void PublishLines(IReadOnlyList<ReceivedLogLine> lines)
    {
        if (LinesReceived is not { } subscribers) return;
        foreach (EventHandler<IReadOnlyList<ReceivedLogLine>> subscriber in subscribers.GetInvocationList())
            try { subscriber(this, lines); }
            catch (Exception ex) { SerialDiagnosticLog.Write($"session={Id}; 日志订阅者异常，连接保持；{ex}"); }
    }

    private void NotifyStatus(string status)
    {
        if (StatusChanged is not { } subscribers) return;
        foreach (EventHandler<string> subscriber in subscribers.GetInvocationList())
            try { subscriber(this, status); }
            catch (Exception ex) { SerialDiagnosticLog.Write($"session={Id}; 状态订阅者异常；{ex}"); }
    }

    private void NotifyConnectionState()
    {
        if (ConnectionStateChanged is not { } subscribers) return;
        foreach (EventHandler subscriber in subscribers.GetInvocationList())
            try { subscriber(this, EventArgs.Empty); }
            catch (Exception ex) { SerialDiagnosticLog.Write($"session={Id}; 连接状态订阅者异常；{ex}"); }
    }

    private void ScheduleDriverCleanup(SerialPort serialPort, string? portName)
    {
        _runtimes.TryGetValue(serialPort, out var runtime);
        _runtimes.Remove(serialPort);
        var cleanupTask = Task.Factory.StartNew(
            () =>
            {
                try
                {
                    // SerialPort.Dispose 在 IsOpen=false 时会跳过内部流；必须释放缓存流。
                    if (runtime is not null) runtime.Dispose();
                    else if (serialPort.IsOpen) serialPort.Close();
                }
                catch (Exception ex)
                {
                    SerialDiagnosticLog.Write($"session={Id}; port={portName}; 底层流释放失败；{ex}");
                }
                finally
                {
                    try
                    {
                        serialPort.Dispose();
                    }
                    catch (Exception ex)
                    {
                        SerialDiagnosticLog.Write($"session={Id}; port={portName}; 串口对象释放失败；{ex}");
                    }
                }
                SerialDiagnosticLog.Write($"session={Id}; port={portName}; 旧连接清理任务结束");
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        lock (_serialPortLock)
        {
            _hasScheduledDriverCleanup = 1;
            _driverCleanupTask = Task.WhenAll(_driverCleanupTask, cleanupTask);
        }

        _ = WatchDriverCleanupAsync(cleanupTask, portName);
    }

    public async Task WaitForDriverReleaseAsync()
    {
        Task cleanup;
        lock (_serialPortLock) cleanup = _driverCleanupTask;
        await cleanup.WaitAsync(DriverCloseTimeout).ConfigureAwait(false);
        await Task.Delay(DriverReopenSettleDelay).ConfigureAwait(false);
    }

    private void WaitForDriverCleanup(string portName)
    {
        Task cleanupTask;
        lock (_serialPortLock)
        {
            if (0 == _hasScheduledDriverCleanup)
            {
                return;
            }

            cleanupTask = _driverCleanupTask;
        }

        if (!cleanupTask.Wait(DriverCloseTimeout))
        {
            throw new IOException($"{portName} 旧连接仍在关闭，稍后重试");
        }

        lock (_serialPortLock)
        {
            if (ReferenceEquals(_driverCleanupTask, cleanupTask))
            {
                _driverCleanupTask = Task.CompletedTask;
                _hasScheduledDriverCleanup = 0;
            }
        }

        Thread.Sleep(DriverReopenSettleDelay);
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
            NotifyStatus($"{message}，等待重新连接");
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
