using SerialLog.Core.Commands;

namespace SerialLog.Core.Configuration;

public sealed class WorkspaceConfig
{
    public string LogRootDirectory { get; set; } = @"D:\serial-log-data\logs";

    public int SelectedPageIndex { get; set; }

    public CommandPanelDock CommandPanelDock { get; set; } = CommandPanelDock.Bottom;

    public List<SerialWindowConfig> SerialWindows { get; set; } = [];

    public List<string> CommandHistory { get; set; } = [];

    public string? AtCommandFilePath { get; set; }

    public int SingleCommandLoopIntervalMilliseconds { get; set; } = 1000;

    public int SingleCommandLoopCount { get; set; }

    public List<CommandGroupConfig> CommandGroups { get; set; } = [];
}

public sealed class SerialWindowConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Title { get; set; } = "串口";

    public string? PortName { get; set; }

    public int BaudRate { get; set; } = 115200;

    public bool AutoSaveEnabled { get; set; }
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
    public string Name { get; set; } = "命令组";

    public List<string> TargetIds { get; set; } = [];

    public List<string> Commands { get; set; } = [];

    public int DelayMilliseconds { get; set; } = 500;

    public int LoopIntervalMilliseconds { get; set; } = 1000;

    public int LoopCount { get; set; }

    public LineEnding LineEnding { get; set; } = LineEnding.CrLf;
}
