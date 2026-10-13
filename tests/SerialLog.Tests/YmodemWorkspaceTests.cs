using System.Collections.ObjectModel;
using System.Reflection;
using SerialLog.App.ViewModels;
using SerialLog.Core.Collaboration;
using SerialLog.Core.Configuration;
using SerialLog.Core.Firmware;
using SerialLog.Core.Serial;

namespace SerialLog.Tests;

public class YmodemWorkspaceTests
{
    [Fact]
    public async Task Busy_target_blocks_sends_settings_transfers_and_auto_reconnect()
    {
        using var window = new SerialWindowViewModel("local", "测试", refreshPortsOnCreate: false) { PortName = "COM7", BaudRate = 460800 };
        window.SetFirmwareBusy(true);
        Assert.False(window.CanSendCommands); Assert.False(window.CanChangeSerialSettings);
        Assert.False(window.ToggleConnectionCommand.CanExecute(null));
        window.BaudRate = 115200; window.PortName = "COM8";
        window.Connect(); window.TryAutoReconnect(); window.Disconnect();
        Assert.Equal(460800, window.BaudRate); Assert.Equal("COM7", window.PortName);
        Assert.False(window.IsConnectionPending);
        await Assert.ThrowsAsync<InvalidOperationException>(() => window.SendAsync("hello", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => window.ReleaseForTransferAsync("B"));
        window.SetFirmwareBusy(false);
        Assert.True(window.CanSendCommands); Assert.True(window.CanChangeSerialSettings);
        Assert.False(window.WantsConnection);
    }

    [Fact]
    public async Task Uses_left_target_selection_and_validation_has_no_side_effects()
    {
        using var local = new SerialWindowViewModel("a", "A", refreshPortsOnCreate: false);
        using var remote = SerialWindowViewModel.CreateRemote(new CollaborationClientSnapshot("pc", "电脑", "#123456", []),
            new CollaborationWindowSnapshot("r", "远程", "COM9", 460800, true, 0), null);
        var windows = new ObservableCollection<SerialWindowViewModel> { local, remote };
        using var commands = new CommandPanelViewModel(windows, _ => { });
        using var vm = new YmodemViewModel(windows, commands, _ => { });
        local.IsSelectedForSend = false;
        await vm.StartAsync(); Assert.Contains("选择", vm.Status);
        local.IsSelectedForSend = true;
        await vm.StartAsync(); Assert.Contains(".bin", vm.Status);
        Assert.False(vm.IsRunning); Assert.False(local.IsFirmwareBusy);
        windows.Remove(local);
        await vm.StartAsync(); Assert.Contains("本地", vm.Status);
    }

    [Fact]
    public async Task Actual_port_mismatch_is_rejected_before_any_upgrade_command()
    {
        using var window = new SerialWindowViewModel("A", "A", refreshPortsOnCreate: false) { PortName = "COM7", BaudRate = 460800 };
        window.SetFirmwareBusy(true);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => window.AcquireFirmwareTransportAsync(default));
        Assert.Contains("实际连接不一致", exception.Message);
        window.SetFirmwareBusy(false);
        Assert.False(window.IsConnectionPending);
    }

