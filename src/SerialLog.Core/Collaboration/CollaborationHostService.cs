using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SerialLog.Core.Collaboration;

public sealed class CollaborationHostService : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, HostClientConnection> _clients =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CollaborationClientSnapshot> _clientSnapshots =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _relayLock = new(1, 1);
    private readonly TimeSpan _heartbeatTimeout;
    private readonly TimeSpan _heartbeatScanInterval;
    private CollaborationClientSnapshot? _hostSnapshot;
    private TcpListener? _listener;
    private CancellationTokenSource? _stopCts;
    private Task? _acceptLoopTask;
    private Task? _heartbeatMonitorTask;

    public CollaborationHostService(
        TimeSpan? heartbeatTimeout = null,
        TimeSpan? heartbeatScanInterval = null)
    {
        _heartbeatTimeout = heartbeatTimeout ?? TimeSpan.FromSeconds(10);
        _heartbeatScanInterval = heartbeatScanInterval ?? TimeSpan.FromSeconds(2);
    }

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
        _heartbeatMonitorTask = HeartbeatMonitorLoopAsync(_stopCts.Token);
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
        _clientSnapshots.Clear();
        _hostSnapshot = null;

        await IgnoreShutdownExceptionAsync(_acceptLoopTask).ConfigureAwait(false);
        await IgnoreShutdownExceptionAsync(_heartbeatMonitorTask).ConfigureAwait(false);

        _acceptLoopTask = null;
        _heartbeatMonitorTask = null;
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

    public async Task PublishHostSnapshotAsync(
        CollaborationClientSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        await _relayLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _hostSnapshot = snapshot;
            await BroadcastAsync(
                CollaborationMessage.ForClientSnapshot(snapshot),
                excludedPcId: snapshot.PcId,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _relayLock.Release();
        }
    }

    public async Task PublishHostLogLineAsync(
        CollaborationLogLine logLine,
        CancellationToken cancellationToken = default)
    {
        await _relayLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await BroadcastAsync(
                CollaborationMessage.ForLogLine(logLine),
                excludedPcId: logLine.PcId,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _relayLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _relayLock.Dispose();
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
                            if (connection is null)
                            {
                                connection = RegisterClient(tcpClient, writer, message.Client.PcId);
                            }
                            else if (!string.Equals(
                                connection.PcId,
                                message.Client.PcId,
                                StringComparison.OrdinalIgnoreCase))
                            {
                                throw new InvalidOperationException("同一协作连接不能切换 PcId。");
                            }

                            connection.MarkSeen();
                            await RelayClientSnapshotAsync(connection, message.Client, cancellationToken)
                                .ConfigureAwait(false);
                            ClientSnapshotReceived?.Invoke(this, message.Client);
                            break;

                        case CollaborationMessageType.LogLine when message.LogLine is not null:
                            EnsureMessageSource(connection, message.LogLine.PcId);
                            MarkClientSeen(message.LogLine.PcId);
                            await RelayClientLogLineAsync(message.LogLine, cancellationToken).ConfigureAwait(false);
                            LogLineReceived?.Invoke(this, message.LogLine);
                            break;

                        case CollaborationMessageType.Heartbeat when message.Heartbeat is not null:
                            EnsureMessageSource(connection, message.Heartbeat.PcId);
                            MarkClientSeen(message.Heartbeat.PcId);
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
        catch (InvalidOperationException)
        {
        }
        finally
        {
            if (connection is not null)
            {
                await RemoveClientAsync(connection, notifyPeers: !cancellationToken.IsCancellationRequested)
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task RelayClientSnapshotAsync(
        HostClientConnection connection,
        CollaborationClientSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await _relayLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _clientSnapshots[snapshot.PcId] = snapshot;

            if (!connection.IsReady)
            {
                if (_hostSnapshot is not null &&
                    !string.Equals(_hostSnapshot.PcId, snapshot.PcId, StringComparison.OrdinalIgnoreCase))
                {
                    await connection.SendAsync(
                        CollaborationMessage.ForClientSnapshot(_hostSnapshot),
                        cancellationToken).ConfigureAwait(false);
                }

                foreach (var peerSnapshot in _clientSnapshots.Values
                    .Where(peer => !string.Equals(peer.PcId, snapshot.PcId, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(peer => peer.PcId, StringComparer.OrdinalIgnoreCase))
                {
                    await connection.SendAsync(
                        CollaborationMessage.ForClientSnapshot(peerSnapshot),
                        cancellationToken).ConfigureAwait(false);
                }

                connection.MarkReady();
            }

            await BroadcastAsync(
                CollaborationMessage.ForClientSnapshot(snapshot),
                excludedPcId: snapshot.PcId,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _relayLock.Release();
        }
    }

    private async Task RelayClientLogLineAsync(
        CollaborationLogLine logLine,
        CancellationToken cancellationToken)
    {
        await _relayLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await BroadcastAsync(
                CollaborationMessage.ForLogLine(logLine),
                excludedPcId: logLine.PcId,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _relayLock.Release();
        }
    }

    private async Task BroadcastAsync(
        CollaborationMessage message,
        string? excludedPcId,
        CancellationToken cancellationToken)
    {
        var destinations = _clients.Values
            .Where(connection =>
                connection.IsReady &&
                !string.Equals(connection.PcId, excludedPcId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        foreach (var destination in destinations)
        {
            try
            {
                await destination.SendAsync(message, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (
                ex is IOException or ObjectDisposedException or SocketException or InvalidOperationException)
            {
                // The heartbeat monitor or the connection receive loop will remove the dead peer.
            }
        }
    }

    private async Task HeartbeatMonitorLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_heartbeatScanInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var connection in _clients.Values)
            {
                if (now - connection.LastSeenUtc <= _heartbeatTimeout)
                {
                    continue;
                }

                await RemoveClientAsync(connection, notifyPeers: true).ConfigureAwait(false);
            }
        }
    }

    private HostClientConnection RegisterClient(
        TcpClient tcpClient,
        StreamWriter writer,
        string pcId)
    {
        var connection = new HostClientConnection(pcId, tcpClient, writer);
        _clients.AddOrUpdate(
            pcId,
            connection,
            (_, oldConnection) =>
            {
                oldConnection.Dispose();
                return connection;
            });
        return connection;
    }

    private async Task RemoveClientAsync(HostClientConnection connection, bool notifyPeers)
    {
        var removed = false;
        await _relayLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_clients.TryGetValue(connection.PcId, out var registered) &&
                ReferenceEquals(connection, registered) &&
                _clients.TryRemove(connection.PcId, out _))
            {
                removed = true;
                _clientSnapshots.TryRemove(connection.PcId, out _);
                connection.Dispose();

                if (notifyPeers)
                {
                    await BroadcastAsync(
                        CollaborationMessage.ForPeerDisconnected(
                            new CollaborationPeerDisconnected(connection.PcId)),
                        excludedPcId: connection.PcId,
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _relayLock.Release();
        }

        if (removed)
        {
            ClientDisconnected?.Invoke(this, connection.PcId);
        }
    }

    private void MarkClientSeen(string pcId)
    {
        if (_clients.TryGetValue(pcId, out var connection))
        {
            connection.MarkSeen();
        }
    }

    private static void EnsureMessageSource(HostClientConnection? connection, string pcId)
    {
        if (connection is null ||
            !string.Equals(connection.PcId, pcId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("协作消息来源与连接身份不一致。");
        }
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
    }

    private sealed class HostClientConnection : IDisposable
    {
        private readonly TcpClient _tcpClient;
        private readonly StreamWriter _writer;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private long _lastSeenUnixMilliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        private int _isReady;
        private int _isDisposed;

        public HostClientConnection(string pcId, TcpClient tcpClient, StreamWriter writer)
        {
            PcId = pcId;
            _tcpClient = tcpClient;
            _writer = writer;
        }

        public string PcId { get; }

        public bool IsReady => Volatile.Read(ref _isReady) == 1;

        public DateTimeOffset LastSeenUtc =>
            DateTimeOffset.FromUnixTimeMilliseconds(Interlocked.Read(ref _lastSeenUnixMilliseconds));

        public void MarkReady()
        {
            Volatile.Write(ref _isReady, 1);
        }

        public void MarkSeen()
        {
            Interlocked.Exchange(ref _lastSeenUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }

        public async Task SendAsync(CollaborationMessage message, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) == 1, this);
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) == 1, this);
                await _writer.WriteLineAsync(CollaborationMessageCodec.Encode(message)).ConfigureAwait(false);
                await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
            {
                return;
            }

            _tcpClient.Dispose();
        }
    }
}
