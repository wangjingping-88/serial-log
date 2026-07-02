using System.Net.Sockets;
using System.Text;
using SerialLog.Core.Logging;

namespace SerialLog.Core.Collaboration;

public sealed class CollaborationClientService : IAsyncDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private TcpClient? _client;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _stopCts;
    private Task? _receiveLoopTask;
    private string _pcId = string.Empty;

    public event EventHandler<CollaborationCommand>? CommandReceived;

    public bool IsConnected => _client?.Connected == true;

    public async Task ConnectAsync(
        string host,
        int port,
        CollaborationClientSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        await DisconnectAsync().ConfigureAwait(false);

        _stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _client = new TcpClient();
        await _client.ConnectAsync(host, port, cancellationToken).ConfigureAwait(false);

        var stream = _client.GetStream();
        _reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        _writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
        _pcId = snapshot.PcId;

        await SendAsync(CollaborationMessage.ForClientSnapshot(snapshot), cancellationToken).ConfigureAwait(false);
        _receiveLoopTask = ReceiveLoopAsync(_stopCts.Token);
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

        if (_receiveLoopTask is not null)
        {
            try
            {
                await _receiveLoopTask.ConfigureAwait(false);
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

        _reader?.Dispose();
        _writer?.Dispose();
        _client?.Dispose();
        _stopCts?.Dispose();

        _reader = null;
        _writer = null;
        _client = null;
        _stopCts = null;
        _receiveLoopTask = null;
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
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_reader is null)
            {
                return;
            }

            var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            var message = CollaborationMessageCodec.Decode(line);
            if (message is { Type: CollaborationMessageType.Command, Command: not null })
            {
                CommandReceived?.Invoke(this, message.Command);
            }
        }
    }
}