    [Fact]
    public void Loop_detection_only_matches_running_tasks_involving_the_target()
    {
        using var commands = new CommandPanelViewModel([], _ => { });
        typeof(CommandPanelViewModel).GetField("_singleLoopTargets", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(commands, new[] { "A" });
        Assert.False(commands.HasRunningLoopForTarget("A"));
        typeof(CommandPanelViewModel).GetField("_isSingleCommandLoopRunning", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(commands, true);
        Assert.True(commands.HasRunningLoopForTarget("A"));
        Assert.False(commands.HasRunningLoopForTarget("B"));
        commands.SelectedCommandPanelTabIndex = 2;
        Assert.Equal(2, commands.SelectedCommandPanelTabIndex);
        Assert.False(commands.IsSingleCommandTabSelected); Assert.False(commands.IsCommandGroupTabSelected);
    }

    [Fact]
    public void Copy_deep_clones_rules_and_local_target_selection_without_starting_or_sharing()
    {
        var source = new TestWorkspaceConfig { Configuration = new WorkspaceConfig
        {
            SerialWindows = [new() { Id = "A", IsShared = true, IsSelectedForSend = true }],
            YmodemMode = YmodemMode.Standard, YmodemFilePath = "firmware.bin"
        } };
        var copy = WorkspaceCatalogStore.Clone(source, "复制");
        Assert.NotEqual("A", Assert.Single(copy.Configuration.SerialWindows).Id);
        Assert.True(copy.Configuration.SerialWindows[0].IsSelectedForSend);
        copy.Configuration.YmodemProfiles[0].Name = "修改副本";
        Assert.Equal("UCCHIP-小网关", source.Configuration.YmodemProfiles[0].Name);
        Assert.False(copy.Configuration.SerialWindows[0].IsShared);
        Assert.Equal(YmodemMode.Standard, copy.Configuration.YmodemMode);
        Assert.Equal("firmware.bin", copy.Configuration.YmodemFilePath);
    }

    [Fact]
    public void Starting_group_loop_during_firmware_transfer_does_not_schedule_delayed_sends()
    {
        using var window = new SerialWindowViewModel("A", "A", refreshPortsOnCreate: false);
        using var commands = new CommandPanelViewModel(new ObservableCollection<SerialWindowViewModel> { window }, _ => { });
        var group = new CommandGroupEditorViewModel(new CommandGroupConfig { Commands = ["test"], TargetIds = ["A"] });
        commands.CommandGroups.Add(group); commands.SelectedCommandGroup = group;
        commands.SyncCommandGroupTargets();
        window.SetFirmwareBusy(true);
        commands.ToggleCommandGroupLoopCommand.Execute(null);
        Assert.False(commands.IsCommandGroupLoopRunning);
        Assert.Contains("烧录", commands.StatusText);
        window.SetFirmwareBusy(false);
    }

    [Fact]
    public void Workspace_configuration_round_trips_and_hiding_preserves_selection()
    {
        var root = Path.Combine(Path.GetTempPath(), "ymodem-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "workspace.json");
            using (var vm = new MainViewModel(path, false, configuration: new WorkspaceConfig { SerialWindows = [new() { Id = "A", Title = "串口A" }] }))
            {
                vm.SerialWindows[0].IsSelectedForSend = true; vm.Ymodem.Mode = YmodemMode.Standard; vm.Ymodem.FilePath = "test.bin";
                vm.SelectedCommandPanelTabIndex = 2;
                vm.FloatCommandPanelCommand.Execute(null); vm.ToggleCommandPanelVisibilityCommand.Execute(null);
                Assert.True(vm.IsCommandPanelHidden); Assert.True(vm.SerialWindows[0].IsSelectedForSend);
                vm.SaveWorkspace();
            }
            using var restored = new MainViewModel(path, false);
            Assert.Equal("test.bin", restored.Ymodem.FilePath);
            Assert.Equal(YmodemMode.Standard, restored.Ymodem.Mode);
            Assert.True(restored.SerialWindows[0].IsSelectedForSend);
            Assert.Equal("UCCHIP-小网关", restored.Ymodem.SelectedProfile?.Name);
            Assert.Equal(2, restored.SelectedCommandPanelTabIndex);
            Assert.False(restored.Ymodem.IsRunning);
            Assert.True(restored.IsCommandPanelHidden);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Background_firmware_job_survives_workspace_switch_and_other_workspace_stop_then_cancels_cleanly()
    {
        var root = Path.Combine(Path.GetTempPath(), "ymodem-background-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var firmware = Path.Combine(root, "test.bin"); File.WriteAllBytes(firmware, new byte[100]);
            using var manager = new TestWorkspaceManager(Path.Combine(root, "workspace.json"), false);
            var a = manager.Active; a.ViewModel.AddWindowCommand.Execute(null);
            var window = Assert.Single(a.ViewModel.SerialWindows);
            var session = (SerialPortSession)typeof(SerialWindowViewModel).GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            typeof(SerialPortSession).GetField("_isConnected", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, 1);
            var transport = new WaitingTransport();
            a.ViewModel.Ymodem.TransportFactory = (_, _) => Task.FromResult<IBinarySerialTransport>(transport);
            window.IsSelectedForSend = true; a.ViewModel.Ymodem.Mode = YmodemMode.Standard; a.ViewModel.Ymodem.FilePath = firmware;
            var burn = a.ViewModel.Ymodem.StartAsync();
            await transport.Reading.Task.WaitAsync(TimeSpan.FromSeconds(2));
            a.ViewModel.ToggleCommandPanelVisibilityCommand.Execute(null);
            a.ViewModel.DisconnectAllCommand.Execute(null);
            Assert.Contains("跳过", a.ViewModel.StatusText);
            Assert.True(window.IsConnected);
            manager.Create("B", false);
            await manager.Active.ViewModel.StopWorkspaceAsync();
            Assert.True(a.ViewModel.Ymodem.IsRunning); Assert.True(window.IsFirmwareBusy); Assert.False(burn.IsCompleted);
            manager.Active = a;
            await a.ViewModel.StopWorkspaceAsync();
            await burn.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(window.IsFirmwareBusy); Assert.False(window.IsConnected);
            Assert.True(transport.Disposed); Assert.True(transport.CancelSent);
            Assert.Contains("取消", a.ViewModel.Ymodem.Status);
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class WaitingTransport : IBinarySerialTransport
    {
        public TaskCompletionSource<bool> Reading { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed, CancelSent;
        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        { Reading.TrySetResult(true); await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
        public ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        { CancelSent |= buffer.Span[0] == 0x18; return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
}
