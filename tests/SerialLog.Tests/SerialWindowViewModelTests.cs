using SerialLog.App.ViewModels;
using SerialLog.Core.Collaboration;
using SerialLog.Core.Logging;

namespace SerialLog.Tests;

public sealed class SerialWindowViewModelTests
{
    [Fact]
    public void Baud_rate_text_accepts_common_and_custom_values()
    {
        var window = new SerialWindowViewModel("center", "中心", refreshPortsOnCreate: false);

        Assert.Contains("115200", window.BaudRateOptions);
        Assert.Contains("460800", window.BaudRateOptions);

        window.BaudRateText = "460800";
        Assert.Equal(460800, window.BaudRate);

        window.BaudRateText = "123456";
        Assert.Equal(123456, window.BaudRate);
    }

    [Fact]
    public void Baud_rate_text_rejects_invalid_value()
    {
        var window = new SerialWindowViewModel("center", "中心", refreshPortsOnCreate: false);

        window.BaudRateText = "abc";

        Assert.Equal(115200, window.BaudRate);
        Assert.Contains("波特率", window.StatusText);
    }

    [Fact]
    public void Auto_refresh_ports_keeps_selected_port_without_error_status()
    {
        var window = new SerialWindowViewModel(
            "center",
            "中心",
            portNameProvider: () => throw new PlatformNotSupportedException("serial ports unavailable"),
            refreshPortsOnCreate: false)
        {
            PortName = "COM13",
            StatusText = "未连接"
        };

        window.AutoRefreshPorts();

        Assert.Equal("COM13", window.PortName);
        Assert.Contains("COM13", window.AvailablePorts);
        Assert.Equal("未连接", window.StatusText);
    }

    [Fact]
    public void Refresh_ports_keeps_window_alive_when_port_provider_fails()
    {
        var window = new SerialWindowViewModel(
            "center",
            "中心",
            portNameProvider: () => throw new PlatformNotSupportedException("serial ports unavailable"))
        {
            PortName = "COM13"
        };

        window.RefreshPorts();

        Assert.Equal("COM13", window.PortName);
        Assert.Contains("COM13", window.AvailablePorts);
        Assert.Contains("刷新端口失败", window.StatusText);
    }

    [Fact]
    public void Remote_window_creation_does_not_refresh_local_ports()
    {
        var client = new CollaborationClientSnapshot(
            "pc-r1",
            "R1-PC",
            "#16A34A",
            []);
        var snapshot = new CollaborationWindowSnapshot("w1", "R1", "COM10", 115200, true, 12);

        var window = SerialWindowViewModel.CreateRemote(
            client,
            snapshot,
            (_, _, _) => Task.CompletedTask);

        Assert.True(window.IsRemote);
        Assert.Equal("COM10", window.PortName);
        Assert.Equal(["COM10"], window.AvailablePorts);
    }

    [Fact]
    public async Task Remote_window_sends_through_collaboration_sender_and_accepts_remote_logs()
    {
        var sent = new List<(string WindowId, string Payload)>();
        var client = new CollaborationClientSnapshot(
            "pc-r1",
            "R1-PC",
            "#16A34A",
            []);
        var snapshot = new CollaborationWindowSnapshot("w1", "R1", "COM10", 115200, true, 12);

        var window = SerialWindowViewModel.CreateRemote(
            client,
            snapshot,
            (windowId, payload, _) =>
            {
                sent.Add((windowId, payload));
                return Task.CompletedTask;
            });

        await window.SendAsync("AT\r\n", CancellationToken.None);
        window.AppendRemoteLine(new ReceivedLogLine(DateTimeOffset.Parse("2026-07-02T12:30:00.123+08:00"), "OK"));

        Assert.True(window.IsRemote);
        Assert.True(window.IsConnected);
        Assert.Equal("remote:pc-r1:w1", window.Id);
        Assert.Equal([("w1", "AT\r\n")], sent);
        Assert.Equal(13, window.LineCount);
        Assert.Equal("[2026-07-02 12:30:00.123] OK", window.Lines.Single().Text);
        Assert.Equal("[12:30:00.123] OK", window.Lines.Single().DisplayText);
    }

    [Fact]
    public void Log_line_display_uses_short_timestamp_and_ansi_color_segments()
    {
        var line = new LogLineViewModel(new ReceivedLogLine(
            DateTimeOffset.Parse("2026-07-02T12:30:00.123+08:00"),
            "OK \u001b[31mERR\u001b[0m DONE"));

        Assert.Equal("[2026-07-02 12:30:00.123] OK \u001b[31mERR\u001b[0m DONE", line.Text);
        Assert.Equal("[12:30:00.123] OK ERR DONE", line.DisplayText);
        Assert.Equal("[2026-07-02 12:30:00.123] OK ERR DONE", line.CopyText);
        Assert.Collection(line.DisplaySegments,
            segment =>
            {
                Assert.Equal("[12:30:00.123] ", segment.Text);
                Assert.Equal("#6B7280", segment.Foreground);
            },
            segment =>
            {
                Assert.Equal("OK ", segment.Text);
                Assert.Null(segment.Foreground);
            },
            segment =>
            {
                Assert.Equal("ERR", segment.Text);
                Assert.Equal("#DC2626", segment.Foreground);
            },
            segment =>
            {
                Assert.Equal(" DONE", segment.Text);
                Assert.Null(segment.Foreground);
            });
    }

    [Fact]
    public void Log_line_display_ignores_non_color_ansi_sequences()
    {
        var line = new LogLineViewModel(new ReceivedLogLine(
            DateTimeOffset.Parse("2026-07-10T16:00:21.357+08:00"),
            "\u001b[2J\u001b[H\u001b[32mINFO\u001b[0m ready"));

        Assert.Equal("[16:00:21.357] INFO ready", line.DisplayText);
        Assert.Collection(line.DisplaySegments,
            segment =>
            {
                Assert.Equal("[16:00:21.357] ", segment.Text);
                Assert.Equal("#6B7280", segment.Foreground);
            },
            segment =>
            {
                Assert.Equal("INFO", segment.Text);
                Assert.Equal("#16A34A", segment.Foreground);
            },
            segment =>
            {
                Assert.Equal(" ready", segment.Text);
                Assert.Null(segment.Foreground);
            });
    }
}
