using SerialLog.Core.Logging;
using System.Text;

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
    public void Parser_trims_invalid_replacement_noise_before_known_log_anchor()
    {
        var parser = new LogLineParser();
        var timestamp = new DateTimeOffset(2026, 7, 8, 15, 7, 59, 963, TimeSpan.FromHours(8));

        var lines = parser.Append("\uFFFD%!\uFFFD1\uFFFD5\uFFFD7\uFFFD\uFFFD+Select modem,enter follow char:\r\n", timestamp);

        var line = Assert.Single(lines);
        Assert.Equal("+Select modem,enter follow char:", line.Text);
    }

    [Fact]
    public void Parser_keeps_normal_text_that_contains_plus()
    {
        var parser = new LogLineParser();
        var timestamp = new DateTimeOffset(2026, 7, 8, 15, 7, 59, 963, TimeSpan.FromHours(8));

        var lines = parser.Append("value=a+b\r\n", timestamp);

        var line = Assert.Single(lines);
        Assert.Equal("value=a+b", line.Text);
    }

    [Fact]
    public void Parser_decodes_utf8_bytes_split_across_chunks()
    {
        var parser = new LogLineParser();
        var timestamp = new DateTimeOffset(2026, 7, 10, 15, 30, 0, TimeSpan.FromHours(8));
        var bytes = Encoding.UTF8.GetBytes("INFO 中文日志\r\n");

        Assert.Empty(parser.Append(bytes.AsSpan(0, 7), timestamp));
        var lines = parser.Append(bytes.AsSpan(7), timestamp);

        var line = Assert.Single(lines);
        Assert.Equal("INFO 中文日志", line.Text);
    }

    [Fact]
    public void Parser_trims_invalid_byte_noise_before_known_log_anchor()
    {
        var parser = new LogLineParser();
        var timestamp = new DateTimeOffset(2026, 7, 10, 15, 30, 0, TimeSpan.FromHours(8));
        byte[] bytes = [0xF0, 0x28, 0x8C, 0x28, 0x21, 0x40, .. Encoding.UTF8.GetBytes("INFO spi_com: wiota_check_state ok\r\n")];

        var lines = parser.Append(bytes, timestamp);

        var line = Assert.Single(lines);
        Assert.Equal("INFO spi_com: wiota_check_state ok", line.Text);
    }

    [Fact]
    public void Parser_trims_punctuation_noise_before_known_log_anchor()
    {
        var parser = new LogLineParser();
        var timestamp = new DateTimeOffset(2026, 7, 10, 15, 30, 0, TimeSpan.FromHours(8));

        var lines = parser.Append("}&$~WARN resource: total 197056 used 86556\r\n", timestamp);

        var line = Assert.Single(lines);
        Assert.Equal("WARN resource: total 197056 used 86556", line.Text);
    }

    [Fact]
    public void Parser_preserves_ansi_escape_sequences_for_display_parsing()
    {
        var parser = new LogLineParser();
        var timestamp = new DateTimeOffset(2026, 7, 10, 16, 0, 21, 357, TimeSpan.FromHours(8));

        var lines = parser.Append("\u001b[32mINFO\u001b[0m spi_com: ready\r\n", timestamp);

        var line = Assert.Single(lines);
        Assert.Equal("\u001b[32mINFO\u001b[0m spi_com: ready", line.Text);
    }

    [Fact]
    public void Parser_preserves_c1_ansi_sequences_for_display_parsing()
    {
        var parser = new LogLineParser();
        var timestamp = new DateTimeOffset(2026, 7, 10, 16, 0, 21, 357, TimeSpan.FromHours(8));

        var lines = parser.Append("\u009b32mINFO\u009b0m ready\r\n", timestamp);

        var line = Assert.Single(lines);
        Assert.Equal("\u009b32mINFO\u009b0m ready", line.Text);
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
