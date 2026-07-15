namespace SerialLog.Core.EcoLink;

public sealed record EcoLinkTestAnalysisResult
{
    public bool Success { get; init; }

    public bool IsTerminal { get; init; }

    public string Stage { get; init; } = "waiting";

    public string Reason { get; init; } = string.Empty;

    public int RegisteredNodeCount { get; init; }

    public IReadOnlyDictionary<int, int> PhaseRounds { get; init; }
        = new Dictionary<int, int>();

    public int? SyncSubmitCount { get; init; }

    public int? SyncSuccessCount { get; init; }

    public int? SyncFailedCount { get; init; }

    public IReadOnlyList<string> Evidence { get; init; } = [];
}
