using System.Collections.Concurrent;
using System.IO.Ports;

namespace SerialLog.Core.Serial;

// 每次 Open 独立创建，始终持有同一个 BaseStream；不另开端口，不增加第二个接收者。
internal sealed class SerialPortRuntime : IDisposable
{
    private readonly Stream _stream;
    private readonly Func<bool> _isOpen;
    private readonly Func<int> _available;
    private readonly Func<bool, SerialNativeSnapshot> _probe;
    private readonly Action<string> _diagnostic;
    private readonly ConcurrentQueue<string> _notices = new();
    private int _polling, _disposed;
    private long _rxBytes, _txBytes, _lastReceiveTicks, _lastSendTicks, _lastErrorTicks;
    private int _lastSendSize;
    private long _lastReadTrace;
    internal bool IsPolling => Volatile.Read(ref _polling) == 1;

    internal SerialPortRuntime(SerialPort port, Action<string> diagnostic)
        : this(port.BaseStream, () => port.IsOpen, () => port.BytesToRead, null, diagnostic) { }

    internal SerialPortRuntime(Stream stream, Func<bool> isOpen, Func<int> available,
        Func<bool, SerialNativeSnapshot>? probe, Action<string> diagnostic)
    {
        _stream = stream; _isOpen = isOpen; _available = available;
        _probe = probe ?? (queue => SerialNativeProbe.Capture(stream, queue)); _diagnostic = diagnostic;
    }

    internal int BytesToRead()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!IsPolling && _isOpen())
        {
            try { return _available(); }
            catch (InvalidOperationException) when (!_isOpen()) { /* 事件线程恰好在检查后退出，再验证原句柄。 */ }
        }
        var snapshot = RequirePollingHandle();
        return checked((int)snapshot.InputBytes);
    }

    private SerialNativeSnapshot RequirePollingHandle()
    {
        var snapshot = _probe(true);
        if (!snapshot.CanPoll)
            throw new IOException($"串口状态失效，不能保留原连接：{snapshot}");
        if (Interlocked.Exchange(ref _polling, 1) == 0)
        {
            _diagnostic($"事件监视已退出，原句柄可用，切换轮询；{snapshot}; {Counters()}");
            _notices.Enqueue("串口事件监视已退出，原句柄探测正常，已切换轮询接收（未断开）");
        }
        if (snapshot.Errors != 0 && DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastErrorTicks) >= TimeSpan.FromSeconds(30).Ticks)
        {
            Interlocked.Exchange(ref _lastErrorTicks, DateTime.UtcNow.Ticks);
            _diagnostic($"轮询串口错误标志；{snapshot}");
            _notices.Enqueue($"串口轮询报告错误：0x{snapshot.Errors:X}，可能有数据丢失，连接保持");
        }
        return snapshot;
    }

    internal int Read(byte[] buffer, int count)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var read = _stream.Read(buffer, 0, count);
        Interlocked.Add(ref _rxBytes, read);
        if (read > 0) Interlocked.Exchange(ref _lastReceiveTicks, DateTime.UtcNow.Ticks);
        if (SerialDiagnosticLog.DetailedReceiveEnabled &&
            System.Diagnostics.Stopwatch.GetElapsedTime(_lastReadTrace).TotalSeconds >= 5)
        {
            _lastReadTrace = System.Diagnostics.Stopwatch.GetTimestamp();
            _diagnostic($"底层读取耗时；readMs={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}; requested={count}; received={read}; poolThread={Thread.CurrentThread.IsThreadPoolThread}");
        }
        return read;
    }

    internal void Write(byte[] bytes)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (IsPolling || !_isOpen()) RequirePollingHandle();
        Interlocked.Exchange(ref _lastSendTicks, DateTime.UtcNow.Ticks);
        Volatile.Write(ref _lastSendSize, bytes.Length);
        // 不自动重发。写失败时无法知道驱动已经接受多少字节。
        _stream.Write(bytes, 0, bytes.Length);
        Interlocked.Add(ref _txBytes, bytes.Length);
    }

    internal IEnumerable<string> TakeNotices()
    {
        while (_notices.TryDequeue(out var notice)) yield return notice;
    }

    internal string Describe() => $"IsOpen={_isOpen()}; polling={IsPolling}; {_probe(false)}; {Counters()}";
    private string Counters() => $"rxBytes={Interlocked.Read(ref _rxBytes)}; txBytes={Interlocked.Read(ref _txBytes)}; " +
        $"lastRxUtcTicks={Interlocked.Read(ref _lastReceiveTicks)}; lastTxUtcTicks={Interlocked.Read(ref _lastSendTicks)}; lastTxSize={Volatile.Read(ref _lastSendSize)}";

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _stream.Dispose();
    }
}
