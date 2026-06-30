using System.Text;

namespace SerialLog.Core.Logging;

public sealed class LogLineParser
{
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

                completed.Add(new ReceivedLogLine(receivedAt, _buffer.ToString()));
                _buffer.Clear();
                continue;
            }

            _buffer.Append(ch);
        }

        return completed;
    }
}
