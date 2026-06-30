namespace SerialLog.Core.Tdma;

public sealed record TdmaLogEvent
{
    public required TdmaEventType Type { get; init; }

    public required string NodeName { get; init; }

    public required string RawLine { get; init; }

    public string? LocalAddress { get; init; }

    public int? Frame { get; init; }

    public int? Slot { get; init; }

    public string? SourceAddress { get; init; }

    public string? DestinationAddress { get; init; }

    public int? PacketNumber { get; init; }

    public string? Direction { get; init; }

    public int? Result { get; init; }

    public int? ReturnCode { get; init; }

    public bool? Matched { get; init; }

    public string? SendResult { get; init; }
}
