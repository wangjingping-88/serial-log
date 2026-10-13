using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using SerialLog.Core.Collaboration;
using SerialLog.Core.Logging;

namespace SerialLog.Tests;

public sealed class WorkspaceCollaborationTests
{
    private static CollaborationClientSnapshot Snapshot(string pc, string workspace) => new(pc, pc, "#123456",
        [new("node", "节点", "COM7", 115200, true, 0, true)], workspace, workspace);
    private static string Target(string pc, string workspace) => CollaborationIdentity.Window(pc, workspace, "node");

    [Fact]
    public async Task Same_computer_multiple_workspaces_receive_only_subscribed_live_logs()
    {
        await using var host = new CollaborationHostService();
        await using var sourceA = new CollaborationClientService();
        await using var sourceB = new CollaborationClientService();
        await using var observer = new CollaborationClientService();
        var received = new ConcurrentBag<CollaborationLogLine>();
        var hostReceived = new ConcurrentBag<CollaborationLogLine>();
        var directory = new ConcurrentBag<CollaborationClientSnapshot>();
        observer.LogLineReceived += (_, line) => received.Add(line);
        observer.SnapshotReceived += (_, snapshot) => directory.Add(snapshot);
        host.LogLineReceived += (_, line) => hostReceived.Add(line);
        await host.StartAsync(IPAddress.Loopback, 0);
        await host.PublishHostSnapshotAsync(Snapshot("pc-host", "host"));
        await sourceA.ConnectAsync("127.0.0.1", host.Port, Snapshot("pc-source", "A"));
        await sourceB.ConnectAsync("127.0.0.1", host.Port, Snapshot("pc-source", "B"));
        await observer.ConnectAsync("127.0.0.1", host.Port, Snapshot("pc-observer", "view"));
        await sourceA.PublishLogLineAsync("node", new(DateTimeOffset.Now, "not subscribed"));
        // TCP 写入完成不等于主机处理完成；用源端同连接 ACK 固定跨连接的先后顺序。
        await sourceA.SetSubscriptionsAsync([]);
        await observer.SetSubscriptionsAsync([Target("pc-source", "A")]);
        await sourceA.PublishLogLineAsync("node", new(DateTimeOffset.Now, "A live"));
        await sourceB.PublishLogLineAsync("node", new(DateTimeOffset.Now, "B hidden"));
        await WaitUntil(() => received.Any(l => l.Text == "A live"));
        await observer.SetSubscriptionsAsync([]);
        await sourceA.PublishLogLineAsync("node", new(DateTimeOffset.Now, "unsubscribed"));
        await sourceA.PublishSnapshotAsync(Snapshot("pc-source", "A") with { Windows = [] });
        // 确认主机处理了撤销期间的日志和取消共享快照，再恢复观察端订阅。
        await sourceA.SetSubscriptionsAsync([]);
        await observer.SetSubscriptionsAsync([Target("pc-source", "A")]);
        await sourceA.PublishLogLineAsync("node", new(DateTimeOffset.Now, "unshared"));
        await sourceA.PublishSnapshotAsync(Snapshot("pc-source", "A"));
        await sourceA.PublishLogLineAsync("node", new(DateTimeOffset.Now, "reshared"));
        await WaitUntil(() => received.Any(l => l.Text == "reshared"));
        Assert.Empty(hostReceived);
        Assert.Contains(directory, s => s.PcId == "pc-source" && s.WorkspaceId == "A");
        Assert.Contains(directory, s => s.PcId == "pc-source" && s.WorkspaceId == "B");
        Assert.DoesNotContain(received, l => l.WorkspaceId == "B" || l.Text is "not subscribed" or "unsubscribed" or "unshared");
        Assert.True(sourceB.IsConnected);
        await sourceA.DisconnectAsync();
        await sourceA.ConnectAsync("127.0.0.1", host.Port, Snapshot("pc-source", "A"));
        await sourceA.PublishLogLineAsync("node", new(DateTimeOffset.Now, "reconnected"));
        await WaitUntil(() => received.Any(l => l.Text == "reconnected"));
        Assert.True(sourceB.IsConnected);
    }

    [Fact]
    public async Task Source_subscription_acknowledgement_confirms_prior_log_and_snapshot_processed()
    {
        await using var host = new CollaborationHostService();
        await using var source = new CollaborationClientService();
        var received = new ConcurrentBag<CollaborationLogLine>();
        var snapshots = new ConcurrentBag<CollaborationClientSnapshot>();
        host.LogLineReceived += (_, line) => received.Add(line);
        host.ClientSnapshotReceived += (_, snapshot) => snapshots.Add(snapshot);
        host.SetSubscriptions([Target("pc-source", "A")]);
        await host.StartAsync(IPAddress.Loopback, 0);
        var snapshot = Snapshot("pc-source", "A");
        await source.ConnectAsync("127.0.0.1", host.Port, snapshot);

        await source.PublishLogLineAsync("node", new(DateTimeOffset.Now, "before acknowledgement"));
        await source.PublishSnapshotAsync(snapshot with { Windows = [] });
        await source.SetSubscriptionsAsync([]);

        // 不靠延时或重复轮询：ACK 返回时，同连接前面的消息必须已被主机处理。
        Assert.Contains(received, line => line.Text == "before acknowledgement");
        Assert.Contains(snapshots, item => item.ConnectionId == snapshot.ConnectionId && item.Windows.Count == 0);
    }

    [Fact]
    public async Task Host_command_validates_workspace_shared_and_connection_state()
    {
        await using var host = new CollaborationHostService();
        await using var a = new CollaborationClientService();
        await using var b = new CollaborationClientService();
        await host.StartAsync(IPAddress.Loopback, 0);
        var snapshot = Snapshot("pc", "A");
        await a.ConnectAsync("127.0.0.1", host.Port, snapshot);
        await b.ConnectAsync("127.0.0.1", host.Port, Snapshot("pc", "B"));
        var command = new TaskCompletionSource<CollaborationCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bCommands = new ConcurrentBag<CollaborationCommand>();
        a.CommandReceived += (_, c) => command.TrySetResult(c);
        b.CommandReceived += (_, c) => bCommands.Add(c);
        await host.SendCommandAsync(snapshot.ConnectionId, "node", "AT");
        Assert.Equal("A", (await command.Task.WaitAsync(TimeSpan.FromSeconds(3))).WorkspaceId);
        await a.PublishSnapshotAsync(snapshot with { Windows = [] });
        await a.SetSubscriptionsAsync([]); // 确认主机已处理前面的快照。
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.SendCommandAsync(snapshot.ConnectionId, "node", "AT"));
        Assert.Empty(bCommands);
    }

    [Fact]
    public async Task Host_port_conflict_does_not_stop_existing_service_and_can_retry()
    {
        await using var a = new CollaborationHostService(); await using var b = new CollaborationHostService();
        await a.StartAsync(IPAddress.Loopback, 0);
        await Assert.ThrowsAsync<SocketException>(() => b.StartAsync(IPAddress.Loopback, a.Port));
        Assert.True(a.IsRunning); Assert.False(b.IsRunning);
        await b.StartAsync(IPAddress.Loopback, 0);
        Assert.True(b.IsRunning); Assert.NotEqual(a.Port, b.Port);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
