namespace SerialLog.Cli;

public sealed class TdmaLoopRunConfig
{
    public string WorkRoot { get; set; } = @"D:\serial-log-data\tdma-loop";

    public string WiotaMeshRoot { get; set; } = @"D:\code\WIoTaMesh";

    public string FirmwarePath { get; set; } = @"D:\code\WIoTaMesh\rtthread.bin";

    public string YmodemToolPath { get; set; } = @"D:\code\WIoTaMesh\tools\mesh_ymodem_tool\mesh_ymodem_tool.exe";

    public int YmodemBaudRate { get; set; } = 115200;

    public string BuildVersionPrefix { get; set; } = "tdma";

    public string VersionHeaderPath { get; set; } = @"D:\code\WIoTaMesh\mesh\tdma\tdma_auto_build_version.h";

    public string VersionQueryCommand { get; set; } = "AT+BUILD?";

    public string VersionResponsePrefix { get; set; } = "+BUILD:";

    public bool ConfirmBuildVersion { get; set; } = true;

    public int VersionConfirmTimeoutSeconds { get; set; } = 10;

    public bool BuildFirmware { get; set; } = true;

    public bool FlashFirmware { get; set; } = true;

    public bool ParallelFlash { get; set; } = true;

    public int MaxIterations { get; set; } = 50;

    public int SummaryInterval { get; set; } = 3;

    public int BootWaitSeconds { get; set; } = 8;

    public int PureSyncSeconds { get; set; } = 30;

    public int LoopTimeoutSeconds { get; set; } = 8;

    public int CommandDelayMilliseconds { get; set; } = 500;

    public string Goal { get; set; } = "unicast_one_frame_loop";

    public string[] AbnormalPatterns { get; set; } = ["LOST"];

    public TdmaLoopNodeConfig[] Nodes { get; set; } = [];

    public string CenterNode { get; set; } = "center";

    public string TargetNode { get; set; } = "R4";

    public string SendCommand { get; set; } = string.Empty;

    public TdmaLoopCommandConfig[] PlanCommands { get; set; } = [];
}

public sealed class TdmaLoopNodeConfig
{
    public string Name { get; set; } = string.Empty;

    public string Address { get; set; } = string.Empty;

    public string? AtPort { get; set; }

    public int AtBaudRate { get; set; } = 115200;

    public string? LogPort { get; set; }

    public int LogBaudRate { get; set; } = 115200;
}

public sealed class TdmaLoopCommandConfig
{
    public string Node { get; set; } = string.Empty;

    public string Command { get; set; } = string.Empty;

    public int? DelayMilliseconds { get; set; }
}
