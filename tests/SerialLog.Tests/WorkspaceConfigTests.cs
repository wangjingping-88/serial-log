using SerialLog.Core.Commands;
using SerialLog.Core.Configuration;

namespace SerialLog.Tests;

public class WorkspaceConfigTests
{
    [Fact]
    public void Workspace_config_round_trips_command_groups_and_serial_windows()
    {
        var config = new WorkspaceConfig
        {
            LogRootDirectory = @"D:\serial-log-data\logs",
            SelectedPageIndex = 1,
            CommandPanelDock = CommandPanelDock.Right,
            SerialWindows =
            [
                new SerialWindowConfig
                {
                    Id = "port-1",
                    Title = "主控",
                    PortName = "COM1",
                    BaudRate = 115200,
                    AutoSaveEnabled = true
                }
            ],
            CommandGroups =
            [
                new CommandGroupConfig
                {
                    Name = "启动检查",
                    TargetIds = ["port-1"],
                    Commands = ["AT", "AT+GMR"],
                    DelayMilliseconds = 500,
                    LineEnding = LineEnding.CrLf
                }
            ]
        };

        var path = Path.Combine(Path.GetTempPath(), "workspace-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            WorkspaceConfigStore.Save(path, config);

            var loaded = WorkspaceConfigStore.Load(path);

            Assert.Equal(config.LogRootDirectory, loaded.LogRootDirectory);
            Assert.Equal(1, loaded.SelectedPageIndex);
            Assert.Equal(CommandPanelDock.Right, loaded.CommandPanelDock);
            Assert.Equal("主控", loaded.SerialWindows.Single().Title);
            Assert.Equal("启动检查", loaded.CommandGroups.Single().Name);
            Assert.Equal(LineEnding.CrLf, loaded.CommandGroups.Single().LineEnding);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
