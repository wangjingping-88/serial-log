using System.Collections.ObjectModel;
using System.Windows;
using SerialLog.App.ViewModels;
using SerialLog.Core.Configuration;

namespace SerialLog.Tests;

public class WorkspaceLayoutViewModelTests
{
    [Fact]
    public void Page_shows_add_slot_when_current_page_has_empty_space()
    {
        var serialWindows = new ObservableCollection<SerialWindowViewModel>
        {
            new("center", "中心"),
            new("r1", "R1")
        };
        var layout = new WorkspaceLayoutViewModel(serialWindows, _ => { });

        Assert.Equal("1 / 1", layout.PageLabel);
        Assert.Equal(3, layout.CurrentPageWindows.Count);
        Assert.Equal(2, layout.CurrentPageWindows.Count(slot => !slot.IsAddSlot));
        Assert.True(layout.CurrentPageWindows.Last().IsAddSlot);
    }

    [Fact]
    public void Add_page_selects_an_empty_page_even_when_current_page_has_space()
    {
        var serialWindows = new ObservableCollection<SerialWindowViewModel>
        {
            new("center", "Center"),
            new("r1", "R1")
        };
        var layout = new WorkspaceLayoutViewModel(serialWindows, _ => { });

        layout.AddPageCommand.Execute(null);

        Assert.Equal(1, layout.CurrentPageIndex);
        Assert.Equal("2 / 2", layout.PageLabel);
        Assert.Single(layout.CurrentPageWindows);
        Assert.True(layout.CurrentPageWindows[0].IsAddSlot);
        Assert.Equal(1, layout.CurrentPageWindows[0].PageIndex);
        Assert.Equal(0, layout.CurrentPageWindows[0].PagePosition);
    }

    [Fact]
    public void Empty_current_page_can_be_deleted()
    {
        var serialWindows = new ObservableCollection<SerialWindowViewModel>
        {
            new("center", "Center")
        };
        var layout = new WorkspaceLayoutViewModel(serialWindows, _ => { });

        layout.AddPageCommand.Execute(null);
        layout.AddPageCommand.Execute(null);

        Assert.Equal("3 / 3", layout.PageLabel);
        Assert.True(layout.RemoveCurrentPageCommand.CanExecute(null));

        layout.RemoveCurrentPageCommand.Execute(null);

        Assert.Equal(1, layout.CurrentPageIndex);
        Assert.Equal("2 / 2", layout.PageLabel);
        Assert.Single(layout.CurrentPageWindows);
        Assert.True(layout.CurrentPageWindows[0].IsAddSlot);
    }

    [Fact]
    public void Page_with_serial_windows_cannot_be_deleted()
    {
        var serialWindows = new ObservableCollection<SerialWindowViewModel>
        {
            new("center", "Center")
        };
        var layout = new WorkspaceLayoutViewModel(serialWindows, _ => { });

        layout.AddPageCommand.Execute(null);
        layout.CurrentPageIndex = 0;

        Assert.False(layout.RemoveCurrentPageCommand.CanExecute(null));
    }

    [Fact]
    public void Deleting_empty_middle_page_moves_later_windows_forward()
    {
        var center = new SerialWindowViewModel("center", "Center");
        var r1 = new SerialWindowViewModel("r1", "R1") { PageIndex = 2 };
        var serialWindows = new ObservableCollection<SerialWindowViewModel> { center, r1 };
        var layout = new WorkspaceLayoutViewModel(serialWindows, _ => { });
        layout.EnsurePageCount(3);
        layout.CurrentPageIndex = 1;

        layout.RemoveCurrentPageCommand.Execute(null);

        Assert.Equal(1, r1.PageIndex);
        Assert.Equal("2 / 2", layout.PageLabel);
        Assert.Contains(layout.CurrentPageWindows, slot => slot.Window?.Id == "r1");
    }

