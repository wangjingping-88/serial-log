using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SerialLog.Core.Collaboration;

public sealed class CollaborationHostService : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, HostClientConnection> _clients = new(StringComparer.OrdinalIgnoreCase);
    private TcpListener? _listener;
    private CancellationTokenSource? _stopCts;
    private Task? _acceptLoopTask;

    public event EventHandler<CollaborationClientSnapshot>? ClientSnapshotReceived;

    public event EventHandler<CollaborationLogLine>? LogLineReceived;

    public event EventHandler<string>? ClientDisconnected;

    public int Port { get; private set; }

    public bool IsRunning => _listener is not null;

    public Task StartAsync(IPAddress address, int port, CancellationToken cancellationToken = default)
    {
        if (_listener is not null)
        {
            return Task.CompletedTask;
        }

        _stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = new TcpListener(address, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoopTask = AcceptLoopAsync(_stopCts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_listener is null)
        {
            return;
        }

        _stopCts?.Cancel();
        _listener.Stop();
        _listener = null;

        foreach (var connection in _clients.Values)
        {
            connection.Dispose();
        }

        _clients.Clear();

        if (_acceptLoopTask is not null)
        {
            try
            {
                await _acceptLoopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        _acceptLoopTask = null;
        _stopCts?.Dispose();
        _stopCts = null;
    }

    public async Task SendCommandAsync(
        string pcId,
        string windowId,
        string payload,
        CancellationToken cancellationToken = default)
    {
        if (!_clients.TryGetValue(pcId, out var connection))
        {
            throw new InvalidOperationException($"协作客户端未连接：{pcId}");
        }

        await connection.SendAsync(
            CollaborationMessage.ForCommand(new CollaborationCommand(windowId, payload)),
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient tcpClient;
            try
            {
                tcpClient = await _listener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            _ = HandleClientAsync(tcpClient, cancellationToken);
        }
    }

    private async Task HandleClientAsync(TcpClient tcpClient, CancellationToken cancellationToken)
    {
        HostClientConnection? connection = null;
        try
        {
            using (tcpClient)
            using (var stream = tcpClient.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true))
            using (var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true })
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    var message = CollaborationMessageCodec.Decode(line);
                    switch (message.Type)
                    {
                        case CollaborationMessageType.ClientSnapshot when message.Client is not null:
                            if (connection is null ||
                                !string.Equals(connection.PcId, message.Client.PcId, StringComparison.OrdinalIgnoreCase))
                            {
                                connection = RegisterClient(tcpClient, writer, message.Client);
                            }

                            ClientSnapshotReceived?.Invoke(this, message.Client);
                            break;

                        case CollaborationMessageType.LogLine when message.LogLine is not null:
                            LogLineReceived?.Invoke(this, message.LogLine);
                            break;
                    }
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
        }
        finally
        {
            if (connection is not null &&
                _clients.TryGetValue(connection.PcId, out var registered) &&
                ReferenceEquals(connection, registered))
            {
                _clients.TryRemove(connection.PcId, out _);
                ClientDisconnected?.Invoke(this, connection.PcId);
            }
        }
    }

    private HostClientConnection RegisterClient(
        TcpClient tcpClient,
        StreamWriter writer,
        CollaborationClientSnapshot snapshot)
    {
        var connection = new HostClientConnection(snapshot.PcId, tcpClient, writer);
        _clients.AddOrUpdate(
            snapshot.PcId,
            connection,
            (_, oldConnection) =>
            {
                oldConnection.Dispose();
                return connection;
            });
        return connection;
    }

    private sealed class HostClientConnection(string pcId, TcpClient tcpClient, StreamWriter writer) : IDisposable
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public string PcId { get; } = pcId;

        public async Task SendAsync(CollaborationMessage message, CancellationToken cancellationToken)
        {
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await writer.WriteLineAsync(CollaborationMessageCodec.Encode(message)).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Dispose()
        {
            tcpClient.Dispose();
            _sendLock.Dispose();
        }
    }
}
