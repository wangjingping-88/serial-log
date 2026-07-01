using System.Collections.ObjectModel;
using SerialLog.App.ViewModels;
using SerialLog.Core.Commands;

namespace SerialLog.Tests;

public class CommandPanelViewModelTests
{
    [Fact]
    public void Selected_imported_at_command_fills_group_editor_before_adding()
    {
        var serialWindows = new ObservableCollection<SerialWindowViewModel>
        {
            new("center", "中心"),
            new("r1", "R1")
        };
        var statusText = string.Empty;
        using var viewModel = new CommandPanelViewModel(serialWindows, text => statusText = text);

        viewModel.AddCommandGroupCommand.Execute(null);
        viewModel.ImportedAtCommands.Add("AT+SEND=<addr[0-65535]>,<len[1-300]>,<data>");
        viewModel.SelectedAtCommand = viewModel.ImportedAtCommands.Single();

        viewModel.AddAtCommandToGroupCommand.Execute(null);

        Assert.NotNull(viewModel.SelectedCommandGroup);
        Assert.Equal("AT+SEND=<addr[0-65535]>,<len[1-300]>,<data>", viewModel.SelectedCommandGroup.NewCommand);
        Assert.Empty(viewModel.SelectedCommandGroup.Commands);
        Assert.Contains("填入", statusText);
    }

    [Fact]
    public void Command_group_targets_track_serial_window_collection()
    {
        var serialWindows = new ObservableCollection<SerialWindowViewModel>
        {
            new("center", "中心")
        };
        using var viewModel = new CommandPanelViewModel(serialWindows, _ => { });

        viewModel.AddCommandGroupCommand.Execute(null);
        serialWindows.Add(new SerialWindowViewModel("r1", "R1"));

        Assert.NotNull(viewModel.SelectedCommandGroup);
        Assert.Equal(["center", "r1"], viewModel.SelectedCommandGroup.Targets.Select(target => target.TargetId));
    }

    [Fact]
    public void Selected_history_command_does_not_clear_command_text_when_selection_is_cleared()
    {
        var serialWindows = new ObservableCollection<SerialWindowViewModel>();
        using var viewModel = new CommandPanelViewModel(serialWindows, _ => { });

        viewModel.SelectedHistoryCommand = "AT+FREQ=490000000";
        Assert.Equal("AT+FREQ=490000000", viewModel.CommandText);

        viewModel.CommandText = "AT+SEND=981,4,11223344";
        viewModel.SelectedHistoryCommand = null;

        Assert.Equal("AT+SEND=981,4,11223344", viewModel.CommandText);
    }

    [Fact]
    public void Loop_settings_are_owned_by_command_panel()
    {
        var serialWindows = new ObservableCollection<SerialWindowViewModel>();
        using var viewModel = new CommandPanelViewModel(serialWindows, _ => { });

        viewModel.SelectedLineEnding = LineEnding.CrLf;
        viewModel.SingleCommandLoopIntervalMilliseconds = -1;
        viewModel.SingleCommandLoopCount = -5;

        Assert.Equal(LineEnding.CrLf, viewModel.SelectedLineEnding);
        Assert.Equal(0, viewModel.SingleCommandLoopIntervalMilliseconds);
        Assert.Equal(0, viewModel.SingleCommandLoopCount);
    }
}
