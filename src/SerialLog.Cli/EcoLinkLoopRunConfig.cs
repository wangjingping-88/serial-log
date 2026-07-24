namespace SerialLog.Cli;

public sealed class EcoLinkLoopRunConfig
{
    public string WorkRoot { get; set; } = @"D:\serial-log-data\ecolink-loop";

    public string EcoLinkRoot { get; set; } = @"D:\code\EcoLink";

    public string YmodemToolPath { get; set; }
        = @"D:\code\WIoTaMesh\tools\mesh_ymodem_tool\mesh_ymodem_tool.exe";

    public string VersionHeaderPath { get; set; }
        = @"D:\code\EcoLink\tools\ecolink_auto_build_version.h";

    public string BuildVersionPrefix { get; set; } = "ecolink";

    public bool BuildFirmware { get; set; } = true;

    public bool FlashFirmware { get; set; } = true;

    public bool ParallelNodeFlash { get; set; }

    public bool ConfirmBuildVersion { get; set; } = true;

    public int MaxIterations { get; set; } = 10;

    public int SummaryInterval { get; set; } = 1;

    public int BootWaitSeconds { get; set; } = 1;

    public int VersionConfirmTimeoutSeconds { get; set; } = 15;

    public int TestTimeoutSeconds { get; set; } = 300;

    public int SerialOpenTimeoutSeconds { get; set; } = 10;

    public int FlashRetryCount { get; set; } = 1;

    public string ExtenderNodeName { get; set; } = "ex_async";

    public int ExpectedNodeCount { get; set; } = 5;

    public int ExpectedRoundsPerPhase { get; set; } = 100;

    public string[] AbnormalPatterns { get; set; } =
    [
        "Hard fault",
        "assert failed",
        "node_frame test failed"
    ];

    public EcoLinkBuildTargetConfig[] BuildTargets { get; set; } = [];

    public EcoLinkDeviceConfig[] Devices { get; set; } = [];
}

public sealed class EcoLinkBuildTargetConfig
{
    public string Name { get; set; } = string.Empty;

    public string WorkingDirectory { get; set; } = string.Empty;

    public string Executable { get; set; } = "scons";

    public string Arguments { get; set; } = string.Empty;

    public string FirmwarePath { get; set; } = string.Empty;
}

public sealed class EcoLinkDeviceConfig
{
    public bool Enabled { get; set; } = true;

    public bool FlashFirmware { get; set; } = true;

    public bool ConfirmBuildVersion { get; set; } = true;

    public string Name { get; set; } = string.Empty;

    public string Role { get; set; } = "node";

    public string Firmware { get; set; } = string.Empty;

    public string PortName { get; set; } = string.Empty;

    public int AtBaudRate { get; set; } = 460800;

    public int LogBaudRate { get; set; } = 460800;

    public int YmodemBaudRate { get; set; } = 460800;
}
