using SerialLog.App.ViewModels;
using SerialLog.Core.Configuration;

namespace SerialLog.Tests;

public class MainViewModelTests
{
    [Fact]
    public void Remove_window_deletes_added_window_and_clamps_page()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "serial-log-workspace-" + Guid.NewGuid().ToString("N") + ".json");
        WorkspaceConfigStore.Save(workspacePath, new WorkspaceConfig());

        using var viewModel = new MainViewModel(workspacePath, startReconnectTimer: false);
        var addedWindow = viewModel.SerialWindows.Last();

        viewModel.RemoveWindowCommand.Execute(addedWindow);

        Assert.Equal(5, viewModel.SerialWindows.Count);
        Assert.Equal(0, viewModel.CurrentPageIndex);
        Assert.Equal("1 / 1", viewModel.PageLabel);
        Assert.DoesNotContain(viewModel.CurrentPageWindows, slot => ReferenceEquals(slot.Window, addedWindow));
        Assert.Contains(viewModel.CurrentPageWindows, slot => slot.IsAddSlot);
    }

    [Fact]
    public void Page_shows_center_add_slot_when_current_page_has_empty_space()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "serial-log-workspace-" + Guid.NewGuid().ToString("N") + ".json");
        WorkspaceConfigStore.Save(workspacePath, new WorkspaceConfig
        {
            SerialWindows =
            [
                new SerialWindowConfig { Id = "center", Title = "中心" },
                new SerialWindowConfig { Id = "r1", Title = "R1" }
            ]
        });

        using var viewModel = new MainViewModel(workspacePath, startReconnectTimer: false);

        Assert.Equal(3, viewModel.CurrentPageWindows.Count);
        Assert.Equal(2, viewModel.CurrentPageWindows.Count(slot => !slot.IsAddSlot));
        Assert.True(viewModel.CurrentPageWindows.Last().IsAddSlot);
    }

    [Fact]
    public void Connect_all_attempts_only_windows_with_configured_ports()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "serial-log-workspace-" + Guid.NewGuid().ToString("N") + ".json");
        WorkspaceConfigStore.Save(workspacePath, new WorkspaceConfig
        {
            SerialWindows =
            [
                new SerialWindowConfig { Id = "center", Title = "中心", PortName = "COM_DOES_NOT_EXIST", BaudRate = 460800 },
                new SerialWindowConfig { Id = "r1", Title = "R1", PortName = "", BaudRate = 460800 }
            ]
        });

        using var viewModel = new MainViewModel(workspacePath, startReconnectTimer: false);

        viewModel.ConnectAllCommand.Execute(null);

        Assert.Contains("已尝试 1", viewModel.StatusText);
    }

    [Fact]
    public void Refresh_ports_keeps_the_user_selected_port_even_when_windows_does_not_report_it()
    {
        var window = new SerialWindowViewModel("center", "中心")
        {
            PortName = "COM987654"
        };

        window.RefreshPorts();

        Assert.Equal("COM987654", window.PortName);
        Assert.Contains("COM987654", window.AvailablePorts);
    }

    [Fact]
    public void Serial_window_toggle_connection_uses_connection_action_text()
    {
        var window = new SerialWindowViewModel("center", "中心");

        Assert.Equal("连接", window.ConnectionActionText);

        window.ToggleConnectionCommand.Execute(null);

        Assert.Equal("请选择端口", window.StatusText);
        Assert.Equal("连接", window.ConnectionActionText);
    }

    [Fact]
    public void Serial_window_connection_status_brush_tracks_status_text()
    {
        var window = new SerialWindowViewModel("center", "中心");

        Assert.Equal("#DC2626", window.ConnectionIndicatorBrush);

        window.StatusText = "端口 COM13 被占用";
        Assert.Equal("#F59E0B", window.ConnectionIndicatorBrush);

        window.StatusText = "已连接";
        Assert.Equal("#16A34A", window.ConnectionIndicatorBrush);
    }

    [Fact]
    public void Disconnect_all_command_is_available_and_updates_status()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "serial-log-workspace-" + Guid.NewGuid().ToString("N") + ".json");
        WorkspaceConfigStore.Save(workspacePath, new WorkspaceConfig
        {
            SerialWindows =
            [
                new SerialWindowConfig { Id = "center", Title = "中心" },
                new SerialWindowConfig { Id = "r1", Title = "R1" }
            ]
        });

        using var viewModel = new MainViewModel(workspacePath, startReconnectTimer: false);

        viewModel.DisconnectAllCommand.Execute(null);

        Assert.Contains("断开全部完成", viewModel.StatusText);
    }

    [Fact]
    public void Selected_imported_at_command_fills_group_editor_before_adding()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "serial-log-workspace-" + Guid.NewGuid().ToString("N") + ".json");
        WorkspaceConfigStore.Save(workspacePath, new WorkspaceConfig());

        using var viewModel = new MainViewModel(workspacePath, startReconnectTimer: false);
        viewModel.AddCommandGroupCommand.Execute(null);
        viewModel.ImportedAtCommands.Add("AT+SEND=<addr[0-65535]>,<len[1-300]>,<data>");
        viewModel.SelectedAtCommand = viewModel.ImportedAtCommands.Single();

        viewModel.AddAtCommandToGroupCommand.Execute(null);

        Assert.NotNull(viewModel.SelectedCommandGroup);
        Assert.Equal("AT+SEND=<addr[0-65535]>,<len[1-300]>,<data>", viewModel.SelectedCommandGroup.NewCommand);
        Assert.Empty(viewModel.SelectedCommandGroup.Commands);

        viewModel.SelectedCommandGroup.NewCommand = "AT+SEND=981,4,11223344";
        viewModel.AddCommandToGroupCommand.Execute(null);

        Assert.Equal(["AT+SEND=981,4,11223344"], viewModel.SelectedCommandGroup.Commands);
    }

    [Fact]
    public void Commands_in_group_can_be_reordered()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "serial-log-workspace-" + Guid.NewGuid().ToString("N") + ".json");
        WorkspaceConfigStore.Save(workspacePath, new WorkspaceConfig());

        using var viewModel = new MainViewModel(workspacePath, startReconnectTimer: false);
        viewModel.AddCommandGroupCommand.Execute(null);

        Assert.NotNull(viewModel.SelectedCommandGroup);
        viewModel.SelectedCommandGroup.Commands.Add("AT+ONE");
        viewModel.SelectedCommandGroup.Commands.Add("AT+TWO");
        viewModel.SelectedCommandGroup.Commands.Add("AT+THREE");

        viewModel.MoveSelectedCommandInGroup(2, 0);

        Assert.Equal(["AT+THREE", "AT+ONE", "AT+TWO"], viewModel.SelectedCommandGroup.Commands);
    }
}
