namespace SerialLog.Core.Commands;

public sealed record CommandGroup(
    string Name,
    IReadOnlyList<string> TargetIds,
    IReadOnlyList<string> Commands,
    TimeSpan Delay,
    LineEnding LineEnding);
