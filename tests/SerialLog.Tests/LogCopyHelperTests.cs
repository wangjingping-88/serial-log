using SerialLog.App.Infrastructure;
using SerialLog.App.ViewModels;
using SerialLog.Core.Logging;

namespace SerialLog.Tests;

public sealed class LogCopyHelperTests
{
    [Fact]
    public void Selected_lines_are_returned_in_display_order()
    {
        var lines = CreateLines("first", "second", "third");

        var selected = LogCopyHelper.OrderSelectedLines(
            lines,
            [lines[2], lines[0]]);

        Assert.Equal([lines[0], lines[2]], selected);
    }

    [Fact]
    public void Empty_selection_returns_no_lines()
    {
        var lines = CreateLines("first", "second");

        var selected = LogCopyHelper.OrderSelectedLines(lines, []);

        Assert.Empty(selected);
    }

    [Fact]
    public void Copy_text_preserves_order_and_strips_ansi_sequences()
    {
        var timestamp = DateTimeOffset.Parse("2026-08-25T15:30:00.123+08:00");
        var lines = new[]
        {
            new LogLineViewModel(new ReceivedLogLine(timestamp, "first")),
            new LogLineViewModel(new ReceivedLogLine(timestamp.AddMilliseconds(1), "\u001b[31mERROR\u001b[0m second"))
        };

        var text = LogCopyHelper.BuildText(lines);

        Assert.Equal(
            "[2026-08-25 15:30:00.123] first" + Environment.NewLine +
            "[2026-08-25 15:30:00.124] ERROR second",
            text);
    }

    [Fact]
    public void Estimated_character_count_is_not_smaller_than_built_text()
    {
        var lines = CreateLines("first", "second");

        var estimated = LogCopyHelper.EstimateCharacterCount(lines);
        var text = LogCopyHelper.BuildText(lines);

        Assert.True(estimated >= text.Length);
    }

    private static LogLineViewModel[] CreateLines(params string[] text)
    {
        var timestamp = DateTimeOffset.Parse("2026-08-25T15:30:00.123+08:00");
        return text.Select((value, index) => new LogLineViewModel(
                new ReceivedLogLine(timestamp.AddMilliseconds(index), value)))
            .ToArray();
    }
}
