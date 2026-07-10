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
        Assert.Equal(6, layout.CurrentPageWindows.Count);
        Assert.Equal(2, layout.CurrentPageWindows.Count(slot => !slot.IsAddSlot));
        Assert.Equal(4, layout.CurrentPageWindows.Count(slot => slot.IsAddSlot));
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
        Assert.Equal(6, layout.CurrentPageWindows.Count);
        Assert.All(layout.CurrentPageWindows, slot => Assert.True(slot.IsAddSlot));
        Assert.All(layout.CurrentPageWindows, slot => Assert.Equal(1, slot.PageIndex));
        Assert.Contains(layout.CurrentPageWindows, slot => slot.PagePosition == 0);
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
        Assert.Equal(6, layout.CurrentPageWindows.Count);
        Assert.All(layout.CurrentPageWindows, slot => Assert.True(slot.IsAddSlot));
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
        Assert.True(layout.IsCommandPanelVerticalShape);
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
    public void Hidden_command_panel_collapses_docked_panel_until_shown()
    {
        var layout = new WorkspaceLayoutViewModel(new ObservableCollection<SerialWindowViewModel>(), _ => { });

        layout.ToggleCommandPanelVisibilityCommand.Execute(null);

        Assert.True(layout.IsCommandPanelHidden);
        Assert.Equal(Visibility.Collapsed, layout.CommandPanelVisibility);
        Assert.Equal(0, layout.CommandPanelWidth);
        Assert.Equal(0, layout.CommandPanelHeight);
        Assert.Equal("显示", layout.CommandPanelVisibilityActionText);

        layout.ToggleCommandPanelVisibilityCommand.Execute(null);

        Assert.False(layout.IsCommandPanelHidden);
        Assert.Equal(Visibility.Visible, layout.CommandPanelVisibility);
        Assert.Equal("隐藏", layout.CommandPanelVisibilityActionText);
        Assert.Equal(300, layout.CommandPanelHeight);
    }

    [Fact]
    public void Hidden_command_panel_does_not_expand_window_when_slot_below_is_occupied()
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

        layout.ToggleCommandPanelVisibilityCommand.Execute(null);
        var first = layout.CurrentPageWindows.Single(slot => slot.Window?.Id == "w1");

        Assert.False(first.CanExpand);
        Assert.False(layout.ToggleWindowExpansionCommand.CanExecute(first));

        layout.ToggleWindowExpansionCommand.Execute(first);

        Assert.Equal(2, layout.SerialGridRows);
        var unchanged = layout.CurrentPageWindows.Single(slot => slot.Window?.Id == "w1");
        Assert.False(unchanged.IsExpanded);
        Assert.Equal(1, unchanged.GridRowSpan);
        Assert.Contains(layout.CurrentPageWindows, slot => slot.Window?.Id == "w4" && slot.PagePosition == 3);
    }

    [Fact]
    public void Hidden_command_panel_does_not_expand_bottom_row_windows()
    {
        var serialWindows = new ObservableCollection<SerialWindowViewModel>
        {
            new("w1", "W1") { PagePosition = 0 },
            new("w2", "W2") { PagePosition = 1 },
            new("w3", "W3") { PagePosition = 2 },
            new("w4", "W4") { PagePosition = 3 },
            new("w5", "W5") { PagePosition = 5 }
        };
        var layout = new WorkspaceLayoutViewModel(serialWindows, _ => { });

        layout.ToggleCommandPanelVisibilityCommand.Execute(null);

        var bottomLeft = layout.CurrentPageWindows.Single(slot => slot.Window?.Id == "w4");
        var bottomRight = layout.CurrentPageWindows.Single(slot => slot.Window?.Id == "w5");
        Assert.False(bottomLeft.CanExpand);
        Assert.False(bottomRight.CanExpand);
        Assert.False(layout.ToggleWindowExpansionCommand.CanExecute(bottomLeft));
        Assert.False(layout.ToggleWindowExpansionCommand.CanExecute(bottomRight));

        layout.ToggleWindowExpansionCommand.Execute(bottomLeft);
        layout.ToggleWindowExpansionCommand.Execute(bottomRight);

        Assert.False(layout.CurrentPageWindows.Single(slot => slot.Window?.Id == "w4").IsExpanded);
        Assert.False(layout.CurrentPageWindows.Single(slot => slot.Window?.Id == "w5").IsExpanded);
    }

    [Fact]
    public void Hidden_command_panel_add_slot_avoids_expanded_window_occupied_positions()
    {
        var serialWindows = new ObservableCollection<SerialWindowViewModel>
        {
            new("w1", "W1"),
            new("w2", "W2"),
            new("w3", "W3")
        };
        var layout = new WorkspaceLayoutViewModel(serialWindows, _ => { });

        layout.ToggleCommandPanelVisibilityCommand.Execute(null);
        var first = layout.CurrentPageWindows.Single(slot => slot.Window?.Id == "w1");
        layout.ToggleWindowExpansionCommand.Execute(first);

        serialWindows.Add(new SerialWindowViewModel("w4", "W4"));

        var added = layout.CurrentPageWindows.Single(slot => slot.Window?.Id == "w4");
        Assert.Equal(1, added.GridRow);
        Assert.Equal(1, added.GridColumn);
        Assert.DoesNotContain(
            layout.CurrentPageWindows.Where(slot => slot.Window is not null)
                .GroupBy(slot => (slot.GridRow, slot.GridColumn)),
            group => group.Count() > 1);
    }

    [Fact]
    public void Page_shows_add_slot_for_each_empty_position()
    {
        var serialWindows = new ObservableCollection<SerialWindowViewModel>
        {
            new("w1", "W1"),
            new("w2", "W2"),
            new("w3", "W3")
        };
        var layout = new WorkspaceLayoutViewModel(serialWindows, _ => { });

        var addSlots = layout.CurrentPageWindows.Where(slot => slot.IsAddSlot).ToArray();

        Assert.Equal(3, addSlots.Length);
        Assert.Contains(addSlots, slot => slot.PagePosition == 3);
        Assert.Contains(addSlots, slot => slot.PagePosition == 4);
        Assert.Contains(addSlots, slot => slot.PagePosition == 5);
    }

    [Fact]
    public void Expanded_window_keeps_one_column_span_when_command_panel_is_hidden()
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
        Assert.Equal(2, topRight.GridRowSpan);
        Assert.Equal(1, topRight.GridColumnSpan);

        layout.ToggleCommandPanelVisibilityCommand.Execute(null);

        Assert.Equal(2, layout.SerialGridRows);
        var expanded = layout.CurrentPageWindows.Single(slot => slot.Window?.Id == "w3");
        Assert.Equal("w3", expanded.Window?.Id);
        Assert.Equal(layout.SerialGridRows, expanded.GridRowSpan);
        Assert.Equal(1, expanded.GridColumnSpan);
        Assert.Contains(layout.CurrentPageWindows, slot => slot.Window?.Id == "w1");
        Assert.Contains(layout.CurrentPageWindows, slot => slot.Window?.Id == "w2");
        Assert.Contains(layout.CurrentPageWindows, slot => slot.Window?.Id == "w5");
        Assert.Equal(1, layout.CurrentPageWindows.Single(slot => slot.Window?.Id == "w1").GridRowSpan);
        Assert.Equal(1, layout.CurrentPageWindows.Single(slot => slot.Window?.Id == "w2").GridRowSpan);
    }

    [Fact]
    public void Hidden_command_panel_keeps_multiple_expanded_windows_expanded()
    {
        var serialWindows = new ObservableCollection<SerialWindowViewModel>
        {
            new("w1", "W1"),
            new("w2", "W2"),
            new("w3", "W3")
        };
        var layout = new WorkspaceLayoutViewModel(serialWindows, _ => { });

        foreach (var id in new[] { "w1", "w2", "w3" })
        {
            var slot = layout.CurrentPageWindows.Single(item => item.Window?.Id == id);
            layout.ToggleWindowExpansionCommand.Execute(slot);
        }

        layout.ToggleCommandPanelVisibilityCommand.Execute(null);

        Assert.Equal(2, layout.SerialGridRows);
        Assert.Equal(3, layout.CurrentPageWindows.Count(slot => slot.IsExpanded));
        Assert.All(
            layout.CurrentPageWindows.Where(slot => slot.Window is not null),
            slot =>
            {
                Assert.True(slot.IsExpanded);
                Assert.Equal(layout.SerialGridRows, slot.GridRowSpan);
                Assert.Equal(1, slot.GridColumnSpan);
            });
    }

    [Fact]
    public void Floating_command_panel_uses_normal_shape_while_preserving_restore_dock()
    {
        var layout = new WorkspaceLayoutViewModel(new ObservableCollection<SerialWindowViewModel>(), _ => { })
        {
            CommandPanelDock = CommandPanelDock.Right
        };

        layout.FloatCommandPanelCommand.Execute(null);

        Assert.True(layout.IsCommandPanelFloating);
        Assert.Equal(CommandPanelDock.Right, layout.CommandPanelDock);
        Assert.False(layout.IsCommandPanelVerticalShape);
        Assert.False(layout.IsCommandPanelDockedVertical);
        Assert.Equal(0, layout.CommandPanelWidth);
        Assert.Equal(0, layout.CommandPanelHeight);
        Assert.Equal(1180, layout.FloatingCommandPanelWidth);
        Assert.Equal(360, layout.FloatingCommandPanelHeight);

        layout.RestoreCommandPanelCommand.Execute(null);

        Assert.False(layout.IsCommandPanelFloating);
        Assert.Equal(CommandPanelDock.Right, layout.CommandPanelDock);
        Assert.True(layout.IsCommandPanelDockedRight);
        Assert.True(layout.IsCommandPanelVerticalShape);
        Assert.Equal(540, layout.CommandPanelWidth);
        Assert.True(double.IsNaN(layout.CommandPanelHeight));
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

    [Fact]
    public void Serial_window_can_move_to_empty_page_position()
    {
        var w1 = new SerialWindowViewModel("w1", "W1") { PagePosition = 0 };
        var w2 = new SerialWindowViewModel("w2", "W2") { PagePosition = 1 };
        var w3 = new SerialWindowViewModel("w3", "W3") { PagePosition = 2 };
        var serialWindows = new ObservableCollection<SerialWindowViewModel> { w1, w2, w3 };
        var layout = new WorkspaceLayoutViewModel(serialWindows, _ => { });

        layout.MoveSerialWindow("w2", 0, 5);

        Assert.Equal(5, w2.PagePosition);
        Assert.Contains(layout.CurrentPageWindows, slot => slot.Window?.Id == "w2" && slot.PagePosition == 5);
        Assert.Contains(layout.CurrentPageWindows, slot => slot.IsAddSlot && slot.PagePosition == 1);
    }
}
