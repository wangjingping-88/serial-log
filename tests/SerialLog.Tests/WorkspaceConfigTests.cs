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
            WorkspaceMode = WorkspaceMode.Host,
            LocalPcId = "pc-center",
            LocalPcName = "Center PC",
            LocalPcColor = "#0B75B7",
            HostAddress = "192.168.1.10",
            HostPort = 58730,
            CommandPanelDock = CommandPanelDock.Right,
            IsCommandPanelFloating = true,
            ExpandedWindowIds = ["port-1"],
            SingleCommandLoopIntervalMilliseconds = 1200,
            SingleCommandLoopCount = 5,
            SerialWindows =
            [
                new SerialWindowConfig
                {
                    Id = "port-1",
                    Title = "主控",
                    PortName = "COM1",
                    BaudRate = 115200,
                    OwnerPcId = "pc-center",
                    OwnerPcName = "Center PC",
                    OwnerPcColor = "#0B75B7",
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
                    LoopIntervalMilliseconds = 1500,
                    LoopCount = 3,
                    LineEnding = LineEnding.CrLf
                }
            ],
            AtCommandSets =
            [
                new AtCommandSetConfig
                {
                    Name = "网关",
                    Commands = ["AT+GATEWAY"]
                },
                new AtCommandSetConfig
                {
                    Name = "Mesh",
                    Commands = ["AT+MESH"]
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
            Assert.Equal(WorkspaceMode.Host, loaded.WorkspaceMode);
            Assert.Equal("pc-center", loaded.LocalPcId);
            Assert.Equal("Center PC", loaded.LocalPcName);
            Assert.Equal("#0B75B7", loaded.LocalPcColor);
            Assert.Equal("192.168.1.10", loaded.HostAddress);
            Assert.Equal(58730, loaded.HostPort);
            Assert.Equal(CommandPanelDock.Right, loaded.CommandPanelDock);
            Assert.True(loaded.IsCommandPanelFloating);
            Assert.Equal(["port-1"], loaded.ExpandedWindowIds);
            Assert.Equal(1200, loaded.SingleCommandLoopIntervalMilliseconds);
            Assert.Equal(5, loaded.SingleCommandLoopCount);
            Assert.Equal("pc-center", loaded.SerialWindows.Single().OwnerPcId);
            Assert.Equal("Center PC", loaded.SerialWindows.Single().OwnerPcName);
            Assert.Equal("#0B75B7", loaded.SerialWindows.Single().OwnerPcColor);
            Assert.Equal("主控", loaded.SerialWindows.Single().Title);
            Assert.Equal("启动检查", loaded.CommandGroups.Single().Name);
            Assert.Equal(1500, loaded.CommandGroups.Single().LoopIntervalMilliseconds);
            Assert.Equal(3, loaded.CommandGroups.Single().LoopCount);
            Assert.Equal(LineEnding.CrLf, loaded.CommandGroups.Single().LineEnding);
            Assert.Equal(["网关", "Mesh"], loaded.AtCommandSets.Select(set => set.Name));
            Assert.Equal(["AT+GATEWAY"], loaded.AtCommandSets[0].Commands);
            Assert.Equal(["AT+MESH"], loaded.AtCommandSets[1].Commands);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
