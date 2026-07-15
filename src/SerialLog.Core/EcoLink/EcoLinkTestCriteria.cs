namespace SerialLog.Core.EcoLink;

public sealed class EcoLinkTestCriteria
{
    public string ExtenderNodeName { get; set; } = "ex_async";

    public int ExpectedNodeCount { get; set; } = 5;

    public int ExpectedRoundsPerPhase { get; set; } = 100;

    public int[] RequiredPhases { get; set; } = [1, 2, 3];

    public string[] AbnormalPatterns { get; set; } =
    [
        "Hard fault",
        "assert failed",
        "node_frame test failed"
    ];
}
