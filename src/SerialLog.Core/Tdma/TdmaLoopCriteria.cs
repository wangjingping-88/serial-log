namespace SerialLog.Core.Tdma;

public sealed class TdmaLoopCriteria
{
    public string CenterAddress { get; set; } = string.Empty;

    public string TargetAddress { get; set; } = string.Empty;

    public string CenterNodeName { get; set; } = "center";

    public string TargetNodeName { get; set; } = "target";

    public int ExpectedDataTxSlot { get; set; } = 0;

    public int ExpectedTargetDataSlot { get; set; } = 3;

    public int ExpectedTargetAckSlot { get; set; } = 4;

    public int ExpectedCenterAckSlot { get; set; } = 7;

    public bool RequireOneFrameLoop { get; set; } = true;

    public string NormalizedCenterAddress => NormalizeAddress(CenterAddress);

    public string NormalizedTargetAddress => NormalizeAddress(TargetAddress);

    public static string NormalizeAddress(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }
}
