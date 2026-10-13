using System.Threading.Channels;

namespace SerialLog.Core.Serial;

internal sealed class SerialBinaryLease : IBinarySerialTransport
{
    private readonly Channel<byte> _received = Channel.CreateBounded<byte>(new BoundedChannelOptions(65536)
    { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _write;
    private readonly Func<SerialBinaryLease, ValueTask> _release;
    private readonly Action<ReadOnlyMemory<byte>>? _observeText;
    private readonly Func<SerialBinaryLease, BinaryReceiveMode, ValueTask>? _switchMode;
    private readonly object _modeLock = new();
    private readonly List<byte> _observedText = [];
    private BinaryReceiveMode _mode;
    internal bool IsLogOnly { get { lock (_modeLock) return _mode == BinaryReceiveMode.LogOnly; } }
    internal bool HasObservedText { get; private set; }
    private int _disposed;
    private Exception? _failure;

    internal SerialBinaryLease(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> write,
        Func<SerialBinaryLease, ValueTask> release, Action<ReadOnlyMemory<byte>>? observeText = null,
        Func<SerialBinaryLease, BinaryReceiveMode, ValueTask>? switchMode = null)
    { _write = write; _release = release; _observeText = observeText; _switchMode = switchMode; }

    internal void Receive(ReadOnlySpan<byte> bytes)
    {
        byte[]? log = null;
        lock (_modeLock)
        {
            if (_mode == BinaryReceiveMode.LogOnly) log = bytes.ToArray();
            else foreach (var b in bytes)
                if (!_received.Writer.TryWrite(b))
                {
                    Fail(new IOException("烧录接收缓冲溢出，传输已停止。"));
                    break;
                }
        }
        if (log is not null) _observeText?.Invoke(log);
    }

    internal byte[] SwitchMode(BinaryReceiveMode mode)
    {
        lock (_modeLock)
        {
            var text = new List<byte>(_observedText); _observedText.Clear();
            _mode = mode;
            if (mode == BinaryReceiveMode.LogOnly)
            {
                HasObservedText = true;
                text.AddRange(DrainUnread().Where(b => b >= 0x20 || b is 10 or 13));
            }
            return text.ToArray();
        }
    }

    public ValueTask SetReceiveModeAsync(BinaryReceiveMode mode, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_switchMode is not null) return _switchMode(this, mode);
        var text = SwitchMode(mode);
        if (text.Length > 0) _observeText?.Invoke(text);
        return ValueTask.CompletedTask;
    }

    internal void Fail(Exception error)
    {
        Interlocked.CompareExchange(ref _failure, error, null);
        _received.Writer.TryComplete(error);
    }
    public ValueTask DiscardReceiveBufferAsync()
    {
        lock (_modeLock) { _observedText.Clear(); DrainUnread(); HasObservedText = false; }
        return ValueTask.CompletedTask;
    }
    internal byte[] DrainUnread()
    {
        var tail = new List<byte>();
        while (_received.Reader.TryRead(out var b)) tail.Add(b);
        return tail.ToArray();
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _failure) is { } failure) throw new IOException(failure.Message, failure);
        if (buffer.Length == 0) return 0;
        var value = await _received.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        buffer.Span[0] = value;
        byte[]? text = null;
        lock (_modeLock)
        {
            if (_mode == BinaryReceiveMode.ObserveText)
            {
                HasObservedText = true; _observedText.Add(value);
                if (value == 10 || _observedText.Count >= 256)
                { text = _observedText.ToArray(); _observedText.Clear(); }
            }
        }
        if (text is not null) _observeText?.Invoke(text);
        return 1; // 不预取：最终 ACK 后的启动日志留给正常接收链路。
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _failure) is { } failure) throw new IOException(failure.Message, failure);
        return _write(buffer, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        await _release(this).ConfigureAwait(false);
        _received.Writer.TryComplete();
    }
}
