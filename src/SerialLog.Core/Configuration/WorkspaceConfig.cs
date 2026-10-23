using SerialLog.Core.Commands;

namespace SerialLog.Core.Configuration;

public sealed class WorkspaceConfig
{
    public const int MinLogFileSizeMegabytes = 50;

    public const int MaxAllowedLogFileSizeMegabytes = 10_240;

    public const int DefaultMaxLogFileSizeMegabytes = 100;

    public const int MaxReceiveSilenceReconnectSeconds = 86_400;

    public string LogRootDirectory { get; set; } = ApplicationDataPaths.LogDirectory;

    public int MaxLogFileSizeMegabytes { get; set; } = DefaultMaxLogFileSizeMegabytes;

    public int ReceiveSilenceReconnectSeconds { get; set; }

    public int SelectedPageIndex { get; set; }

    public int PageCount { get; set; }

    public WorkspaceMode WorkspaceMode { get; set; } = WorkspaceMode.Local;

    public string LocalPcId { get; set; } = string.Empty;

    public string LocalPcName { get; set; } = string.Empty;

    public string LocalPcColor { get; set; } = "#0B75B7";

    public string ThemeColor { get; set; } = string.Empty;

    public string HostAddress { get; set; } = "127.0.0.1";

    public int HostPort { get; set; } = 58730;

    public CommandPanelDock CommandPanelDock { get; set; } = CommandPanelDock.Bottom;

    public bool IsCommandPanelFloating { get; set; }

    public bool IsCommandPanelHidden { get; set; }

    public List<string> ExpandedWindowIds { get; set; } = [];

    public List<SerialWindowConfig> SerialWindows { get; set; } = [];

    public List<string> CommandHistory { get; set; } = [];

    public string? AtCommandFilePath { get; set; }

    public List<AtCommandSetConfig> AtCommandSets { get; set; } = [];

    public string? SelectedAtCommandSetName { get; set; }

    public int SingleCommandLoopIntervalMilliseconds { get; set; } = 1000;

    public int SingleCommandLoopCount { get; set; }

    public bool SingleCommandInfiniteLoop { get; set; }

    public List<CommandGroupConfig> CommandGroups { get; set; } = [];
    public List<CommandGroupListConfig> CommandGroupLists { get; set; } = [];
    public string SelectedCommandGroupListId { get; set; } = string.Empty;

    public List<ShortcutBindingConfig> ShortcutBindings { get; set; } = [];
    public List<RemoteWindowSubscription> Subscriptions { get; set; } = [];
    public string CommandText { get; set; } = string.Empty;
    public LineEnding SelectedLineEnding { get; set; } = LineEnding.CrLf;
    public string? SelectedCommandGroupName { get; set; }
    public string? SelectedCommandGroupId { get; set; }
    public List<CommandGroupQueueItemConfig> CommandGroupQueue { get; set; } = [];
    public bool ShowSentCommands { get; set; } = true;
    public bool InterpretCommandEscapes { get; set; } = true;
    public int SelectedCommandPanelTabIndex { get; set; }
    public string YmodemFilePath { get; set; } = string.Empty;
    public Firmware.YmodemMode YmodemMode { get; set; } = Firmware.YmodemMode.Standard;
    public List<Firmware.YmodemRuleProfile> YmodemProfiles { get; set; } = [Firmware.YmodemRuleProfiles.CreateUcchipExample()];
    public string? YmodemSelectedProfileId { get; set; }
    public WorkspaceConfig() => YmodemSelectedProfileId = YmodemProfiles[0].Id;
}

public sealed class RemoteWindowSubscription
{
    public Collaboration.CollaborationClientSnapshot Source { get; set; } = new("", "", "", []);
    public Collaboration.CollaborationWindowSnapshot Window { get; set; } = new("", "", null, 115200, false, 0);
    public bool AutoSaveEnabled { get; set; } = true;
    public bool IsSelectedForSend { get; set; }
    public int PageIndex { get; set; } = -1;
    public int PagePosition { get; set; } = -1;
    [System.Text.Json.Serialization.JsonIgnore]
    public string Id => Collaboration.CollaborationIdentity.Window(Source.PcId, Source.WorkspaceId, Window.Id);
}

public sealed class ShortcutBindingConfig
{
    public string ActionId { get; set; } = string.Empty;

    public string Gesture { get; set; } = string.Empty;
}

public sealed class SerialWindowConfig
{
    public bool IsShared { get; set; }
    public bool IsSelectedForSend { get; set; }
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = "串口";

    public string? PortName { get; set; }

    public int BaudRate { get; set; } = 115200;

    public int PageIndex { get; set; } = -1;

    public int PagePosition { get; set; } = -1;

    public string OwnerPcId { get; set; } = string.Empty;

    public string OwnerPcName { get; set; } = string.Empty;

    public string OwnerPcColor { get; set; } = string.Empty;

    public bool AutoSaveEnabled { get; set; }
}

public enum WorkspaceMode
{
    Local,
    Host,
    Client
}

public enum CommandPanelDock
{
    Bottom,
    Top,
    Left,
    Right
}

public sealed class CommandGroupConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public List<string> ListIds { get; set; } = [];
    public bool InterpretEscapes { get; set; } = true;
    public string Name { get; set; } = "命令组";

    public List<string> TargetIds { get; set; } = [];

    public List<string> Commands { get; set; } = [];

    public int DelayMilliseconds { get; set; } = 500;

    public int LoopIntervalMilliseconds { get; set; } = 1000;

    public int LoopCount { get; set; }

    public bool IsInfiniteLoop { get; set; }

    public LineEnding LineEnding { get; set; } = LineEnding.CrLf;
}

public sealed class CommandGroupListConfig
{
    public const string DefaultId = "00000000000000000000000000000001";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "命令组列表";
    public static CommandGroupListConfig CreateDefault() => new() { Id = DefaultId, Name = "默认列表" };
}

public sealed class CommandGroupQueueItemConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string GroupId { get; set; } = string.Empty;
}

public sealed class AtCommandSetConfig
{
    public string Name { get; set; } = "命令集";

    public List<string> Commands { get; set; } = [];
}
