using System.Text;

namespace SerialLog.Core.Logging;

public sealed class LogLineParser
{
    private const char ReplacementCharacter = '\uFFFD';
    private readonly StringBuilder _buffer = new();

    public IReadOnlyList<ReceivedLogLine> Append(string chunk, DateTimeOffset receivedAt)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return [];
        }

        var completed = new List<ReceivedLogLine>();

        foreach (var ch in chunk)
        {
            if (ch == '\r' || ch == '\n')
            {
                if (_buffer.Length == 0)
                {
                    continue;
                }

                AddCompletedLine(completed, receivedAt, _buffer.ToString());
                _buffer.Clear();
                continue;
            }

            _buffer.Append(ch);
        }

        return completed;
    }

    private static void AddCompletedLine(ICollection<ReceivedLogLine> completed, DateTimeOffset receivedAt, string? rawLine = null)
    {
        var text = NormalizeTextLine(rawLine ?? string.Empty);
        if (text.Length > 0)
        {
            completed.Add(new ReceivedLogLine(receivedAt, text));
        }
    }

    private static string NormalizeTextLine(string line)
    {
        if (line.Length == 0)
        {
            return string.Empty;
        }

        var normalized = line.TrimEnd('\0');
        if (normalized.IndexOf(ReplacementCharacter) < 0)
        {
            return normalized;
        }

        var firstPlus = normalized.IndexOf('+');
        if (firstPlus > 0)
        {
            normalized = normalized[firstPlus..];
        }

        normalized = normalized.Replace(ReplacementCharacter.ToString(), string.Empty);
        return HasVisibleText(normalized) ? normalized : string.Empty;
    }

    private static bool HasVisibleText(string text)
    {
        return text.Any(ch => !char.IsControl(ch) && !char.IsWhiteSpace(ch));
    }
}
