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

        Assert.Equal(CollaborationMessageType.ClientSnapshot, decoded.Type);
        Assert.NotNull(decoded.Client);
        Assert.Equal("pc-r1", decoded.Client.PcId);
        Assert.Equal("R1-LOG", decoded.Client.Windows.Single().Title);
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
}
