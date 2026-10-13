using System.Collections.Specialized;
using System.Runtime.InteropServices;
using SerialLog.App.ViewModels;
using SerialLog.Core.Configuration;

namespace SerialLog.Tests;

public class CommandGroupEditingTests
{
    private static CommandGroupEditorViewModel Group(params string[] commands) => new(new CommandGroupConfig { Commands = commands.ToList() });

    [Fact]
    public void Editing_duplicate_command_replaces_only_clicked_row_and_preserves_order()
    {
        using var panel = new CommandPanelViewModel([], _ => { }, editCommandInGroup: _ => "  中文命令=修改  ");
        var group = Group("duplicate", "middle", "duplicate"); panel.CommandGroups.Add(group); panel.SelectedCommandGroup = group;
        var notified = false; panel.PropertyChanged += (_, e) => notified |= e.PropertyName == nameof(panel.CommandGroups);
        panel.EditCommandInGroupCommand.Execute(2);
        Assert.Equal(new[] { "duplicate", "middle", "中文命令=修改" }, group.Commands);
        Assert.Equal(3, group.Commands.Count); Assert.True(notified);
        Assert.Equal(group.Commands, group.ToConfig().Commands);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("original")]
    public void Cancel_empty_and_unchanged_edits_preserve_original(string? response)
    {
        using var panel = new CommandPanelViewModel([], _ => { }, editCommandInGroup: _ => response);
        var group = Group("original"); panel.SelectedCommandGroup = group;
        panel.EditCommandInGroupCommand.Execute(0);
        Assert.Equal("original", Assert.Single(group.Commands));
        if (response == "   ") Assert.Contains("不能为空", panel.StatusText);
    }

    [Fact]
    public void Editor_keeps_opening_group_even_if_current_group_changes_before_save()
    {
        CommandPanelViewModel? panel = null;
        var first = Group("first"); var second = Group("second");
        using (panel = new CommandPanelViewModel([], _ => { }, editCommandInGroup: _ => { panel!.SelectedCommandGroup = second; return "edited"; }))
        {
            panel.SelectedCommandGroup = first; panel.EditCommandInGroupCommand.Execute(0);
            Assert.Equal("edited", first.Commands[0]); Assert.Equal("second", second.Commands[0]);
        }
    }

    [Fact]
    public void Changed_list_during_editor_is_not_overwritten()
    {
        var group = Group("original");
        using var panel = new CommandPanelViewModel([], _ => { }, editCommandInGroup: _ => { group.Commands[0] = "changed elsewhere"; return "edited"; });
        panel.SelectedCommandGroup = group; panel.EditCommandInGroupCommand.Execute(0);
        Assert.Equal("changed elsewhere", group.Commands[0]); Assert.Contains("列表已变化", panel.StatusText);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task Invalid_selection_disables_actions_and_never_edits_or_copies(int index)
    {
        var edits = 0; var copies = 0;
        using var panel = new CommandPanelViewModel([], _ => { }, editCommandInGroup: text => { edits++; return text; }, copyCommandText: _ => { copies++; return Task.CompletedTask; });
        Assert.False(panel.EditCommandInGroupCommand.CanExecute(0));
        panel.SelectedCommandGroup = Group("only");
        Assert.False(panel.EditCommandInGroupCommand.CanExecute(index)); Assert.False(panel.CopyCommandFromGroupCommand.CanExecute(index)); Assert.False(panel.RemoveCommandFromGroupCommand.CanExecute(index));
        panel.EditCommandInGroupCommand.Execute(index); panel.RemoveCommandFromGroupCommand.Execute(index); await panel.CopyCommandFromGroupAsync(index);
        Assert.Equal(0, edits); Assert.Equal(0, copies); Assert.Single(panel.SelectedCommandGroup.Commands);
    }

    [Fact]
    public void Deleting_duplicate_command_uses_selected_index_not_first_equal_value()
    {
        using var panel = new CommandPanelViewModel([], _ => { });
        var group = Group("same", "middle", "same"); panel.SelectedCommandGroup = group;
        var removedIndex = -1; group.Commands.CollectionChanged += (_, e) => { if (e.Action == NotifyCollectionChangedAction.Remove) removedIndex = e.OldStartingIndex; };
        panel.RemoveCommandFromGroupCommand.Execute(2);
        Assert.Equal(2, removedIndex); Assert.Equal(new[] { "same", "middle" }, group.Commands);
    }

    [Fact]
    public async Task Copy_uses_complete_original_command_and_does_not_mutate_group()
    {
        var full = "  中文=" + new string('A', 10000) + "  "; string? copied = null;
        using var panel = new CommandPanelViewModel([], _ => { }, copyCommandText: text => { copied = text; return Task.CompletedTask; });
        panel.SelectedCommandGroup = Group("first", full);
        await panel.CopyCommandFromGroupAsync(1);
        Assert.Equal(full, copied); Assert.Equal(full, panel.SelectedCommandGroup.Commands[1]);
        Assert.Equal(2, panel.SelectedCommandGroup.Commands.Count); Assert.Contains("完整命令", panel.StatusText);
    }

    [Fact]
    public async Task Pending_copy_disables_repeat_and_captures_text_before_group_switch()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var writes = new List<string>();
        using var panel = new CommandPanelViewModel([], _ => { }, copyCommandText: text => { writes.Add(text); return completion.Task; });
        panel.SelectedCommandGroup = Group("first"); var copy = panel.CopyCommandFromGroupAsync(0);
        Assert.False(copy.IsCompleted); Assert.False(panel.CopyCommandFromGroupCommand.CanExecute(0));
        panel.SelectedCommandGroup = Group("second"); await panel.CopyCommandFromGroupAsync(0);
        Assert.Equal("first", Assert.Single(writes)); completion.SetResult(); await copy;
        Assert.True(panel.CopyCommandFromGroupCommand.CanExecute(0)); Assert.Equal("second", panel.SelectedCommandGroup.Commands[0]);
    }

    [Fact]
    public async Task Clipboard_failure_is_reported_without_throwing_or_changing_commands()
    {
        using var panel = new CommandPanelViewModel([], _ => { }, copyCommandText: _ => Task.FromException(new ExternalException("剪贴板被其他程序占用")));
        panel.SelectedCommandGroup = Group("original");
        await panel.CopyCommandFromGroupAsync(0);
        Assert.Contains("复制命令失败", panel.StatusText); Assert.Equal("original", panel.SelectedCommandGroup.Commands[0]);
        Assert.True(panel.CopyCommandFromGroupCommand.CanExecute(0));
    }

    [Fact]
    public void Edited_commands_round_trip_with_workspace_configuration()
    {
        var root = Path.Combine(Path.GetTempPath(), "command-group-edit-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            using var panel = new CommandPanelViewModel([], _ => { }, editCommandInGroup: _ => "AT+中文=保存");
            var group = Group("old", "unchanged"); panel.SelectedCommandGroup = group; panel.EditCommandInGroupCommand.Execute(0);
            var path = Path.Combine(root, "workspace.json"); WorkspaceConfigStore.Save(path, new WorkspaceConfig { CommandGroups = [group.ToConfig()] });
            Assert.Equal(new[] { "AT+中文=保存", "unchanged" }, WorkspaceConfigStore.Load(path).CommandGroups[0].Commands);
        }
        finally { Directory.Delete(root, true); }
    }
}
