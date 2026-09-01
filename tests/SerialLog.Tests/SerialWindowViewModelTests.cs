using SerialLog.App.ViewModels;
using SerialLog.Core.Collaboration;
using SerialLog.Core.Logging;

namespace SerialLog.Tests;

public sealed class SerialWindowViewModelTests
{
    [Fact]
    public void Connect_reports_unavailable_log_directory_without_throwing()
    {
        using var window = new SerialWindowViewModel("center", "中心", refreshPortsOnCreate: false)
        {
            PortName = "COM5"
        };
        window.SetLogSessionDirectoryProvider(
            () => throw new DirectoryNotFoundException("目标驱动器不存在"));

        var exception = Record.Exception(window.Connect);

        Assert.Null(exception);
        Assert.Contains("日志目录不可用", window.StatusText);
        Assert.Equal("日志目录不可用", window.SaveStatusText);
        Assert.False(window.IsConnected);
    }

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
    public void Auto_refresh_ports_keeps_selected_port_when_the_provider_omits_it()
    {
        var ports = new[] { "COM3", "COM4" };
        var window = new SerialWindowViewModel(
            "async",
            "Async",
            portNameProvider: () => ports,
            refreshPortsOnCreate: false)
        {
            PortName = "COM3"
        };

        window.RefreshPorts();
        ports = ["COM4"];
        window.AutoRefreshPorts();

        Assert.Equal("COM3", window.PortName);
        Assert.Contains("COM3", window.AvailablePorts);
        Assert.Contains("COM4", window.AvailablePorts);
    }

    [Fact]
    public void Remote_snapshot_refresh_keeps_the_selected_port()
    {
        var client = new CollaborationClientSnapshot("pc-r1", "R1-PC", "#16A34A", []);
        var snapshot = new CollaborationWindowSnapshot("w1", "R1", "COM10", 115200, true, 12);
        var window = SerialWindowViewModel.CreateRemote(
            client,
            snapshot,
            (_, _, _) => Task.CompletedTask);

        window.UpdateRemoteSnapshot(client, snapshot, (_, _, _) => Task.CompletedTask);

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
    public async Task Read_only_remote_window_cannot_be_selected_or_send_commands()
    {
        var client = new CollaborationClientSnapshot("pc-r1", "R1-PC", "#16A34A", []);
        var snapshot = new CollaborationWindowSnapshot("w1", "R1", "COM10", 115200, true, 12);
        var window = SerialWindowViewModel.CreateRemote(client, snapshot, sendCommandAsync: null);

        window.IsSelectedForSend = true;

        Assert.True(window.IsRemote);
        Assert.False(window.CanSendCommands);
        Assert.False(window.IsSelectedForSend);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => window.SendAsync("AT\r\n", CancellationToken.None));
    }

