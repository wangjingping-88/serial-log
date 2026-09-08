using SerialLog.Core.Logging;

namespace SerialLog.Core.Collaboration;

public enum CollaborationMessageType
{
    ClientSnapshot,
    LogLine,
    Command,
    Heartbeat,
    PeerDisconnected,
    Subscriptions,
    Error
}

public sealed record CollaborationWindowSnapshot(
    string Id,
    string Title,
    string? PortName,
    int BaudRate,
    bool IsConnected,
    long LineCount,
    bool IsShared = false);

public sealed record CollaborationClientSnapshot(
    string PcId,
    string PcName,
    string PcColor,
    IReadOnlyList<CollaborationWindowSnapshot> Windows,
    string WorkspaceId = "",
    string WorkspaceName = "默认测试")
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string ConnectionId => CollaborationIdentity.Connection(PcId, WorkspaceId);
}

public sealed record CollaborationLogLine(
    string PcId,
    string WindowId,
    DateTimeOffset Timestamp,
    string Text,
    string WorkspaceId = "")
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string ConnectionId => CollaborationIdentity.Connection(PcId, WorkspaceId);
    [System.Text.Json.Serialization.JsonIgnore]
    public string SubscriptionId => CollaborationIdentity.Window(PcId, WorkspaceId, WindowId);
    public ReceivedLogLine ToReceivedLogLine()
    {
        return new ReceivedLogLine(Timestamp, Text);
    }
}

public sealed record CollaborationCommand(string WindowId, string Payload, string WorkspaceId = "");

public sealed record CollaborationHeartbeat(string PcId, DateTimeOffset Timestamp, string WorkspaceId = "")
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string ConnectionId => CollaborationIdentity.Connection(PcId, WorkspaceId);
}

public static class CollaborationIdentity
{
    // 空工作区用于旧的内部调用；协议版本校验仍拒绝旧客户端。
    public static string Connection(string pc, string workspace) => string.IsNullOrEmpty(workspace) ? pc : $"{pc.Length}:{pc}{workspace}";
    public static string Window(string pc, string workspace, string window) => $"{Connection(pc, workspace)}:{window}";
}

public sealed record CollaborationPeerDisconnected(string PcId, string WorkspaceId = "")
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string ConnectionId => CollaborationIdentity.Connection(PcId, WorkspaceId);
}

public sealed class CollaborationMessage
{
    public int ProtocolVersion { get; init; } = CollaborationProtocol.CurrentVersion;

    public CollaborationMessageType Type { get; init; }

    public CollaborationClientSnapshot? Client { get; init; }

    public CollaborationLogLine? LogLine { get; init; }

    public CollaborationCommand? Command { get; init; }

    public CollaborationHeartbeat? Heartbeat { get; init; }

    public CollaborationPeerDisconnected? PeerDisconnected { get; init; }
    public IReadOnlyList<string>? Subscriptions { get; init; }
    public string? Error { get; init; }

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

    public static CollaborationMessage ForHeartbeat(CollaborationHeartbeat heartbeat)
    {
        return new CollaborationMessage
        {
            Type = CollaborationMessageType.Heartbeat,
            Heartbeat = heartbeat
        };
    }

    public static CollaborationMessage ForPeerDisconnected(CollaborationPeerDisconnected peerDisconnected)
    {
        return new CollaborationMessage
        {
            Type = CollaborationMessageType.PeerDisconnected,
            PeerDisconnected = peerDisconnected
        };
    }
}
