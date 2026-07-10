using System.Text;

namespace SerialLog.Core.Logging;

public sealed class LogLineParser
{
    private const char ReplacementCharacter = '\uFFFD';
    private static readonly string[] KnownLineAnchors =
    [
        "INFO ",
        "WARN ",
        "ERROR ",
        "DEBUG ",
        "TRACE ",
        "resource:",
        "spi_com:",
        "wiota:",
        "DTU ",
        "node_",
        "fn ",
        "total ",
        "+",
        "AT"
    ];

    private readonly StringBuilder _buffer = new();
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();

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

    public IReadOnlyList<ReceivedLogLine> Append(ReadOnlySpan<byte> chunk, DateTimeOffset receivedAt)
    {
        if (chunk.Length == 0)
        {
            return [];
        }

        var charCount = _decoder.GetCharCount(chunk, flush: false);
        if (charCount == 0)
        {
            return [];
        }

        var chars = new char[charCount];
        _decoder.GetChars(chunk, chars, flush: false);
        return Append(new string(chars), receivedAt);
    }

    public void Reset()
    {
        _buffer.Clear();
        _decoder.Reset();
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

        var normalized = RemoveUnsupportedControlCharacters(line.TrimEnd('\0'));
        var anchorIndex = FindNoisyPrefixAnchor(normalized);
        if (anchorIndex > 0)
        {
            normalized = normalized[anchorIndex..];
        }

        normalized = normalized.Replace(ReplacementCharacter.ToString(), string.Empty);
        return HasVisibleText(normalized) ? normalized : string.Empty;
    }

    private static int FindNoisyPrefixAnchor(string text)
    {
        if (text.Length == 0)
        {
            return -1;
        }

        var first = KnownLineAnchors
            .Select(anchor => text.IndexOf(anchor, StringComparison.Ordinal))
            .Where(index => index > 0)
            .DefaultIfEmpty(-1)
            .Min();
        if (first <= 0)
        {
            return -1;
        }

        var prefix = RemoveAnsiEscapeSequences(text[..first]);
        if (prefix.Length == 0)
        {
            return -1;
        }

        if (prefix.IndexOf(ReplacementCharacter) >= 0)
        {
            return first;
        }

        var visible = prefix.Where(ch => !char.IsWhiteSpace(ch)).ToArray();
        if (visible.Length == 0)
        {
            return first;
        }

        var lettersOrDigits = visible.Count(char.IsLetterOrDigit);
        return visible.Length >= 2 && lettersOrDigits * 2 < visible.Length ? first : -1;
    }

    private static string RemoveAnsiEscapeSequences(string text)
    {
        var builder = new StringBuilder(text.Length);
        var index = 0;
        while (index < text.Length)
        {
            if (text[index] == '\u001b' && index + 1 < text.Length && text[index + 1] == '[')
            {
                index += 2;
                while (index < text.Length && !(text[index] is >= '\u0040' and <= '\u007E'))
                {
                    index++;
                }

                index = Math.Min(index + 1, text.Length);
                continue;
            }

            builder.Append(text[index]);
            index++;
        }

        return builder.ToString();
    }

    private static string RemoveUnsupportedControlCharacters(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch == '\t' || ch == '\u001b' || !char.IsControl(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static bool HasVisibleText(string text)
    {
        return text.Any(ch => !char.IsControl(ch) && !char.IsWhiteSpace(ch));
    }
}
