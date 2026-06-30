namespace SerialLog.Core.Tdma;

public sealed record TdmaLoopAnalysisResult
{
    public required bool Success { get; init; }

    public required string Stage { get; init; }

    public required string Reason { get; init; }

    public int? PacketNumber { get; init; }

    public int? DataFrame { get; init; }

    public bool HasSyncLost { get; init; }

    public IReadOnlyList<string> Evidence { get; init; } = [];
}
