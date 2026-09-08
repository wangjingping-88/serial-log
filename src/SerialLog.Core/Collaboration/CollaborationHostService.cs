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
    private HashSet<string> _hostSubscriptions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _sharingEpochs = new(StringComparer.Ordinal);

    private void UpdateSharingEpochs(CollaborationClientSnapshot? before, CollaborationClientSnapshot? after)
    {
        static HashSet<string> Keys(CollaborationClientSnapshot? source) => source is null ? [] : source.Windows
            .Where(w => w.IsShared).Select(w => CollaborationIdentity.Window(source.PcId, source.WorkspaceId, w.Id)).ToHashSet(StringComparer.Ordinal);
        var changed = Keys(before);
        changed.SymmetricExceptWith(Keys(after));
        foreach (var id in changed) _sharingEpochs.AddOrUpdate(id, 1, (_, epoch) => epoch + 1);
    }
    public void SetSubscriptions(IReadOnlyList<string> subscriptions) =>
        Volatile.Write(ref _hostSubscriptions, subscriptions.ToHashSet(StringComparer.Ordinal));

    private bool IsShared(CollaborationLogLine line)
    {
        var snapshot = _hostSnapshot?.ConnectionId == line.ConnectionId ? _hostSnapshot :
            _clientSnapshots.GetValueOrDefault(line.ConnectionId);
        return snapshot?.Windows.Any(w => w.Id == line.WindowId && w.IsShared) == true;
    }

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

        var listener = new TcpListener(address, port);
        listener.Start();
        _stopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener = listener;
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

        if (!_clientSnapshots.TryGetValue(pcId, out var target) ||
            !target.Windows.Any(w => w.Id == windowId && w.IsShared && w.IsConnected))
            throw new InvalidOperationException("目标窗口未共享、未连接或已失效。");

        await connection.SendAsync(
            CollaborationMessage.ForCommand(new CollaborationCommand(windowId, payload, target.WorkspaceId)),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task PublishHostSnapshotAsync(
        CollaborationClientSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        await _relayLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            UpdateSharingEpochs(_hostSnapshot, snapshot);
            _hostSnapshot = snapshot;
            await BroadcastAsync(
                CollaborationMessage.ForClientSnapshot(snapshot),
                excludedPcId: snapshot.ConnectionId,
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
        await PublishHostLogLinesAsync([logLine], cancellationToken).ConfigureAwait(false);
    }

    public async Task PublishHostLogLinesAsync(
        IReadOnlyList<CollaborationLogLine> logLines,
        CancellationToken cancellationToken = default)
    {
        if (logLines.Count == 0)
        {
            return;
        }

        await _relayLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var line in logLines) BroadcastLogLine(CollaborationMessage.ForLogLine(line), line.ConnectionId);
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
            using (var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = false })
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }

                    CollaborationMessage message;
                    try { message = CollaborationMessageCodec.Decode(line); }
                    catch (InvalidOperationException exception)
                    {
                        await writer.WriteLineAsync(CollaborationMessageCodec.Encode(new CollaborationMessage
                        { Type = CollaborationMessageType.Error, Error = exception.Message })).ConfigureAwait(false);
                        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    switch (message.Type)
                    {
                        case CollaborationMessageType.ClientSnapshot when message.Client is not null:
                            if (connection is null)
                            {
                                connection = RegisterClient(tcpClient, writer, message.Client.ConnectionId);
                            }
                            else if (!string.Equals(
                                connection.PcId,
                                message.Client.ConnectionId,
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
                            EnsureMessageSource(connection, message.LogLine.ConnectionId);
                            MarkClientSeen(message.LogLine.ConnectionId);
                            await RelayClientLogLineAsync(message.LogLine, cancellationToken).ConfigureAwait(false);
                            if (IsShared(message.LogLine) && Volatile.Read(ref _hostSubscriptions).Contains(message.LogLine.SubscriptionId))
                                LogLineReceived?.Invoke(this, message.LogLine);
                            break;

                        case CollaborationMessageType.Subscriptions when connection is not null:
                            connection.SetSubscriptions(message.Subscriptions ?? []);
                            await connection.SendAsync(new CollaborationMessage { Type = CollaborationMessageType.Subscriptions }, cancellationToken).ConfigureAwait(false);
                            break;

                        case CollaborationMessageType.Heartbeat when message.Heartbeat is not null:
                            EnsureMessageSource(connection, message.Heartbeat.ConnectionId);
                            MarkClientSeen(message.Heartbeat.ConnectionId);
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
            UpdateSharingEpochs(_clientSnapshots.GetValueOrDefault(snapshot.ConnectionId), snapshot);
            _clientSnapshots[snapshot.ConnectionId] = snapshot;

            if (!connection.IsReady)
            {
                if (_hostSnapshot is not null &&
                    !string.Equals(_hostSnapshot.ConnectionId, snapshot.ConnectionId, StringComparison.OrdinalIgnoreCase))
                {
                    await connection.SendAsync(
                        CollaborationMessage.ForClientSnapshot(_hostSnapshot),
                        cancellationToken).ConfigureAwait(false);
                }

                foreach (var peerSnapshot in _clientSnapshots.Values
                    .Where(peer => !string.Equals(peer.ConnectionId, snapshot.ConnectionId, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(peer => peer.ConnectionId, StringComparer.OrdinalIgnoreCase))
                {
                    await connection.SendAsync(
                        CollaborationMessage.ForClientSnapshot(peerSnapshot),
                        cancellationToken).ConfigureAwait(false);
                }

                connection.MarkReady();
            }

            await BroadcastAsync(
                CollaborationMessage.ForClientSnapshot(snapshot),
                excludedPcId: snapshot.ConnectionId,
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
            BroadcastLogLine(CollaborationMessage.ForLogLine(logLine), logLine.ConnectionId);
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
        await BroadcastManyAsync([message], excludedPcId, cancellationToken).ConfigureAwait(false);
    }

    private async Task BroadcastManyAsync(
        IReadOnlyList<CollaborationMessage> messages,
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
                await destination.SendManyAsync(messages, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (
                ex is IOException or ObjectDisposedException or SocketException or InvalidOperationException)
            {
                // The heartbeat monitor or the connection receive loop will remove the dead peer.
            }
        }
    }

    private void BroadcastLogLine(CollaborationMessage message, string excludedPcId)
    {
        foreach (var destination in _clients.Values.Where(connection =>
            connection.IsReady &&
            !string.Equals(connection.PcId, excludedPcId, StringComparison.OrdinalIgnoreCase)))
        {
            destination.EnqueueLog(message);
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
        connection.IsShared = IsShared;
        connection.SharingEpoch = line => _sharingEpochs.GetValueOrDefault(line.SubscriptionId);
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
                if (_clientSnapshots.TryRemove(connection.PcId, out var removedSnapshot)) UpdateSharingEpochs(removedSnapshot, null);
                connection.Dispose();

                if (notifyPeers)
                {
                    await BroadcastAsync(
                        CollaborationMessage.ForPeerDisconnected(
                            new CollaborationPeerDisconnected(removedSnapshot?.PcId ?? connection.PcId, removedSnapshot?.WorkspaceId ?? "")),
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
        private HashSet<string> _subscriptions = new(StringComparer.Ordinal);
        private long _subscriptionEpoch;
        private sealed record PendingLog(CollaborationMessage Message, long SubscriptionEpoch, long SharingEpoch);
        public Func<CollaborationLogLine, long> SharingEpoch { get; set; } = _ => 0;
        public Func<CollaborationLogLine, bool> IsShared { get; set; } = _ => false;
        public void SetSubscriptions(IReadOnlyList<string> subscriptions)
        {
            Volatile.Write(ref _subscriptions, subscriptions.ToHashSet(StringComparer.Ordinal));
            Interlocked.Increment(ref _subscriptionEpoch);
        }
        private bool Accepts(CollaborationMessage message) => message.LogLine is null ||
            (IsShared(message.LogLine) && Volatile.Read(ref _subscriptions).Contains(message.LogLine.SubscriptionId));
        private readonly TcpClient _tcpClient;
        private readonly StreamWriter _writer;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly Queue<PendingLog> _pendingLogs = [];
        private readonly object _pendingLogsLock = new();
        private bool _isLogFlushScheduled;
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
            await SendManyAsync([message], cancellationToken).ConfigureAwait(false);
        }

        public async Task SendManyAsync(
            IReadOnlyList<CollaborationMessage> messages,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) == 1, this);
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) == 1, this);
                foreach (var message in messages)
                {
                    if (!Accepts(message)) continue;
                    await _writer.WriteLineAsync(CollaborationMessageCodec.Encode(message)).ConfigureAwait(false);
                }

                await _writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void EnqueueLog(CollaborationMessage message)
        {
            if (!Accepts(message)) return;
            var scheduleFlush = false;
            lock (_pendingLogsLock)
            {
                if (_pendingLogs.Count >= 50000)
                {
                    _pendingLogs.Dequeue();
                }

                _pendingLogs.Enqueue(new PendingLog(message, Interlocked.Read(ref _subscriptionEpoch), SharingEpoch(message.LogLine!)));
                if (!_isLogFlushScheduled)
                {
                    _isLogFlushScheduled = true;
                    scheduleFlush = true;
                }
            }

            if (scheduleFlush)
            {
                _ = Task.Run(FlushLogsAsync);
            }
        }

        private async Task FlushLogsAsync()
        {
            try
            {
                while (Volatile.Read(ref _isDisposed) == 0)
                {
                    List<PendingLog> batch = new(256);
                    lock (_pendingLogsLock)
                    {
                        while (_pendingLogs.Count > 0 && batch.Count < 256)
                        {
                            batch.Add(_pendingLogs.Dequeue());
                        }

                        if (batch.Count == 0)
                        {
                            _isLogFlushScheduled = false;
                            return;
                        }
                    }

                    await _sendLock.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        foreach (var item in batch)
                        {
                            if (item.SubscriptionEpoch != Interlocked.Read(ref _subscriptionEpoch) ||
                                item.SharingEpoch != SharingEpoch(item.Message.LogLine!) || !Accepts(item.Message)) continue;
                            await _writer.WriteLineAsync(CollaborationMessageCodec.Encode(item.Message)).ConfigureAwait(false);
                        }
                        await _writer.FlushAsync().ConfigureAwait(false);
                    }
                    finally { _sendLock.Release(); }
                }
            }
            catch (Exception ex) when (
                ex is IOException or ObjectDisposedException or SocketException or InvalidOperationException)
            {
                lock (_pendingLogsLock)
                {
                    _isLogFlushScheduled = false;
                }
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
