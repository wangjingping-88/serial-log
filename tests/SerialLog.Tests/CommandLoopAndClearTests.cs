using System.Collections.Concurrent;
using System.Text.Json;
using SerialLog.App.ViewModels;
using SerialLog.Core.Collaboration;
using SerialLog.Core.Configuration;

namespace SerialLog.Tests;

public class CommandLoopAndClearTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Single_command_zero_is_one_round_and_finite_count_is_respected(int count)
    {
        var sent = new ConcurrentQueue<string>();
        using var window = Remote((payload, _) => { sent.Enqueue(payload); return Task.CompletedTask; });
        using var panel = new CommandPanelViewModel([window], _ => { })
        { CommandText = "AT", SingleCommandLoopCount = count, SingleCommandLoopIntervalMilliseconds = 0 };
        window.IsSelectedForSend = true;
        panel.ToggleSingleCommandLoopCommand.Execute(null);
        await Until(() => !panel.IsSingleCommandLoopRunning);
        Assert.Equal(Math.Max(1, count), sent.Count);
        Assert.False(panel.SingleCommandInfiniteLoop);
    }

    [Fact]
    public async Task Single_command_infinite_choice_is_snapshotted_and_cancellable()
    {
        var sent = new ConcurrentQueue<string>();
        using var window = Remote((payload, _) => { sent.Enqueue(payload); return Task.CompletedTask; });
        using var panel = new CommandPanelViewModel([window], _ => { })
        { CommandText = "AT", SingleCommandLoopCount = 0, SingleCommandInfiniteLoop = true, SingleCommandLoopIntervalMilliseconds = 1 };
        window.IsSelectedForSend = true;
        panel.ToggleSingleCommandLoopCommand.Execute(null);
        await Until(() => sent.Count >= 2);
        panel.SingleCommandInfiniteLoop = false;
        var before = sent.Count;
        await Until(() => sent.Count >= before + 2);
        Assert.True(panel.IsSingleCommandLoopRunning);
        await panel.StopLoopsAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(panel.IsSingleCommandLoopRunning);
    }

    [Fact]
    public void Clearing_groups_requires_confirmation_and_removes_only_the_current_workspaces_queue_references()
    {
        var confirmed = false; var confirmations = 0;
        using var a = new CommandPanelViewModel([], _ => { }, confirmDelete: (_, message) =>
        { confirmations++; Assert.Contains("队列", message); return confirmed; });
        using var b = new CommandPanelViewModel([], _ => { });
        Assert.False(a.ClearCommandGroupsCommand.CanExecute(null));
        a.AddCommandGroupCommand.Execute(null); a.GroupScheduler.AddToQueue(a.SelectedCommandGroup!);
        a.DuplicateCommandGroupCommand.Execute(null); a.GroupScheduler.AddToQueue(a.SelectedCommandGroup!);
        b.AddCommandGroupCommand.Execute(null); b.GroupScheduler.AddToQueue(b.SelectedCommandGroup!);
        a.ClearCommandGroupsCommand.Execute(null);
        Assert.Equal(2, a.CommandGroups.Count); Assert.Equal(2, a.GroupScheduler.Queue.Count);
        confirmed = true; a.ClearCommandGroupsCommand.Execute(null);
        Assert.Equal(2, confirmations); Assert.Empty(a.CommandGroups); Assert.Empty(a.GroupScheduler.Queue);
        Assert.Null(a.SelectedCommandGroup); Assert.False(a.ClearCommandGroupsCommand.CanExecute(null));
        Assert.Single(b.CommandGroups); Assert.Single(b.GroupScheduler.Queue);
    }

    [Fact]
    public void Settings_clear_changes_only_draft_until_save_and_keeps_owner_protected_groups()
    {
        using var owner = new CommandPanelViewModel([], _ => { });
        owner.AddCommandGroupCommand.Execute(null); var protectedGroup = owner.SelectedCommandGroup!;
        owner.DuplicateCommandGroupCommand.Execute(null); var idle = owner.SelectedCommandGroup!;
        owner.GroupScheduler.AddToQueue(protectedGroup); owner.GroupScheduler.AddToQueue(idle);
        using var draft = new CommandPanelViewModel([], _ => { }, (_, _) => true)
        { GroupDeletionGuard = id => id != protectedGroup.Id };
        draft.LoadCommandGroupLists(owner.CommandGroupLists.Select(list => list.ToConfig()), string.Empty);
        foreach (var group in owner.CommandGroups) draft.CommandGroups.Add(new(group.ToConfig()));
        draft.ClearCommandGroupsCommand.Execute(null);
        Assert.Equal(protectedGroup.Id, Assert.Single(draft.CommandGroups).Id);
        Assert.Equal(2, owner.CommandGroups.Count); Assert.Equal(2, owner.GroupScheduler.Queue.Count);
        Assert.False(draft.ClearCommandGroupsCommand.CanExecute(null));
        owner.ApplyCommandGroupSettings(draft.CommandGroups.Select(group => group.ToConfig()).ToArray(), protectedGroup.Id);
        Assert.Same(protectedGroup, Assert.Single(owner.CommandGroups));
        Assert.Equal(protectedGroup.Id, Assert.Single(owner.GroupScheduler.Queue).GroupId);
    }

    [Fact]
    public async Task Clearing_groups_preserves_in_flight_and_pending_queue_groups_without_stopping_them()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var window = Remote(async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); });
        using var panel = new CommandPanelViewModel([window], _ => { }, confirmDelete: (_, message) =>
        { Assert.Contains("保留", message); return true; });
        panel.AddCommandGroupCommand.Execute(null); var running = panel.SelectedCommandGroup!;
        running.Commands.Add("A"); running.Targets.Single().IsSelected = true;
        panel.DuplicateCommandGroupCommand.Execute(null); var pending = panel.SelectedCommandGroup!;
        panel.AddCommandGroupCommand.Execute(null); var idle = panel.SelectedCommandGroup!;
        panel.GroupScheduler.AddToQueue(running); panel.GroupScheduler.AddToQueue(pending);
        var run = panel.GroupScheduler.StartQueueAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        panel.ClearCommandGroupsCommand.Execute(null);
        Assert.Equal(new[] { running, pending }, panel.CommandGroups);
        Assert.DoesNotContain(idle, panel.CommandGroups); Assert.Equal(2, panel.GroupScheduler.Queue.Count);
        Assert.True(running.IsRunning && panel.GroupScheduler.IsQueueRunning);
        Assert.False(panel.ClearCommandGroupsCommand.CanExecute(null));
        release.TrySetResult(); await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(panel.GroupScheduler.Queue, entry => Assert.StartsWith("完成", entry.Status));
    }

    [Fact]
    public void Missing_infinite_flag_defaults_to_single_round_and_export_restores_choices_per_workspace()
    {
        var config = JsonSerializer.Deserialize<CommandGroupConfig>("{\"LoopCount\":0}")!;
        Assert.False(config.IsInfiniteLoop);
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        using var a = new MainViewModel(Path.Combine(repo, "artifacts/loops-a.json"), false,
            configuration: new WorkspaceConfig { CommandGroups = [config] });
        using var b = new MainViewModel(Path.Combine(repo, "artifacts/loops-b.json"), false, configuration: new());
        Assert.False(a.SingleCommandInfiniteLoop || a.CommandGroups.Single().IsInfiniteLoop);
        a.SingleCommandInfiniteLoop = true; a.CommandGroups.Single().IsInfiniteLoop = true;
        using var restored = new MainViewModel(Path.Combine(repo, "artifacts/loops-restored.json"), false,
            configuration: a.ExportConfiguration());
        Assert.True(restored.SingleCommandInfiniteLoop && restored.CommandGroups.Single().IsInfiniteLoop);
        Assert.False(b.SingleCommandInfiniteLoop); Assert.Empty(b.CommandGroups);
    }

    private static SerialWindowViewModel Remote(Func<string, CancellationToken, Task> send) => SerialWindowViewModel.CreateRemote(
        new CollaborationClientSnapshot("pc", "来源电脑", "#123456", []),
        new("node", "节点", "COM7", 460800, true, 0), (_, payload, token) => send(payload, token));

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(1, timeout.Token);
    }
}
