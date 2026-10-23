using SerialLog.App.ViewModels;
using SerialLog.Core.Configuration;
using SerialLog.Core.Collaboration;
using SerialLog.Core.Logging;

namespace SerialLog.Tests;

public class SentCommandEchoTests
{
    [Fact]
    public void Echo_records_readable_sent_text_and_uses_existing_log_display_and_share_events()
    {
        var now = DateTimeOffset.Parse("2026-10-14T12:00:00+08:00");
        using var window = new SerialWindowViewModel("node", "节点", new FixedClock(now), refreshPortsOnCreate: false);
        var shared = new List<ReceivedLogLine>(); window.LinesReceived += (_, lines) => shared.AddRange(lines);
        window.RecordSentCommand("AT\r\n中文数据\r\n");
        var line = Assert.Single(shared); Assert.Equal(now, line.Timestamp);
        Assert.Equal(@"[SerialLog] [发送] AT\r\n中文数据", line.Text);
        Assert.Contains("[发送]", Assert.Single(window.Lines).Text);
        window.ShowSentCommands = false; window.RecordSentCommand("not displayed");
        Assert.Single(shared); Assert.Single(window.Lines);
    }

    [Fact]
    public async Task Failed_actual_serial_write_does_not_echo_success()
    {
        using var window = new SerialWindowViewModel("node", "节点", refreshPortsOnCreate: false);
        var shared = new List<ReceivedLogLine>(); window.LinesReceived += (_, lines) => shared.AddRange(lines);
        await Assert.ThrowsAnyAsync<Exception>(() => window.SendAsync("AT\r\n", default));
        Assert.Empty(shared); Assert.Empty(window.Lines);
    }

    [Fact]
    public async Task Remote_request_does_not_generate_local_echo_before_receiving_source_log()
    {
        var sent = new List<string>();
        var source = new CollaborationClientSnapshot("pc", "来源电脑", "#123456", []);
        using var window = SerialWindowViewModel.CreateRemote(source, new("node", "节点", "COM7", 460800, true, 0),
            (_, payload, _) => { sent.Add(payload); return Task.CompletedTask; });
        await window.SendAsync("AT\r\n", default);
        Assert.Equal("AT\r\n", Assert.Single(sent)); Assert.Empty(window.Lines);
        window.AppendRemoteLine(new(DateTimeOffset.Now, "[SerialLog] [发送] AT"));
        Assert.Single(window.Lines);
    }

    [Fact]
    public void Echo_is_written_to_the_same_auto_saved_log_file_when_enabled()
    {
        var directory = Path.Combine(Path.GetTempPath(), "echo-persist-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var window = new SerialWindowViewModel("node", "节点", refreshPortsOnCreate: false);
            window.BeginNewLogSession(directory); window.AutoSaveEnabled = true;
            window.RecordSentCommand("SENT\r\n"); window.ShowSentCommands = false; window.RecordSentCommand("HIDDEN\r\n");
            window.Dispose();
            var file = Assert.Single(Directory.GetFiles(directory, "*.log"));
            Assert.Contains("[SerialLog] [发送] SENT", File.ReadAllText(file));
            Assert.DoesNotContain("HIDDEN", File.ReadAllText(file));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void Echo_and_escape_settings_are_workspace_local_and_restored_after_export()
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        using var a = new MainViewModel(Path.Combine(repo, "artifacts/echo-a.json"), false,
            configuration: new() { SerialWindows = [new() { Id = "A" }] });
        using var b = new MainViewModel(Path.Combine(repo, "artifacts/echo-b.json"), false,
            configuration: new() { SerialWindows = [new() { Id = "B" }] });
        a.ShowSentCommands = false; a.InterpretCommandEscapes = false;
        Assert.False(a.SerialWindows.Single().ShowSentCommands);
        Assert.True(b.ShowSentCommands && b.InterpretCommandEscapes && b.SerialWindows.Single().ShowSentCommands);
        using var restored = new MainViewModel(Path.Combine(repo, "artifacts/echo-restored.json"), false, configuration: a.ExportConfiguration());
        Assert.False(restored.ShowSentCommands || restored.InterpretCommandEscapes || restored.SerialWindows.Single().ShowSentCommands);
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock { public DateTimeOffset Now { get; } = now; }
}
