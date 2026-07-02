using SerialLog.Core.Logging;

namespace SerialLog.Core.Collaboration;

public enum CollaborationMessageType
{
    ClientSnapshot,
    LogLine,
    Command
}

public sealed record CollaborationWindowSnapshot(
    string Id,
    string Title,
    string? PortName,
    int BaudRate,
    bool IsConnected,
    long LineCount);

public sealed record CollaborationClientSnapshot(
    string PcId,
    string PcName,
    string PcColor,
    IReadOnlyList<CollaborationWindowSnapshot> Windows);

public sealed record CollaborationLogLine(
    string PcId,
    string WindowId,
    DateTimeOffset Timestamp,
    string Text)
{
    public ReceivedLogLine ToReceivedLogLine()
    {
        return new ReceivedLogLine(Timestamp, Text);
    }
}

public sealed record CollaborationCommand(string WindowId, string Payload);

public sealed class CollaborationMessage
{
    public CollaborationMessageType Type { get; init; }

    public CollaborationClientSnapshot? Client { get; init; }

    public CollaborationLogLine? LogLine { get; init; }

    public CollaborationCommand? Command { get; init; }

    public static CollaborationMessage ForClientSnapshot(CollaborationClientSnapshot snapshot)
    {
        return new CollaborationMessage
        {
            Type = CollaborationMessageType.ClientSnapshot,
            Client = snapshot
        };
    }

    public static CollaborationMessage ForLogLine(CollaborationLogLine logLine)
    {
        return new CollaborationMessage
        {
            Type = CollaborationMessageType.LogLine,
            LogLine = logLine
        };
    }

    public static CollaborationMessage ForCommand(CollaborationCommand command)
    {
        return new CollaborationMessage
        {
            Type = CollaborationMessageType.Command,
            Command = command
        };
    }
}
