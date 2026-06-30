using SerialLog.Core.Logging;

namespace SerialLog.Tests;

public class LoggingTests
{
    [Fact]
    public void Parser_adds_receive_timestamp_to_each_completed_line()
    {
        var parser = new LogLineParser();
        var timestamp = new DateTimeOffset(2026, 6, 30, 9, 1, 2, 345, TimeSpan.FromHours(8));

        var lines = parser.Append("boot ok\r\nready\npartial", timestamp);

        Assert.Collection(lines,
            line =>
            {
                Assert.Equal(timestamp, line.Timestamp);
                Assert.Equal("boot ok", line.Text);
                Assert.Equal("[2026-06-30 09:01:02.345] boot ok", line.FormattedText);
            },
            line =>
            {
                Assert.Equal(timestamp, line.Timestamp);
                Assert.Equal("ready", line.Text);
            });
    }

    [Fact]
    public void Parser_holds_partial_text_until_newline_arrives()
    {
        var parser = new LogLineParser();
        var first = new DateTimeOffset(2026, 6, 30, 9, 1, 2, 100, TimeSpan.FromHours(8));
        var second = new DateTimeOffset(2026, 6, 30, 9, 1, 2, 456, TimeSpan.FromHours(8));

        Assert.Empty(parser.Append("AT+GMR", first));

        var lines = parser.Append("\r\n", second);

        var line = Assert.Single(lines);
        Assert.Equal(second, line.Timestamp);
        Assert.Equal("AT+GMR", line.Text);
    }

    [Fact]
    public void Parser_ignores_empty_lines_from_blank_or_repeated_newlines()
    {
        var parser = new LogLineParser();
        var timestamp = new DateTimeOffset(2026, 6, 30, 14, 15, 7, 215, TimeSpan.FromHours(8));

        var lines = parser.Append("\r\nTDMA_RF_READY,132621534,0\r\r\n\nOK\r\n", timestamp);

        Assert.Collection(lines,
            line => Assert.Equal("TDMA_RF_READY,132621534,0", line.Text),
            line => Assert.Equal("OK", line.Text));
    }

    [Fact]
    public void Rolling_writer_stores_log_files_directly_in_the_connection_session_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "serial-log-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new FixedClock(new DateTimeOffset(2026, 6, 30, 9, 1, 2, TimeSpan.FromHours(8)));
            var writer = new RollingLogFileWriter(root, "主控串口", 90, clock);

            writer.WriteLine(new ReceivedLogLine(clock.Now, "first log line"));
            writer.WriteLine(new ReceivedLogLine(clock.Now, "second log line that makes the file roll"));

            var files = Directory.GetFiles(root, "*.log")
                .OrderBy(path => path)
                .ToArray();

            Assert.Equal(2, files.Length);
            Assert.EndsWith("主控串口_20260630_001.log", files[0]);
            Assert.EndsWith("主控串口_20260630_002.log", files[1]);
            Assert.Contains("first log line", File.ReadAllText(files[0]));
            Assert.Contains("second log line", File.ReadAllText(files[1]));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now { get; } = now;
    }
}
