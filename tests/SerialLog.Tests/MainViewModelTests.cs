using System.Windows;
using SerialLog.App.ViewModels;
using SerialLog.Core.Configuration;
using SerialLog.Core.Logging;

namespace SerialLog.Tests;

public class MainViewModelTests
{
    [Fact]
    public void App_version_status_exposes_application_and_protocol_versions()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "serial-log-workspace-" + Guid.NewGuid().ToString("N") + ".json");
        WorkspaceConfigStore.Save(workspacePath, new WorkspaceConfig());

        using var viewModel = new MainViewModel(workspacePath, startReconnectTimer: false);

        Assert.StartsWith("v", viewModel.AppVersionText);
        Assert.Contains("协议", viewModel.ProtocolVersionText);
        Assert.Contains(viewModel.AppVersionText, viewModel.AppBuildStatusText);
        Assert.Contains(viewModel.ProtocolVersionText, viewModel.AppBuildStatusText);
    }

    [Fact]
    public void Remove_window_deletes_added_window_and_clamps_page()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "serial-log-workspace-" + Guid.NewGuid().ToString("N") + ".json");
        WorkspaceConfigStore.Save(workspacePath, new WorkspaceConfig());

        using var viewModel = new MainViewModel(workspacePath, startReconnectTimer: false, confirmDelete: (_, _) => true);
        var addedWindow = viewModel.SerialWindows.Last();

        viewModel.RemoveWindowCommand.Execute(addedWindow);

        Assert.Equal(5, viewModel.SerialWindows.Count);
        Assert.Equal(0, viewModel.CurrentPageIndex);
        Assert.Equal("1 / 1", viewModel.PageLabel);
        Assert.DoesNotContain(viewModel.CurrentPageWindows, slot => ReferenceEquals(slot.Window, addedWindow));
        Assert.Contains(viewModel.CurrentPageWindows, slot => slot.IsAddSlot);
    }

    [Fact]
    public void Remove_window_cancel_keeps_window()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "serial-log-workspace-" + Guid.NewGuid().ToString("N") + ".json");
        WorkspaceConfigStore.Save(workspacePath, new WorkspaceConfig());

        using var viewModel = new MainViewModel(workspacePath, startReconnectTimer: false, confirmDelete: (_, _) => false);
        var addedWindow = viewModel.SerialWindows.Last();

        viewModel.RemoveWindowCommand.Execute(addedWindow);

        Assert.Equal(6, viewModel.SerialWindows.Count);
        Assert.Contains(addedWindow, viewModel.SerialWindows);
        Assert.Contains("已取消删除窗口", viewModel.StatusText);
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

        Assert.Equal(6, viewModel.CurrentPageWindows.Count);
        Assert.Equal(2, viewModel.CurrentPageWindows.Count(slot => !slot.IsAddSlot));
        Assert.Equal(4, viewModel.CurrentPageWindows.Count(slot => slot.IsAddSlot));
    }

    [Fact]
    public void Add_page_does_not_fill_the_current_short_page()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "serial-log-workspace-" + Guid.NewGuid().ToString("N") + ".json");
        WorkspaceConfigStore.Save(workspacePath, new WorkspaceConfig
        {
            SerialWindows =
            [
                new SerialWindowConfig { Id = "center", Title = "Center" },
                new SerialWindowConfig { Id = "r1", Title = "R1" }
            ]
        });

        using var viewModel = new MainViewModel(workspacePath, startReconnectTimer: false);

        viewModel.AddPageCommand.Execute(null);

        Assert.Equal(2, viewModel.SerialWindows.Count);
        Assert.Equal(1, viewModel.CurrentPageIndex);
        Assert.Equal("2 / 2", viewModel.PageLabel);
        Assert.Equal(6, viewModel.CurrentPageWindows.Count);
        Assert.All(viewModel.CurrentPageWindows, slot => Assert.True(slot.IsAddSlot));

        viewModel.AddWindowCommand.Execute(null);

        Assert.Equal(3, viewModel.SerialWindows.Count);
        Assert.Equal(1, viewModel.SerialWindows.Last().PageIndex);
        Assert.Equal(1, viewModel.CurrentPageIndex);
        Assert.Contains(viewModel.CurrentPageWindows, slot => slot.Window == viewModel.SerialWindows.Last());
    }

    [Fact]
    public void Add_window_uses_clicked_add_slot_position()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "serial-log-workspace-" + Guid.NewGuid().ToString("N") + ".json");
        WorkspaceConfigStore.Save(workspacePath, new WorkspaceConfig
        {
            SerialWindows =
            [
                new SerialWindowConfig { Id = "center", Title = "Center", PagePosition = 0 },
                new SerialWindowConfig { Id = "r1", Title = "R1", PagePosition = 1 },
                new SerialWindowConfig { Id = "r2", Title = "R2", PagePosition = 2 }
            ]
        });

        using var viewModel = new MainViewModel(workspacePath, startReconnectTimer: false);
        var targetSlot = viewModel.CurrentPageWindows.Single(slot => slot.IsAddSlot && slot.PagePosition == 5);

        viewModel.AddWindowCommand.Execute(targetSlot);

        var addedWindow = viewModel.SerialWindows.Last();
        Assert.Equal(5, addedWindow.PagePosition);
        Assert.Contains(viewModel.CurrentPageWindows, slot => slot.Window == addedWindow && slot.PagePosition == 5);
    }

    [Fact]
    public void Networked_workspace_marks_local_windows_with_pc_identity()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "serial-log-workspace-" + Guid.NewGuid().ToString("N") + ".json");
        WorkspaceConfigStore.Save(workspacePath, new WorkspaceConfig
        {
            WorkspaceMode = WorkspaceMode.Host,
            LocalPcId = "pc-center",
            LocalPcName = "Center PC",
            LocalPcColor = "#0B75B7",
            SerialWindows =
            [
                new SerialWindowConfig { Id = "center", Title = "Center" }
            ]
        });

        using var viewModel = new MainViewModel(workspacePath, startReconnectTimer: false);

        Assert.True(viewModel.IsCollaborationNetworked);
        Assert.Equal("Center PC", viewModel.SerialWindows.Single().OwnerPcName);
        Assert.Equal("#0B75B7", viewModel.SerialWindows.Single().OwnerPcColor);
        Assert.Equal("Center PC", viewModel.SerialWindows.Single().OwnerBadgeText);
    }

    [Fact]
    public void Local_workspace_applies_pc_color_and_keeps_pc_badge_hidden()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "serial-log-workspace-" + Guid.NewGuid().ToString("N") + ".json");
        WorkspaceConfigStore.Save(workspacePath, new WorkspaceConfig
        {
            WorkspaceMode = WorkspaceMode.Local,
            LocalPcColor = "#16A34A",
            SerialWindows =
            [
                new SerialWindowConfig { Id = "center", Title = "Center" }
            ]
        });

        using var viewModel = new MainViewModel(workspacePath, startReconnectTimer: false);

        Assert.False(viewModel.IsCollaborationNetworked);
        Assert.All(viewModel.SerialWindows, window => Assert.False(window.HasOwnerBadge));
        Assert.All(viewModel.SerialWindows, window => Assert.Equal("#16A34A", window.OwnerPcColor));
    }

    [Fact]
    public void Saving_workspace_persists_collaboration_identity()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "serial-log-workspace-" + Guid.NewGuid().ToString("N") + ".json");
        WorkspaceConfigStore.Save(workspacePath, new WorkspaceConfig());

        using (var viewModel = new MainViewModel(workspacePath, startReconnectTimer: false))
        {
            viewModel.WorkspaceMode = WorkspaceMode.Client;
            viewModel.LocalPcId = "pc-r1";
            viewModel.LocalPcName = "R1 PC";
            viewModel.LocalPcColor = "#16A34A";
            viewModel.HostAddress = "192.168.1.10";
            viewModel.HostPort = 58730;
            viewModel.SaveWorkspace();
        }

        var loaded = WorkspaceConfigStore.Load(workspacePath);
        Assert.Equal(WorkspaceMode.Client, loaded.WorkspaceMode);
        Assert.Equal("pc-r1", loaded.LocalPcId);
        Assert.Equal("R1 PC", loaded.LocalPcName);
        Assert.Equal("#16A34A", loaded.LocalPcColor);
        Assert.Equal("192.168.1.10", loaded.HostAddress);
        Assert.Equal(58730, loaded.HostPort);
        Assert.All(loaded.SerialWindows, window => Assert.Equal("R1 PC", window.OwnerPcName));
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

        Assert.False(viewModel.AreAllLocalSerialWindowsConnected);
        Assert.Equal("连接全部", viewModel.ToggleAllConnectionsActionText);

        viewModel.ConnectAllCommand.Execute(null);

        Assert.Contains("已尝试 1", viewModel.StatusText);
    }

    [Fact]
    public void New_log_session_switches_subsequent_log_writes_to_a_new_directory()
    {
        var root = Path.Combine(Path.GetTempPath(), "serial-log-sessions-" + Guid.NewGuid().ToString("N"));
        var workspacePath = Path.Combine(Path.GetTempPath(), "serial-log-workspace-" + Guid.NewGuid().ToString("N") + ".json");
        WorkspaceConfigStore.Save(workspacePath, new WorkspaceConfig());

        try
        {
            using var viewModel = new MainViewModel(workspacePath, startReconnectTimer: false);
            viewModel.LogRootDirectory = root;
            var window = viewModel.SerialWindows[0];

            viewModel.NewLogSessionCommand.Execute(null);
            Assert.NotEmpty(Directory.GetDirectories(root, "*", SearchOption.AllDirectories));
            window.AppendRemoteLine(new ReceivedLogLine(DateTimeOffset.Now, "first session"));
            var firstLogDirectory = Path.GetDirectoryName(
                Assert.Single(Directory.GetFiles(root, "*.log", SearchOption.AllDirectories)))!;

            viewModel.NewLogSessionCommand.Execute(null);
            window.AppendRemoteLine(new ReceivedLogLine(DateTimeOffset.Now, "second session"));
            var logDirectories = Directory.GetFiles(root, "*.log", SearchOption.AllDirectories)
                .Select(Path.GetDirectoryName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Assert.Equal(2, logDirectories.Length);
            Assert.Contains(firstLogDirectory, logDirectories, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }

            File.Delete(workspacePath);
        }
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
    public void Selected_imported_at_command_can_fill_single_command_editor()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "serial-log-workspace-" + Guid.NewGuid().ToString("N") + ".json");
        WorkspaceConfigStore.Save(workspacePath, new WorkspaceConfig());

        using var viewModel = new MainViewModel(workspacePath, startReconnectTimer: false);
        viewModel.ImportedAtCommands.Add("AT+FREQ=490000000");
        viewModel.SelectedAtCommand = viewModel.ImportedAtCommands.Single();

        viewModel.FillSingleCommandFromAtCommandCommand.Execute(null);

        Assert.Equal("AT+FREQ=490000000", viewModel.CommandText);
        Assert.Contains("已填入单条命令", viewModel.StatusText);
    }

    [Fact]
    public void Clearing_history_selection_does_not_clear_single_command_editor()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "serial-log-workspace-" + Guid.NewGuid().ToString("N") + ".json");
        WorkspaceConfigStore.Save(workspacePath, new WorkspaceConfig());

        using var viewModel = new MainViewModel(workspacePath, startReconnectTimer: false);
        var selectedHistoryProperty = typeof(MainViewModel).GetProperty("SelectedHistoryCommand");

        Assert.NotNull(selectedHistoryProperty);

        selectedHistoryProperty.SetValue(viewModel, "AT+FREQ=490000000");
        Assert.Equal("AT+FREQ=490000000", viewModel.CommandText);

        viewModel.CommandText = "AT+SEND=981,4,11223344";
        selectedHistoryProperty.SetValue(viewModel, null);

        Assert.Equal("AT+SEND=981,4,11223344", viewModel.CommandText);
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

    [Fact]
    public void Command_panel_dock_round_trips_through_workspace()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "serial-log-workspace-" + Guid.NewGuid().ToString("N") + ".json");
        WorkspaceConfigStore.Save(workspacePath, new WorkspaceConfig
        {
            CommandPanelDock = CommandPanelDock.Left
        });

        using (var viewModel = new MainViewModel(workspacePath, startReconnectTimer: false))
        {
            Assert.Equal(CommandPanelDock.Left, viewModel.CommandPanelDock);
            viewModel.SetCommandPanelDockCommand.Execute(CommandPanelDock.Top);
            viewModel.SaveWorkspace();
        }

        using var restored = new MainViewModel(workspacePath, startReconnectTimer: false);

        Assert.Equal(CommandPanelDock.Top, restored.CommandPanelDock);
    }

    [Fact]
    public void Command_panel_hidden_state_round_trips_through_workspace()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "serial-log-workspace-" + Guid.NewGuid().ToString("N") + ".json");
        WorkspaceConfigStore.Save(workspacePath, new WorkspaceConfig());

        using (var viewModel = new MainViewModel(workspacePath, startReconnectTimer: false))
        {
            viewModel.ToggleCommandPanelVisibilityCommand.Execute(null);
            viewModel.SaveWorkspace();
        }

        using var restored = new MainViewModel(workspacePath, startReconnectTimer: false);

        Assert.True(restored.IsCommandPanelHidden);
        Assert.Equal(Visibility.Collapsed, restored.CommandPanelVisibility);
    }

    [Fact]
    public void Command_panel_floating_state_round_trips_through_workspace()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "serial-log-workspace-" + Guid.NewGuid().ToString("N") + ".json");
        WorkspaceConfigStore.Save(workspacePath, new WorkspaceConfig
        {
            CommandPanelDock = CommandPanelDock.Right
        });

        using (var viewModel = new MainViewModel(workspacePath, startReconnectTimer: false))
        {
            viewModel.FloatCommandPanelCommand.Execute(null);
            viewModel.SaveWorkspace();
        }

        using var restored = new MainViewModel(workspacePath, startReconnectTimer: false);

        Assert.True(restored.IsCommandPanelFloating);
        Assert.False(restored.IsCommandPanelHidden);
        Assert.Equal(CommandPanelDock.Right, restored.CommandPanelDock);
        Assert.Equal(Visibility.Collapsed, restored.CommandPanelVisibility);
    }

    [Fact]
    public void Hidden_expanded_windows_round_trip_through_workspace()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "serial-log-workspace-" + Guid.NewGuid().ToString("N") + ".json");
        WorkspaceConfigStore.Save(workspacePath, new WorkspaceConfig
        {
            IsCommandPanelHidden = true,
            ExpandedWindowIds = ["center", "r1", "r2"],
            SerialWindows =
            [
                new SerialWindowConfig { Id = "center", Title = "中心" },
                new SerialWindowConfig { Id = "r1", Title = "R1" },
                new SerialWindowConfig { Id = "r2", Title = "R2" }
            ]
        });

        using var viewModel = new MainViewModel(workspacePath, startReconnectTimer: false);

        Assert.True(viewModel.IsCommandPanelHidden);
        Assert.Equal(3, viewModel.CurrentPageWindows.Count(slot => slot.IsExpanded));
        Assert.All(
            viewModel.CurrentPageWindows.Where(slot => slot.Window is not null),
            slot =>
            {
                Assert.True(slot.IsExpanded);
                Assert.Equal(viewModel.SerialGridRows, slot.GridRowSpan);
                Assert.Equal(1, slot.GridColumnSpan);
            });
    }

    [Fact]
    public void Side_docked_command_panel_uses_compact_width()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "serial-log-workspace-" + Guid.NewGuid().ToString("N") + ".json");
        WorkspaceConfigStore.Save(workspacePath, new WorkspaceConfig
        {
            CommandPanelDock = CommandPanelDock.Right
        });

        using var viewModel = new MainViewModel(workspacePath, startReconnectTimer: false);

        Assert.True(viewModel.IsCommandPanelDockedVertical);
        Assert.True(viewModel.IsCommandPanelVerticalShape);
        Assert.Equal(540, viewModel.CommandPanelWidth);
        Assert.True(double.IsNaN(viewModel.CommandPanelHeight));
        Assert.Equal(3, viewModel.SerialGridRows);
        Assert.Equal(2, viewModel.SerialGridColumns);
    }

    [Fact]
    public void Floating_command_panel_hides_docked_panel_until_restored()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "serial-log-workspace-" + Guid.NewGuid().ToString("N") + ".json");
        WorkspaceConfigStore.Save(workspacePath, new WorkspaceConfig());

        using var viewModel = new MainViewModel(workspacePath, startReconnectTimer: false);

        viewModel.FloatCommandPanelCommand.Execute(null);

        Assert.True(viewModel.IsCommandPanelFloating);
        Assert.Equal(0, viewModel.CommandPanelWidth);
        Assert.Equal(0, viewModel.CommandPanelHeight);
        Assert.Equal("命令区：浮动", viewModel.CommandPanelOrientationLabel);

        viewModel.RestoreCommandPanelCommand.Execute(null);

        Assert.False(viewModel.IsCommandPanelFloating);
        Assert.Equal(CommandPanelDock.Bottom, viewModel.CommandPanelDock);
        Assert.True(double.IsNaN(viewModel.CommandPanelWidth));
        Assert.Equal(300, viewModel.CommandPanelHeight);
    }

    [Fact]
    public void Floating_command_panel_restores_to_previous_side_dock()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "serial-log-workspace-" + Guid.NewGuid().ToString("N") + ".json");
        WorkspaceConfigStore.Save(workspacePath, new WorkspaceConfig
        {
            CommandPanelDock = CommandPanelDock.Right
        });

        using var viewModel = new MainViewModel(workspacePath, startReconnectTimer: false);

        viewModel.FloatCommandPanelCommand.Execute(null);

        Assert.True(viewModel.IsCommandPanelFloating);
        Assert.Equal(CommandPanelDock.Right, viewModel.CommandPanelDock);
        Assert.False(viewModel.IsCommandPanelVerticalShape);
        Assert.False(viewModel.IsCommandPanelDockedVertical);
        Assert.Equal(1180, viewModel.FloatingCommandPanelWidth);
        Assert.Equal(360, viewModel.FloatingCommandPanelHeight);

        viewModel.RestoreCommandPanelCommand.Execute(null);

        Assert.False(viewModel.IsCommandPanelFloating);
        Assert.Equal(CommandPanelDock.Right, viewModel.CommandPanelDock);
        Assert.True(viewModel.IsCommandPanelDockedRight);
        Assert.True(viewModel.IsCommandPanelVerticalShape);
        Assert.Equal(540, viewModel.CommandPanelWidth);
        Assert.True(double.IsNaN(viewModel.CommandPanelHeight));
    }

    [Fact]
    public void Command_panel_tab_selection_exposes_active_target_scope()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "serial-log-workspace-" + Guid.NewGuid().ToString("N") + ".json");
        WorkspaceConfigStore.Save(workspacePath, new WorkspaceConfig());

        using var viewModel = new MainViewModel(workspacePath, startReconnectTimer: false);

        Assert.True(viewModel.IsSingleCommandTabSelected);
        Assert.False(viewModel.IsCommandGroupTabSelected);

        viewModel.SelectedCommandPanelTabIndex = 1;

        Assert.False(viewModel.IsSingleCommandTabSelected);
        Assert.True(viewModel.IsCommandGroupTabSelected);
    }

    [Fact]
    public void Serial_windows_can_be_reordered_and_saved()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "serial-log-workspace-" + Guid.NewGuid().ToString("N") + ".json");
        WorkspaceConfigStore.Save(workspacePath, new WorkspaceConfig
        {
            SerialWindows =
            [
                new SerialWindowConfig { Id = "center", Title = "中心" },
                new SerialWindowConfig { Id = "r1", Title = "R1" },
                new SerialWindowConfig { Id = "r2", Title = "R2" }
            ]
        });

        using (var viewModel = new MainViewModel(workspacePath, startReconnectTimer: false))
        {
            viewModel.MoveSerialWindow("r2", 0);
            viewModel.SaveWorkspace();

            Assert.Equal(["r2", "center", "r1"], viewModel.SerialWindows.Select(window => window.Id));
        }

        using var restored = new MainViewModel(workspacePath, startReconnectTimer: false);

        Assert.Equal(["r2", "center", "r1"], restored.SerialWindows.Select(window => window.Id));
    }

    [Fact]
    public void Imported_at_command_sets_round_trip_through_workspace()
    {
        var workspacePath = Path.Combine(Path.GetTempPath(), "serial-log-workspace-" + Guid.NewGuid().ToString("N") + ".json");
        WorkspaceConfigStore.Save(workspacePath, new WorkspaceConfig());

        using (var viewModel = new MainViewModel(workspacePath, startReconnectTimer: false))
        {
            viewModel.SelectedAtCommandSet.Name = "网关";
            viewModel.ImportedAtCommands.Add("AT+GATEWAY");
            viewModel.AddAtCommandSetCommand.Execute(null);
            viewModel.SelectedAtCommandSet.Name = "Mesh";
            viewModel.ImportedAtCommands.Add("AT+MESH");
            viewModel.SaveWorkspace();
        }

        using var restored = new MainViewModel(workspacePath, startReconnectTimer: false);

        Assert.Equal(["网关", "Mesh"], restored.ImportedAtCommandSets.Select(set => set.Name));
        Assert.Equal("Mesh", restored.SelectedAtCommandSet.Name);
        Assert.Equal(["AT+MESH"], restored.ImportedAtCommands);

        restored.SelectedAtCommandSet = restored.ImportedAtCommandSets.Single(set => set.Name == "网关");

        Assert.Equal(["AT+GATEWAY"], restored.ImportedAtCommands);
    }
}
