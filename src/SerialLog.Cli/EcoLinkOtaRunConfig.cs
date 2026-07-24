namespace SerialLog.Cli;

public sealed class EcoLinkOtaRunConfig
{
    public string WorkRoot { get; set; } = @"D:\serial-log-data\ecolink-ota";

    public string EcoLinkRoot { get; set; } = @"D:\code\EcoLink";

    public bool PreflightOnly { get; set; } = true;

    public bool PreparePackages { get; set; } = true;

    public bool FlashBaseline { get; set; }

    public bool PublishUpgradeCommands { get; set; }

    public int SerialOpenTimeoutSeconds { get; set; } = 10;

    public uint ExtenderDeviceId { get; set; } = 119472;

    public bool DiscoverExtenderDeviceIdFromCmd201 { get; set; } = true;

    public int DeviceDiscoveryTimeoutSeconds { get; set; } = 10;

    public EcoLinkOtaMqttConfig Mqtt { get; set; } = new();

    public EcoLinkOtaHttpConfig Http { get; set; } = new();

    public EcoLinkOtaToolConfig Tools { get; set; } = new();

    public EcoLinkOtaDeviceConfig[] Devices { get; set; } = [];

    public EcoLinkOtaTargetConfig[] Targets { get; set; } = [];
}

public sealed class EcoLinkOtaMqttConfig
{
    public string Host { get; set; } = "117.172.29.2";

    public int Port { get; set; } = 36106;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public int Qos { get; set; }

    public string ClientIdPrefix { get; set; } = "ecolink-ota-test";

    public string UpTopic { get; set; } = "ucchip/up/sgw/704027/+";

    public string DownTopic { get; set; } = "ucchip/down/sgw/704027/1";

    public int ConnectTimeoutSeconds { get; set; } = 10;

    public int KeepAliveSeconds { get; set; } = 30;
}

public sealed class EcoLinkOtaHttpConfig
{
    public string BindAddress { get; set; } = "0.0.0.0";

    public string AdvertiseAddress { get; set; } = "192.168.1.81";

    public int Port { get; set; } = 36109;

    public string BasePath { get; set; } = "download";

    public string PackageRoot { get; set; }
        = @"D:\serial-log-data\ota\packages";
}

public sealed class EcoLinkOtaToolConfig
{
    public string BsdiffPath { get; set; }
        = @"D:\code\EcoLink\eco_gateway\docs\工具\iote_ota_upgrade\src\bsdiff_cmd.exe";

    public bool BsdiffCompatibilityConfirmed { get; set; }

    public string UcProgrammerPath { get; set; }
        = @"D:\code\EcoLink\tools\UcProgrammer\UcProgrammer.exe";

    public int UcProgrammerDeviceIndex { get; set; }

    public string UcProgrammerAddress { get; set; } = "0";

    public string EcoYmodemPath { get; set; }
        = @"D:\code\EcoLink\tools\eco_ymodem\eco_ymodem.exe";

    public string GatewayYmodemPath { get; set; }
        = @"D:\code\EcoLink\tools\eco_ymodem\eco_ymodem.exe";
}

public sealed class EcoLinkOtaDeviceConfig
{
    public bool Enabled { get; set; } = true;

    public bool MonitorLogs { get; set; } = true;

    public bool FlashBaseline { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    public string PortName { get; set; } = string.Empty;

    public int BaudRate { get; set; } = 460800;

    public string FlashMethod { get; set; } = "none";

    public string BaselineFirmwarePath { get; set; } = string.Empty;

    public ushort NodeId { get; set; }
}

public sealed class EcoLinkOtaTargetConfig
{
    public bool Enabled { get; set; } = true;

    public string Name { get; set; } = string.Empty;

    public string DevType { get; set; } = string.Empty;

    public uint SessionId { get; set; }

    public int UpgradeType { get; set; } = 1;

    public int Range { get; set; } = 1;

    public uint[] DeviceIds { get; set; } = [];

    public ushort[] NodeIds { get; set; } = [];

    public string OldFirmwarePath { get; set; } = string.Empty;

    public string NewFirmwarePath { get; set; } = string.Empty;

    public string OldVersion { get; set; } = string.Empty;

    public string NewVersion { get; set; } = string.Empty;

    public string PackageFileName { get; set; } = string.Empty;

    public bool IncludeUbootPartition { get; set; }

    public int MaxPackageBytes { get; set; } = 52 * 1024;

    public string[] VerifyDevices { get; set; } = [];

    public bool VerifyGatewayVersionReports { get; set; }

    public int TimeoutSeconds { get; set; } = 1800;

    public string[] SuccessMqttPrompts { get; set; } =
    [
        "upgrade process has end!"
    ];

    public string[] FailurePatterns { get; set; } =
    [
        "reject duplicate upgrade request",
        "ver not match",
        "wiota offline",
        "auth list empty",
        "unknown device_type",
        "ota file download or md5 failed",
        "ota upgrade error",
        "Hard fault",
        "assert failed"
    ];
}
