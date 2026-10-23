using System.Text.Json.Nodes;
using SerialLog.Core.Configuration;

namespace SerialLog.Tests;

public class WorkspacePackageTests
{
    internal static TestWorkspaceConfig Source()
    {
        var window = new SerialWindowConfig { Title = "AP", PortName = "COM7", BaudRate = 460800, IsShared = true, OwnerPcId = "foreign" };
        var list = new CommandGroupListConfig { Name = "AP命令" };
        var group = new CommandGroupConfig { Name = "发送", Commands = ["AT+SEND\\r\\n中文"], TargetIds = [window.Id], ListIds = [list.Id] };
        return new TestWorkspaceConfig { Name = "联合测试", Configuration = new()
        {
            LocalPcId = "foreign", LocalPcName = "发送者", HostAddress = "10.1.2.3", WorkspaceMode = WorkspaceMode.Host,
            SerialWindows = [window], CommandGroups = [group], CommandGroupLists = [list],
            SelectedCommandGroupListId = list.Id, SelectedCommandGroupId = group.Id,
            CommandGroupQueue = [new() { GroupId = group.Id }], ExpandedWindowIds = [window.Id],
            ThemeColor = "#22AA55", LogRootDirectory = "Z:\\foreign-logs", AtCommandFilePath = "Z:\\commands.txt", YmodemFilePath = "Z:\\firmware.bin"
        } };
    }

    [Fact]
    public void Snapshot_preserves_test_settings_and_remaps_relationships_without_machine_state()
    {
        var source = Source(); var json = WorkspacePackageCodec.Export(source);
        var copy = WorkspacePackageCodec.PrepareImport(json, "导入副本", "local", "接收者", "D:\\local-logs");
        var c = copy.Configuration;
        Assert.NotEqual(source.Id, copy.Id); Assert.Equal("导入副本", copy.Name);
        Assert.Equal("local", c.LocalPcId); Assert.Equal("接收者", c.LocalPcName); Assert.Equal(WorkspaceMode.Local, c.WorkspaceMode);
        Assert.Null(c.SerialWindows[0].PortName); Assert.False(c.SerialWindows[0].IsShared); Assert.Empty(c.Subscriptions);
        Assert.Equal(460800, c.SerialWindows[0].BaudRate); Assert.Equal("#22AA55", c.ThemeColor);
        Assert.Equal(c.SerialWindows[0].Id, Assert.Single(c.CommandGroups[0].TargetIds));
        Assert.Equal(c.CommandGroupLists[0].Id, Assert.Single(c.CommandGroups[0].ListIds));
        Assert.Equal(c.CommandGroups[0].Id, c.CommandGroupQueue[0].GroupId);
        Assert.Equal(c.SerialWindows[0].Id, Assert.Single(c.ExpandedWindowIds));
        Assert.Equal("D:\\local-logs", c.LogRootDirectory); Assert.Null(c.AtCommandFilePath); Assert.Equal("", c.YmodemFilePath);
        Assert.Equal("COM7", source.Configuration.SerialWindows[0].PortName); Assert.True(source.Configuration.SerialWindows[0].IsShared);
        Assert.NotSame(source.Configuration.YmodemProfiles[0], c.YmodemProfiles[0]);
    }

    [Fact]
    public void Invalid_snapshot_rejected_without_creating_runtime()
    {
        Assert.Throws<InvalidDataException>(() => WorkspacePackageCodec.Parse("{}"));
        var bad = JsonNode.Parse(WorkspacePackageCodec.Export(Source()))!;
        bad["workspace"]!["configuration"]!["serialWindows"]![0]!["baudRate"] = -1;
        Assert.Throws<InvalidDataException>(() => WorkspacePackageCodec.Parse(bad.ToJsonString()));
        Assert.Throws<InvalidDataException>(() => WorkspacePackageCodec.Parse(new string('x', WorkspacePackageCodec.MaxBytes + 1)));
    }

    [Fact]
    public void Import_creates_new_saved_workspace_without_switching_active_or_starting_connections()
    {
        Exception? failure = null;
        var directory = Path.Combine(Path.GetTempPath(), "serial-log-package-" + Guid.NewGuid().ToString("N"));
        var thread = new Thread(() =>
        {
            try
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "workspace.json");
                using var manager = new SerialLog.App.ViewModels.TestWorkspaceManager(path, false);
                var active = manager.Active;
                var imported = manager.ImportSnapshot(WorkspacePackageCodec.Export(Source()));
                Assert.Same(active, manager.Active); Assert.Equal(2, manager.Workspaces.Count);
                Assert.False(imported.ViewModel.HasRunningTests);
                Assert.All(imported.ViewModel.SerialWindows, window => { Assert.False(window.IsConnected); Assert.False(window.WantsConnection); });
                var second = manager.ImportSnapshot(WorkspacePackageCodec.Export(Source()));
                Assert.Equal("联合测试 (2)", second.Name);
                var saved = WorkspaceCatalogStore.Load(path);
                Assert.Equal(active.Id, saved.CurrentWorkspaceId); Assert.Equal(3, saved.Workspaces.Count);
                Assert.Throws<InvalidDataException>(() => manager.ImportSnapshot("{}")); Assert.Equal(3, manager.Workspaces.Count);
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(20)));
        // 只清理由本测试创建的唯一临时目录。
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
