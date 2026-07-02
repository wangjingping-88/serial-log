using System.Net.Sockets;
using System.Text;
using SerialLog.Core.Logging;

namespace SerialLog.Core.Collaboration;

public sealed class CollaborationClientService : IAsyncDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly TimeSpan _heartbeatInterval;
    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _stopCts;
    private Task? _receiveLoopTask;
    private Task? _heartbeatLoopTask;
    private string _pcId = string.Empty;
    private int _connectionLostRaised;

    public CollaborationClientService(TimeSpan? heartbeatInterval = null)
    {
        _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(3);
    }

    public event EventHandler<CollaborationCommand>? CommandReceived;

    public event EventHandler<string>? Disconnected;

    public bool IsConnected => _client?.Connected == true;

    public async Task ConnectAsync(
        string host,
        int port,
        CollaborationClientSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        await DisconnectAsync().ConfigureAwait(false);

        _connectionLostRaised = 0;
        _stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _client = new TcpClient();
        await _client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);

        var stream = _client.GetStream();
        _reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        _writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
        _pcId = snapshot.PcId;

        await SendAsync(CollaborationMessage.ForClientSnapshot(snapshot), cancellationToken).ConfigureAwait(false);
        _receiveLoopTask = ReceiveLoopAsync(_stopCts.Token);
        _heartbeatLoopTask = HeartbeatLoopAsync(_stopCts.Token);
    }

    public Task PublishSnapshotAsync(
        CollaborationClientSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        _pcId = snapshot.PcId;
        return SendAsync(CollaborationMessage.ForClientSnapshot(snapshot), cancellationToken);
    }

    public Task PublishLogLineAsync(
        string windowId,
        ReceivedLogLine line,
        CancellationToken cancellationToken = default)
    {
        var logLine = new CollaborationLogLine(_pcId, windowId, line.Timestamp, line.Text);
        return SendAsync(CollaborationMessage.ForLogLine(logLine), cancellationToken);
    }

    public async Task DisconnectAsync()
    {
        _stopCts?.Cancel();
        _client?.Close();

        await IgnoreShutdownExceptionAsync(_receiveLoopTask).ConfigureAwait(false);
        await IgnoreShutdownExceptionAsync(_heartbeatLoopTask).ConfigureAwait(false);

        _reader?.Dispose();
        _writer?.Dispose();
        _client?.Dispose();
        _stopCts?.Dispose();

        _reader = null;
        _writer = null;
        _client = null;
        _stopCts = null;
        _receiveLoopTask = null;
        _heartbeatLoopTask = null;
        _pcId = string.Empty;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        _sendLock.Dispose();
    }

    private async Task SendAsync(CollaborationMessage message, CancellationToken cancellationToken)
    {
        if (_writer is null)
        {
            throw new InvalidOperationException("协作客户端未连接。");
        }

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(CollaborationMessageCodec.Encode(message)).ConfigureAwait(false);
            await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_reader is null)
                {
                    return;
                }

                var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    NotifyConnectionLost("主机连接已断开");
                    return;
                }

                var message = CollaborationMessageCodec.Decode(line);
                if (message is { Type: CollaborationMessageType.Command, Command: not null })
                {
                    CommandReceived?.Invoke(this, message.Command);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
            NotifyConnectionLost("主机连接已断开");
        }
        catch (Exception ex)
        {
            NotifyConnectionLost(ex.Message);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_heartbeatInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await SendAsync(
                    CollaborationMessage.ForHeartbeat(new CollaborationHeartbeat(_pcId, DateTimeOffset.UtcNow)),
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
            NotifyConnectionLost("心跳发送失败，主机连接已断开");
        }
        catch (Exception ex)
        {
            NotifyConnectionLost($"心跳发送失败：{ex.Message}");
        }
    }

    private void NotifyConnectionLost(string reason)
    {
        if (Interlocked.Exchange(ref _connectionLostRaised, 1) == 1)
        {
            return;
        }

        _stopCts?.Cancel();
        Disconnected?.Invoke(this, reason);
    }

    private static async Task IgnoreShutdownExceptionAsync(Task? task)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (IOException)
        {
        }
    }
}
