using System.Collections.Concurrent;
using System.Net;
using SerialLog.Core.Collaboration;
using SerialLog.Core.Configuration;

namespace SerialLog.Tests;

public class CommandPackageNetworkTests
{
    private static CollaborationClientSnapshot Snapshot(string pc, string workspace, bool supports = true) =>
        new(pc, pc, "#123456", [], workspace, workspace, supports);
    private static string Json => CommandGroupPackageCodec.Export([new() { Name = "测试", Commands = ["AT"] }], []);

    [Theory]
    [InlineData("host-client")]
    [InlineData("client-host")]
    [InlineData("client-client")]
    [InlineData("workspace")]
    public async Task Targeted_configuration_and_receipt_route_without_subscriptions_or_command_execution(string route)
    {
        await using var host = new CollaborationHostService();
        await using var a = new CollaborationClientService(); await using var b = new CollaborationClientService();
        await using var unrelated = new CollaborationClientService();
        var hs = Snapshot("host", "H"); var sa = Snapshot("pc", "A"); var sb = Snapshot("pc", "B");
        var hMessages = new ConcurrentBag<CollaborationCommandGroupTransfer>();
        var aMessages = new ConcurrentBag<CollaborationCommandGroupTransfer>(); var bMessages = new ConcurrentBag<CollaborationCommandGroupTransfer>();
        var otherMessages = new ConcurrentBag<CollaborationCommandGroupTransfer>(); var commands = new ConcurrentBag<CollaborationCommand>();
        host.CommandGroupTransferReceived += (_, t) => hMessages.Add(t); a.CommandGroupTransferReceived += (_, t) => aMessages.Add(t);
        b.CommandGroupTransferReceived += (_, t) => bMessages.Add(t); unrelated.CommandGroupTransferReceived += (_, t) => otherMessages.Add(t);
        a.CommandReceived += (_, c) => commands.Add(c); b.CommandReceived += (_, c) => commands.Add(c);
        await host.StartAsync(IPAddress.Loopback, 0); await host.PublishHostSnapshotAsync(hs);
        await a.ConnectAsync("127.0.0.1", host.Port, sa); await b.ConnectAsync("127.0.0.1", host.Port, sb);
        await unrelated.ConnectAsync("127.0.0.1", host.Port, Snapshot("other", "C"));
        Assert.True(a.CanTransferCommandGroups);
        var source = route == "host-client" ? hs.ConnectionId : sa.ConnectionId;
        var target = route == "client-host" ? hs.ConnectionId : sb.ConnectionId;
        var packet = new CollaborationCommandGroupTransfer(Guid.NewGuid().ToString("N"), source, target,
            route == "workspace" ? WorkspacePackageCodec.Export(WorkspacePackageTests.Source()) : Json,
            Kind: route == "workspace" ? ConfigurationPackageKind.Workspace : ConfigurationPackageKind.CommandGroups);
        if (route == "host-client") await host.SendCommandGroupsAsync(packet); else await a.SendCommandGroupsAsync(packet);
        var targetMessages = route == "client-host" ? hMessages : bMessages;
        await WaitUntil(() => !targetMessages.IsEmpty);
        var delivered = Assert.Single(targetMessages); Assert.Equal(packet.PackageJson, delivered.PackageJson);
        var receipt = new CollaborationCommandGroupTransfer(packet.Id, target, source, IsReceipt: true, Accepted: true, Kind: packet.Kind);
        if (route == "client-host") await host.SendCommandGroupsAsync(receipt); else await b.SendCommandGroupsAsync(receipt);
        var senderMessages = route == "host-client" ? hMessages : aMessages;
        await WaitUntil(() => senderMessages.Any(t => t.IsReceipt));
        Assert.True(Assert.Single(senderMessages).Accepted); Assert.Empty(otherMessages); Assert.Empty(commands);
    }

    [Fact]
    public async Task Offline_legacy_and_invalid_packages_return_failure_without_disconnect()
    {
        await using var host = new CollaborationHostService(); await using var a = new CollaborationClientService();
        await using var legacy = new CollaborationClientService();
        var h = Snapshot("h", "H"); var s = Snapshot("a", "A"); var old = Snapshot("old", "B", false);
        var replies = new ConcurrentBag<CollaborationCommandGroupTransfer>(); a.CommandGroupTransferReceived += (_, r) => replies.Add(r);
        await host.StartAsync(IPAddress.Loopback, 0); await host.PublishHostSnapshotAsync(h);
        await a.ConnectAsync("127.0.0.1", host.Port, s); await legacy.ConnectAsync("127.0.0.1", host.Port, old);
        foreach (var (destination, json) in new[] { ("missing", Json), (old.ConnectionId, Json), (h.ConnectionId, "{}") })
        {
            var id = Guid.NewGuid().ToString("N");
            await a.SendCommandGroupsAsync(new(id, s.ConnectionId, destination, json));
            await WaitUntil(() => replies.Any(r => r.Id == id));
            Assert.False(replies.Single(r => r.Id == id).Accepted);
        }
        await a.SetSubscriptionsAsync([]); Assert.True(a.IsConnected); Assert.True(host.IsRunning);
        await Assert.ThrowsAsync<InvalidOperationException>(() => a.SendCommandGroupsAsync(new(Guid.NewGuid().ToString("N"), "spoof", h.ConnectionId, Json)));
    }

    private static async Task WaitUntil(Func<bool> check)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!check()) await Task.Delay(10, timeout.Token);
    }
}
