namespace SerialLog.Core.Logging;

public sealed record ReceivedLogLine(DateTimeOffset Timestamp, string Text)
{
    public string FormattedText => $"[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] {Text}";
}
