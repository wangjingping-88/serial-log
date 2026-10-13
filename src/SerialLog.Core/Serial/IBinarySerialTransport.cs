namespace SerialLog.Core.Serial;

public enum BinaryReceiveMode { Raw, ObserveText, LogOnly }

/// <summary>已连接串口的独占二进制通道；释放后恢复日志接收。</summary>
public interface IBinarySerialTransport : IAsyncDisposable
{
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
    ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken);
    ValueTask SetReceiveModeAsync(BinaryReceiveMode mode, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    ValueTask DiscardReceiveBufferAsync() => ValueTask.CompletedTask;
}