    [Fact]
    public async Task Remote_snapshot_can_revoke_existing_command_permission()
    {
        var client = new CollaborationClientSnapshot("pc-r1", "R1-PC", "#16A34A", []);
        var snapshot = new CollaborationWindowSnapshot("w1", "R1", "COM10", 115200, true, 12);
        var window = SerialWindowViewModel.CreateRemote(
            client,
            snapshot,
            (_, _, _) => Task.CompletedTask);

        window.UpdateRemoteSnapshot(client, snapshot, sendCommandAsync: null);

        Assert.False(window.CanSendCommands);
        Assert.False(window.IsSelectedForSend);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => window.SendAsync("AT\r\n", CancellationToken.None));
    }

    [Fact]
    public void Remote_window_can_save_received_logs_to_the_local_log_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "serial-log-remote-logs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var client = new CollaborationClientSnapshot("pc-r1", "R1-PC", "#16A34A", []);
            var snapshot = new CollaborationWindowSnapshot("w1", "R1", "COM10", 115200, true, 0);
            using var window = SerialWindowViewModel.CreateRemote(
                client,
                snapshot,
                (_, _, _) => Task.CompletedTask);

            window.ApplyLogRoot(root);
            window.AutoSaveEnabled = true;
            window.AppendRemoteLine(new ReceivedLogLine(
                DateTimeOffset.Parse("2026-07-10T16:00:21.357+08:00"),
                "INFO remote log"));
            window.Dispose();

            var logFile = Assert.Single(Directory.GetFiles(root, "*.log", SearchOption.AllDirectories));
            Assert.Contains("[2026-07-10 16:00:21.357] INFO remote log", File.ReadAllText(logFile));
            Assert.Equal("保存远端日志到本机", window.AutoSaveToolTip);
            Assert.Equal(System.Windows.Visibility.Collapsed, window.AutoSaveToggleVisibility);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
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

    [Fact]
    public void Log_line_display_ignores_c1_ansi_sequences()
    {
        var line = new LogLineViewModel(new ReceivedLogLine(
            DateTimeOffset.Parse("2026-07-10T16:00:21.357+08:00"),
            "\u009b2J\u009bH\u009b32mINFO\u009b0m ready"));

        Assert.Equal("[16:00:21.357] INFO ready", line.DisplayText);
        Assert.Contains(line.DisplaySegments, segment => segment.Text == "INFO" && segment.Foreground == "#16A34A");
    }

    [Fact]
    public void Reenabling_auto_save_starts_a_new_file_in_the_current_session()
    {
        var sessionDirectory = Path.Combine(
            Path.GetTempPath(),
            "serial-log-auto-save-toggle-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new FixedClock(new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.FromHours(8)));
            using var window = new SerialWindowViewModel(
                "node",
                "node",
                clock,
                refreshPortsOnCreate: false);
            window.BeginNewLogSession(sessionDirectory);

            window.AutoSaveEnabled = true;
            window.AppendRemoteLine(new ReceivedLogLine(clock.Now, "first file"));
            window.AutoSaveEnabled = false;
            window.AppendRemoteLine(new ReceivedLogLine(clock.Now, "not persisted"));
            window.AutoSaveEnabled = true;
            window.AppendRemoteLine(new ReceivedLogLine(clock.Now, "second file"));
            window.Dispose();

            var files = Directory.GetFiles(sessionDirectory, "*.log").OrderBy(path => path).ToArray();
            Assert.Equal(2, files.Length);
            Assert.Contains("first file", File.ReadAllText(files[0]));
            Assert.DoesNotContain("not persisted", File.ReadAllText(files[0]));
            Assert.Contains("second file", File.ReadAllText(files[1]));
        }
        finally
        {
            if (Directory.Exists(sessionDirectory))
            {
                Directory.Delete(sessionDirectory, true);
            }
        }
    }

    [Fact]
    public void High_rate_display_buffer_keeps_only_the_latest_five_thousand_lines()
    {
        using var window = new SerialWindowViewModel(
            "high-rate",
            "High rate",
            refreshPortsOnCreate: false);
        window.IsLogAutoScrollPaused = true;
        var timestamp = DateTimeOffset.Parse("2026-07-31T10:30:00+08:00");
        var lines = Enumerable.Range(0, 6_000)
            .Select(index => new ReceivedLogLine(timestamp.AddMilliseconds(index), $"line {index}"))
            .ToArray();
        var queueLines = typeof(SerialWindowViewModel).GetMethod(
            "QueueLines",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(queueLines);
        queueLines.Invoke(window, [lines]);

        Assert.Equal(5_000, window.Lines.Count);
        Assert.EndsWith("line 1000", window.Lines[0].Text);
        Assert.EndsWith("line 5999", window.Lines[^1].Text);
        Assert.Equal(6_000, window.LineCount);
        Assert.True(window.IsLogAutoScrollPaused);
    }

    [Fact]
    public void Clearing_log_resumes_auto_scroll()
    {
        using var window = new SerialWindowViewModel(
            "clear-log",
            "Clear log",
            refreshPortsOnCreate: false);
        window.IsLogAutoScrollPaused = true;
        window.LogHorizontalOffset = 240;
        window.LogVerticalOffset = 320;
        window.HasLogScrollPosition = true;
        window.AppendRemoteLine(new ReceivedLogLine(
            DateTimeOffset.Parse("2026-07-31T10:30:00+08:00"),
            "line"));

        window.Clear();

        Assert.Empty(window.Lines);
        Assert.Equal(0, window.LineCount);
        Assert.False(window.IsLogAutoScrollPaused);
        Assert.Equal(0, window.LogHorizontalOffset);
        Assert.Equal(0, window.LogVerticalOffset);
        Assert.False(window.HasLogScrollPosition);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now { get; } = now;
    }
}
