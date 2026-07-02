using System.Net;
using SerialLog.Core.Collaboration;
using SerialLog.Core.Logging;

namespace SerialLog.Tests;

public sealed class CollaborationNetworkTests
{
    [Fact]
    public void Message_codec_round_trips_client_snapshot()
    {
        var snapshot = new CollaborationClientSnapshot(
            "pc-r1",
            "R1",
            "#16A34A",
            [
                new CollaborationWindowSnapshot("w1", "R1-LOG", "COM10", 115200, true, 42)
            ]);

        var encoded = CollaborationMessageCodec.Encode(CollaborationMessage.ForClientSnapshot(snapshot));
        var decoded = CollaborationMessageCodec.Decode(encoded);

        Assert.Equal(CollaborationProtocol.CurrentVersion, decoded.ProtocolVersion);
        Assert.Equal(CollaborationMessageType.ClientSnapshot, decoded.Type);
        Assert.NotNull(decoded.Client);
        Assert.Equal("pc-r1", decoded.Client.PcId);
        Assert.Equal("R1-LOG", decoded.Client.Windows.Single().Title);
    }

    [Fact]
    public void Message_codec_round_trips_heartbeat()
    {
        var heartbeat = new CollaborationHeartbeat(
            "pc-r1",
            DateTimeOffset.Parse("2026-07-02T12:30:00.123+08:00"));

        var encoded = CollaborationMessageCodec.Encode(CollaborationMessage.ForHeartbeat(heartbeat));
        var decoded = CollaborationMessageCodec.Decode(encoded);

        Assert.Equal(CollaborationProtocol.CurrentVersion, decoded.ProtocolVersion);
        Assert.Equal(CollaborationMessageType.Heartbeat, decoded.Type);
        Assert.NotNull(decoded.Heartbeat);
        Assert.Equal("pc-r1", decoded.Heartbeat.PcId);
        Assert.Equal(heartbeat.Timestamp, decoded.Heartbeat.Timestamp);
    }

    [Fact]
    public void Message_codec_rejects_protocol_mismatch()
    {
        var encoded = """
            {"protocolVersion":999,"type":"Heartbeat","heartbeat":{"pcId":"pc-r1","timestamp":"2026-07-02T12:30:00.123+08:00"}}
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => CollaborationMessageCodec.Decode(encoded));

        Assert.Contains("999", ex.Message);
        Assert.Contains(CollaborationProtocol.CurrentVersion.ToString(), ex.Message);
    }

    [Fact]
    public async Task Host_and_client_exchange_snapshot_log_and_command()
    {
        await using var host = new CollaborationHostService();
        await using var client = new CollaborationClientService();

        var snapshotReceived = new TaskCompletionSource<CollaborationClientSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var logReceived = new TaskCompletionSource<CollaborationLogLine>(TaskCreationOptions.RunContinuationsAsynchronously);
        var commandReceived = new TaskCompletionSource<CollaborationCommand>(TaskCreationOptions.RunContinuationsAsynchronously);

        host.ClientSnapshotReceived += (_, snapshot) => snapshotReceived.TrySetResult(snapshot);
        host.LogLineReceived += (_, logLine) => logReceived.TrySetResult(logLine);
        client.CommandReceived += (_, command) => commandReceived.TrySetResult(command);

        await host.StartAsync(IPAddress.Loopback, 0);
        var snapshot = new CollaborationClientSnapshot(
            "pc-r1",
            "R1",
            "#16A34A",
            [
                new CollaborationWindowSnapshot("w1", "R1-LOG", "COM10", 115200, true, 0)
            ]);

        await client.ConnectAsync(IPAddress.Loopback.ToString(), host.Port, snapshot);

        var receivedSnapshot = await snapshotReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("pc-r1", receivedSnapshot.PcId);
        Assert.Equal("w1", receivedSnapshot.Windows.Single().Id);

        var line = new ReceivedLogLine(DateTimeOffset.Parse("2026-07-02T12:30:00.123+08:00"), "AT OK");
        await client.PublishLogLineAsync("w1", line);

        var receivedLine = await logReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("pc-r1", receivedLine.PcId);
        Assert.Equal("w1", receivedLine.WindowId);
        Assert.Equal("AT OK", receivedLine.Text);

        await host.SendCommandAsync("pc-r1", "w1", "AT\r\n");

        var command = await commandReceived.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("w1", command.WindowId);
        Assert.Equal("AT\r\n", command.Payload);
    }

    [Fact]
    public async Task Host_marks_client_disconnected_when_heartbeat_times_out()
    {
        await using var host = new CollaborationHostService(
            heartbeatTimeout: TimeSpan.FromMilliseconds(150),
            heartbeatScanInterval: TimeSpan.FromMilliseconds(40));
        await using var client = new CollaborationClientService(
            heartbeatInterval: TimeSpan.FromSeconds(30));

        var disconnected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        host.ClientDisconnected += (_, pcId) => disconnected.TrySetResult(pcId);

        await host.StartAsync(IPAddress.Loopback, 0);
        await client.ConnectAsync(
            IPAddress.Loopback.ToString(),
            host.Port,
            new CollaborationClientSnapshot(
                "pc-r1",
                "R1",
                "#16A34A",
                [new CollaborationWindowSnapshot("w1", "R1-LOG", "COM10", 115200, true, 0)]));

        Assert.Equal("pc-r1", await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Client_raises_disconnected_when_host_stops()
    {
        await using var host = new CollaborationHostService();
        await using var client = new CollaborationClientService(
            heartbeatInterval: TimeSpan.FromMilliseconds(50));

        var disconnected = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Disconnected += (_, reason) => disconnected.TrySetResult(reason);

        await host.StartAsync(IPAddress.Loopback, 0);
        await client.ConnectAsync(
            IPAddress.Loopback.ToString(),
            host.Port,
            new CollaborationClientSnapshot(
                "pc-r1",
                "R1",
                "#16A34A",
                [new CollaborationWindowSnapshot("w1", "R1-LOG", "COM10", 115200, true, 0)]));

        await host.StopAsync();

        Assert.Contains("断开", await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }
}
