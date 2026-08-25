using System.Globalization;
using System.Text;
using SerialLog.App.ViewModels;

namespace SerialLog.App.Infrastructure;

public static class LogCopyHelper
{
    public static IReadOnlyList<LogLineViewModel> OrderSelectedLines(
        IEnumerable<LogLineViewModel> allLines,
        IEnumerable<LogLineViewModel> selectedLines)
    {
        var selectedSet = selectedLines.ToHashSet();
        if (selectedSet.Count == 0)
        {
            return [];
        }

        return allLines.Where(selectedSet.Contains).ToArray();
    }

    public static long EstimateCharacterCount(IReadOnlyList<LogLineViewModel> lines)
    {
        long characterCount = 0;
        for (var index = 0; index < lines.Count; index++)
        {
            characterCount += lines[index].Line.Text.Length + 26;
            if (index > 0)
            {
                characterCount += Environment.NewLine.Length;
            }
        }

        return characterCount;
    }

    public static string BuildText(IReadOnlyList<LogLineViewModel> lines)
    {
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var estimatedCharacters = EstimateCharacterCount(lines);
        var capacity = (int)Math.Min(estimatedCharacters, int.MaxValue);
        var builder = new StringBuilder(capacity);
        for (var index = 0; index < lines.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(Environment.NewLine);
            }

            var line = lines[index].Line;
            builder.Append('[');
            builder.Append(line.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            builder.Append("] ");
            builder.Append(AnsiLogTextParser.Strip(line.Text));
        }

        return builder.ToString();
    }
}