    [Fact]
    public void Expanded_window_spans_empty_slot_below_on_short_page()
    {
        var serialWindows = new ObservableCollection<SerialWindowViewModel>
        {
            new("w1", "W1"),
            new("w2", "W2"),
            new("w3", "W3"),
            new("w4", "W4"),
            new("w5", "W5")
        };
        var layout = new WorkspaceLayoutViewModel(serialWindows, _ => { });

        var topRight = layout.CurrentPageWindows.Single(slot => slot.Window?.Id == "w3");
        layout.ToggleWindowExpansionCommand.Execute(topRight);

        topRight = layout.CurrentPageWindows.Single(slot => slot.Window?.Id == "w3");
        Assert.True(topRight.IsExpanded);
        Assert.True(topRight.CanExpand);
        Assert.Equal(0, topRight.GridRow);
        Assert.Equal(2, topRight.GridColumn);
        Assert.Equal(2, topRight.GridRowSpan);
        Assert.DoesNotContain(layout.CurrentPageWindows, slot => slot.IsAddSlot);
    }

    [Fact]
    public void Window_with_occupied_slot_below_does_not_expand()
    {
        var serialWindows = new ObservableCollection<SerialWindowViewModel>
        {
            new("w1", "W1"),
            new("w2", "W2"),
            new("w3", "W3"),
            new("w4", "W4"),
            new("w5", "W5"),
            new("w6", "W6")
        };
        var layout = new WorkspaceLayoutViewModel(serialWindows, _ => { });

        var topLeft = layout.CurrentPageWindows.Single(slot => slot.Window?.Id == "w1");
        layout.ToggleWindowExpansionCommand.Execute(topLeft);

        topLeft = layout.CurrentPageWindows.Single(slot => slot.Window?.Id == "w1");
        Assert.False(topLeft.IsExpanded);
        Assert.False(topLeft.CanExpand);
        Assert.Equal(1, topLeft.GridRowSpan);
    }

    [Fact]
    public void Side_docked_command_panel_uses_vertical_workspace_shape()
    {
        var layout = new WorkspaceLayoutViewModel(new ObservableCollection<SerialWindowViewModel>(), _ => { })
        {
            CommandPanelDock = CommandPanelDock.Right
        };

        Assert.True(layout.IsCommandPanelDockedVertical);
        Assert.Equal(540, layout.CommandPanelWidth);
        Assert.True(double.IsNaN(layout.CommandPanelHeight));
        Assert.Equal(3, layout.SerialGridRows);
        Assert.Equal(2, layout.SerialGridColumns);
        Assert.Equal(Visibility.Visible, layout.CommandPanelVisibility);
    }

    [Fact]
    public void Floating_command_panel_hides_docked_panel_until_restored()
    {
        var layout = new WorkspaceLayoutViewModel(new ObservableCollection<SerialWindowViewModel>(), _ => { });

        layout.FloatCommandPanelCommand.Execute(null);

        Assert.True(layout.IsCommandPanelFloating);
        Assert.Equal(0, layout.CommandPanelWidth);
        Assert.Equal(0, layout.CommandPanelHeight);

        layout.RestoreCommandPanelCommand.Execute(null);

        Assert.False(layout.IsCommandPanelFloating);
        Assert.Equal(CommandPanelDock.Bottom, layout.CommandPanelDock);
        Assert.Equal(300, layout.CommandPanelHeight);
    }

    [Fact]
    public void Serial_windows_can_be_reordered_without_recreating_sessions()
    {
        var center = new SerialWindowViewModel("center", "中心");
        var r1 = new SerialWindowViewModel("r1", "R1");
        var r2 = new SerialWindowViewModel("r2", "R2");
        var serialWindows = new ObservableCollection<SerialWindowViewModel> { center, r1, r2 };
        var layout = new WorkspaceLayoutViewModel(serialWindows, _ => { });

        layout.MoveSerialWindow("r2", 0);

        Assert.Equal(["r2", "center", "r1"], serialWindows.Select(window => window.Id));
        Assert.Same(r2, serialWindows[0]);
    }
}
