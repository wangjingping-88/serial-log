using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using SerialLog.App.ViewModels;
using SerialLog.Core.Commands;
using SerialLog.Core.Configuration;

namespace SerialLog.Tests;

public class CommandGroupSchedulerTests
{
    [Fact]
    public async Task Different_ports_run_in_parallel_but_same_resource_is_exclusive_until_send_finishes()
    {
        var first = new Target("first", hold: true, ignoreCancellation: true);
        var alias = new Target("alias");
        var other = new Target("other", hold: true);
        var a = Group("A", "first"); var b = Group("B", "alias"); var c = Group("C", "other");
        using var scheduler = new CommandGroupSchedulerViewModel([a, b, c], () => [first, alias, other], _ => { },
            resourceKey: t => t.Id == "other" ? "COM8" : t.Id == "alias" ? "com7" : "COM7");
        var runA = scheduler.StartGroupAsync(a); var runC = scheduler.StartGroupAsync(c);
        await Task.WhenAll(first.Entered.Task, other.Entered.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(a.IsRunning && c.IsRunning);
        Assert.Contains("串口冲突", (await scheduler.StartGroupAsync(b)).Message);
        Assert.Empty(alias.Payloads);
        var stopping = scheduler.StopAsync("first");
        Assert.False(stopping.IsCompleted); // 不支持取消的在途驱动写入必须先结束。
        Assert.Contains("串口冲突", (await scheduler.StartGroupAsync(b)).Message);
        first.Release.TrySetResult(); await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((await runA).Cancelled);
        Assert.True(c.IsRunning);
        Assert.True((await scheduler.StartGroupAsync(b)).Success);
        other.Release.TrySetResult();
        await Task.WhenAll(runA, runC).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(scheduler.HasRunningTasks);
    }

    [Fact]
    public async Task Selected_groups_start_without_implicit_waiting_for_conflicts()
    {
        var first = new Target("first", hold: true); var second = new Target("second", hold: true);
        var a = Group("A", "first"); var b = Group("B", "first"); var c = Group("C", "second");
        a.IsSelectedForRun = b.IsSelectedForRun = c.IsSelectedForRun = true;
        using var scheduler = new CommandGroupSchedulerViewModel([a, b, c], () => [first, second], _ => { });
        scheduler.RunSelectedCommand.Execute(null);
        await Task.WhenAll(first.Entered.Task, second.Entered.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(a.IsRunning && c.IsRunning); Assert.False(b.IsRunning);
        Assert.Contains("串口冲突", b.ExecutionStatus);
        await scheduler.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(first.Payloads); Assert.Single(second.Payloads);
    }

    [Fact]
    public async Task Finite_runs_keep_commands_targets_and_parameters_snapshotted()
    {
        var target = new Target("port", hold: true); var ignored = new Target("new");
        var group = Group("原名称", "port", 2, @"AT\r\n数据");
        using var scheduler = new CommandGroupSchedulerViewModel([group], () => [target, ignored], _ => { });
        var run = scheduler.StartGroupAsync(group);
        await target.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(scheduler.CanDelete(group.Id));
        group.Name = "新名称"; group.Commands[0] = "NEW"; group.LoopCount = 8; group.InterpretEscapes = false;
        group.Targets.Clear(); group.Targets.Add(new("new", "新目标", true));
        target.Release.TrySetResult();
        Assert.True((await run.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        Assert.Equal(new[] { "AT\r\n数据", "AT\r\n数据" }, target.Payloads);
        Assert.Empty(ignored.Payloads); Assert.True(scheduler.CanDelete(group.Id));
    }

    [Fact]
    public async Task Infinite_group_is_cancellable_and_cannot_enter_queue()
    {
        var target = new Target("port", hold: true); var group = Group("无限", "port", 0);
        group.IsInfiniteLoop = true;
        var status = "";
        using var scheduler = new CommandGroupSchedulerViewModel([group], () => [target], s => status = s);
        Assert.False(scheduler.AddToQueueCommand.CanExecute(group));
        Assert.False(scheduler.AddToQueue(group)); Assert.Contains("无限", status);
        group.IsInfiniteLoop = false; Assert.True(scheduler.AddToQueueCommand.CanExecute(group));
        group.IsInfiniteLoop = true; Assert.False(scheduler.AddToQueueCommand.CanExecute(group));
        var run = scheduler.StartGroupAsync(group);
        await target.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await scheduler.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((await run).Cancelled); Assert.False(group.IsRunning);
    }

    [Fact]
    public async Task Queue_allows_duplicate_groups_preserves_order_and_uses_each_groups_repeat_count()
    {
        var target = new Target("port"); var a = Group("A", "port", 2, "A"); var b = Group("B", "port", 1, "B");
        using var scheduler = new CommandGroupSchedulerViewModel([a, b], () => [target], _ => { });
        Assert.True(scheduler.AddToQueue(a)); Assert.True(scheduler.AddToQueue(b)); Assert.True(scheduler.AddToQueue(a));
        Assert.Equal(3, scheduler.Queue.Select(q => q.Id).Distinct().Count());
        scheduler.SelectedQueueItem = scheduler.Queue[1]; scheduler.MoveQueueUpCommand.Execute(null);
        Assert.Equal(new[] { "B", "A", "A" }, scheduler.Queue.Select(q => q.Name));
        scheduler.RemoveQueueItemCommand.Execute(scheduler.Queue[2]);
        scheduler.SelectedGroup = b; scheduler.MoveGroupUpCommand.Execute(null);
        Assert.Equal(new[] { b, a }, scheduler.Groups);
        a.Name = "改名"; Assert.Equal("改名", scheduler.Queue[1].Name);
        await scheduler.StartQueueAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "B", "A", "A" }, target.Payloads);
        Assert.All(scheduler.Queue, q => Assert.StartsWith("完成", q.Status));
        Assert.False(scheduler.HasRunningTasks);
    }

    [Fact]
    public async Task Queue_snapshots_future_groups_and_locks_its_structure_while_running()
    {
        var target = new Target("port", hold: true); var a = Group("A", "port", 1, "A"); var b = Group("B", "port", 2, "B");
        using var scheduler = new CommandGroupSchedulerViewModel([a, b], () => [target], _ => { });
        scheduler.AddToQueue(a); scheduler.AddToQueue(b);
        var run = scheduler.StartQueueAsync();
        await target.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(scheduler.CanEditQueue); Assert.False(scheduler.CanDelete(b.Id));
        Assert.False(scheduler.AddToQueue(a));
        Assert.False(scheduler.RemoveQueueItemCommand.CanExecute(scheduler.Queue[1]));
        b.Commands[0] = "CHANGED"; b.IsInfiniteLoop = true;
        target.Release.TrySetResult(); await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "A", "B", "B" }, target.Payloads);
        Assert.True(scheduler.CanEditQueue);
        await scheduler.StartQueueAsync();
        Assert.Contains("无限", scheduler.QueueStatus); Assert.Equal(3, target.Payloads.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Queue_stops_after_send_failure_or_disconnected_target(bool disconnected)
    {
        var target = new Target("port") { Connected = !disconnected, Fail = !disconnected };
        var a = Group("A", "port", 1, "A"); var b = Group("B", "port", 1, "B");
        using var scheduler = new CommandGroupSchedulerViewModel([a, b], () => [target], _ => { });
        scheduler.AddToQueue(a); scheduler.AddToQueue(b);
        await scheduler.StartQueueAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("第 1 项", scheduler.QueueStatus); Assert.Equal("未执行", scheduler.Queue[1].Status);
        Assert.DoesNotContain("B", target.Payloads);
    }

    [Fact]
    public async Task Stopping_target_in_pending_queue_cancels_queue_without_stopping_unrelated_group()
    {
        var first = new Target("first", hold: true); var next = new Target("next"); var other = new Target("other", hold: true);
        var a = Group("A", "first"); var b = Group("B", "next"); var c = Group("C", "other");
        using var scheduler = new CommandGroupSchedulerViewModel([a, b, c], () => [first, next, other], _ => { });
        scheduler.AddToQueue(a); scheduler.AddToQueue(b);
        var queue = scheduler.StartQueueAsync(); var independent = scheduler.StartGroupAsync(c);
        await Task.WhenAll(first.Entered.Task, other.Entered.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(scheduler.HasTarget("next"));
        await scheduler.StopAsync("next").WaitAsync(TimeSpan.FromSeconds(5));
        await queue; Assert.Empty(next.Payloads); Assert.True(c.IsRunning);
        other.Release.TrySetResult(); await independent.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task External_firmware_conflict_is_reported_even_when_busy_target_is_filtered_out()
    {
        var group = Group("升级", "port");
        using var scheduler = new CommandGroupSchedulerViewModel([group], () => [], _ => { }, _ => "目标正在烧录");
        Assert.Contains("烧录", (await scheduler.StartGroupAsync(group)).Message);
        Assert.False(scheduler.HasRunningTasks);
    }

    [Fact]
    public async Task Separate_workspaces_keep_background_tasks_running_and_dispose_cancels_safely()
    {
        var first = new Target("first", hold: true); var second = new Target("second", hold: true);
        var a = Group("A", "first"); var b = Group("B", "second");
        using var schedulerA = new CommandGroupSchedulerViewModel([a], () => [first], _ => { });
        using var schedulerB = new CommandGroupSchedulerViewModel([b], () => [second], _ => { });
        var runA = schedulerA.StartGroupAsync(a); var runB = schedulerB.StartGroupAsync(b);
        await Task.WhenAll(first.Entered.Task, second.Entered.Task).WaitAsync(TimeSpan.FromSeconds(5));
        schedulerA.Dispose(); Assert.True((await runA.WaitAsync(TimeSpan.FromSeconds(5))).Cancelled);
        Assert.True(b.IsRunning); second.Release.TrySetResult(); Assert.True((await runB).Success);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Finite_zero_means_one_round_for_direct_runs_and_queue(int count)
    {
        var target = new Target("port"); var group = Group("有限次数", "port", count);
        using var scheduler = new CommandGroupSchedulerViewModel([group], () => [target], _ => { });
        Assert.False(group.IsInfiniteLoop); Assert.True(group.IsLoopCountEnabled);
        Assert.True((await scheduler.StartGroupAsync(group).WaitAsync(TimeSpan.FromSeconds(5))).Success);
        Assert.Equal(Math.Max(1, count), target.Payloads.Count);
        Assert.True(scheduler.AddToQueueCommand.CanExecute(group)); Assert.True(scheduler.AddToQueue(group));
        await scheduler.StartQueueAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Math.Max(1, count) * 2, target.Payloads.Count);
    }

    [Fact]
    public async Task Infinite_checkbox_is_snapshotted_and_explicit_one_round_overrides_it()
    {
        var target = new Target("port", hold: true); var group = Group("快照", "port", 1);
        group.IsInfiniteLoop = true;
        using var scheduler = new CommandGroupSchedulerViewModel([group], () => [target], _ => { });
        var run = scheduler.StartGroupAsync(group);
        await target.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        group.IsInfiniteLoop = false; target.Release.TrySetResult();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (target.Payloads.Count < 2) await Task.Delay(1, timeout.Token);
        await scheduler.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((await run).Cancelled);
        group.IsInfiniteLoop = true;
        var before = target.Payloads.Count;
        Assert.True((await scheduler.StartGroupAsync(group, 1).WaitAsync(TimeSpan.FromSeconds(5))).Success);
        Assert.Equal(before + 1, target.Payloads.Count);
    }

    [Fact]
    public async Task Clearing_queue_is_workspace_local_and_is_blocked_while_running()
    {
        var target = new Target("port", hold: true); var group = Group("A", "port", 0);
        using var a = new CommandGroupSchedulerViewModel([group], () => [target], _ => { });
        using var b = new CommandGroupSchedulerViewModel([Group("B", "other", 0)], () => [], _ => { });
        a.AddToQueue(group); b.AddToQueue(b.Groups.Single());
        var run = a.StartQueueAsync(); await target.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(a.ClearQueueCommand.CanExecute(null)); a.ClearQueueCommand.Execute(null);
        Assert.Single(a.Queue); Assert.Single(b.Queue);
        target.Release.TrySetResult(); await run.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(a.ClearQueueCommand.CanExecute(null)); a.ClearQueueCommand.Execute(null);
        Assert.Empty(a.Queue); Assert.Null(a.SelectedQueueItem); Assert.Single(b.Queue);
        Assert.False(a.ClearQueueCommand.CanExecute(null)); Assert.Contains("清空", a.QueueStatus);
    }

    [Fact]
    public void Smart_grouping_uses_exact_target_sets_preserves_bucket_order_and_places_empty_targets_last()
    {
        static CommandGroupEditorViewModel WithTargets(string name, params string[] ids)
        {
            var group = new CommandGroupEditorViewModel { Name = name };
            group.Commands.Add(name);
            foreach (var id in ids) group.Targets.Add(new(id, "同一个显示名称", true));
            return group;
        }
        var empty1 = WithTargets("未选1"); var a1 = WithTargets("A1", "a");
        var ab1 = WithTargets("AB1", "a", "b"); var b1 = WithTargets("B1", "b");
        var a2 = WithTargets("A2", "a"); a2.Targets.Add(new("unused", "未勾选", false));
        var ab2 = WithTargets("AB2", "b", "a"); var empty2 = WithTargets("未选2");
        var a3 = WithTargets("A3", "a", "a"); var collision1 = WithTargets("键1", "1:a"); var collision2 = WithTargets("键2", "1", "a");
        using var scheduler = new CommandGroupSchedulerViewModel([empty1, a1, ab1, b1, a2, ab2, empty2, a3, collision1, collision2], () => [], _ => { });
        foreach (var group in scheduler.Groups) scheduler.AddToQueue(group);
        var originalQueue = scheduler.Queue.ToArray(); var ids = scheduler.Groups.Select(g => g.Id).ToHashSet();
        scheduler.SelectedGroup = ab2; scheduler.SmartGroupCommand.Execute(null);
        var expected = new[] { a1, a2, a3, ab1, ab2, b1, collision1, collision2, empty1, empty2 };
        Assert.Equal(expected, scheduler.Groups); Assert.Same(ab2, scheduler.SelectedGroup);
        Assert.Equal("第 5 / 10 组", scheduler.SelectedGroupPositionText);
        Assert.Equal(originalQueue, scheduler.Queue); Assert.True(ids.SetEquals(scheduler.Groups.Select(g => g.Id)));
        Assert.All(scheduler.Groups, group => Assert.Equal(group.Name, Assert.Single(group.Commands)));
        scheduler.SmartGroupCommand.Execute(null); Assert.Equal(expected, scheduler.Groups);
        Assert.Equal(originalQueue, scheduler.Queue);
    }

    [Fact]
    public async Task Grouping_does_not_stop_running_tasks_or_change_another_workspaces_order()
    {
        var target = new Target("port", hold: true);
        var first = Group("A1", "port"); var other = Group("B", "other"); var last = Group("A2", "port");
        using var a = new CommandGroupSchedulerViewModel([first, other, last], () => [target], _ => { });
        using var b = new CommandGroupSchedulerViewModel([Group("其他工作区1", "x"), Group("其他工作区2", "y")], () => [], _ => { });
        var orderB = b.Groups.ToArray(); a.AddToQueue(other); a.AddToQueue(first); var queue = a.Queue.ToArray();
        var run = a.StartGroupAsync(first); await target.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        a.SmartGroupCommand.Execute(null);
        Assert.Equal(new[] { first, last, other }, a.Groups); Assert.True(first.IsRunning);
        Assert.Equal(orderB, b.Groups); Assert.Equal(queue, a.Queue);
        target.Release.TrySetResult(); Assert.True((await run.WaitAsync(TimeSpan.FromSeconds(5))).Success);
    }

    private static CommandGroupEditorViewModel Group(string name, string target, int repeats = 1, string command = "AT")
    {
        var group = new CommandGroupEditorViewModel { Name = name, LoopCount = repeats, DelayMilliseconds = 0, LoopIntervalMilliseconds = 0, LineEnding = LineEnding.None };
        group.Commands.Add(command); group.Targets.Add(new(target, target, true)); return group;
    }

    [Fact]
    public async Task List_scope_limits_parallel_run_and_keeps_already_running_hidden_group_and_queue_unchanged()
    {
        var first = new Target("first", hold: true); var other = new Target("other", hold: true);
        var a = Group("AP", "first"); var b = Group("IOT", "other");
        a.IsSelectedForRun = b.IsSelectedForRun = true;
        var visible = new ObservableCollection<CommandGroupEditorViewModel> { a };
        using var scheduler = new CommandGroupSchedulerViewModel([a, b], () => [first, other], _ => { }) { GroupScope = () => visible };
        scheduler.AddToQueue(a); var queue = scheduler.Queue.Single();
        scheduler.RunSelectedCommand.Execute(null);
        await first.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(other.Payloads); Assert.True(a.IsRunning); Assert.False(b.IsRunning);
        visible.Clear(); visible.Add(b); scheduler.RefreshScope(); scheduler.RunSelectedCommand.Execute(null);
        await other.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(a.IsRunning && b.IsRunning); Assert.Same(queue, scheduler.Queue.Single());
        scheduler.RunSelectedCommand.Execute(null); Assert.Single(other.Payloads);
        await scheduler.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class Target(string id, bool hold = false, bool ignoreCancellation = false) : ICommandTarget
    {
        public string Id { get; } = id;
        public bool Connected { get; set; } = true;
        public bool IsConnected => Connected;
        public bool Fail { get; set; }
        public ConcurrentQueue<string> Payloads { get; } = new();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task SendAsync(string payload, CancellationToken cancellationToken)
        {
            Payloads.Enqueue(payload); Entered.TrySetResult();
            if (Fail) throw new IOException("模拟写入失败");
            if (hold) await Release.Task.WaitAsync(ignoreCancellation ? CancellationToken.None : cancellationToken);
        }
    }
}
