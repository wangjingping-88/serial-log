using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using SerialLog.App.Infrastructure;
using SerialLog.Core.Commands;
using SerialLog.Core.Configuration;

namespace SerialLog.App.ViewModels;

public sealed class CommandGroupQueueItemViewModel : ObservableObject
{
    private string _name = string.Empty;
    private string _status = "等待执行";
    public string Id { get; }
    public string GroupId { get; }
    public string Name { get => _name; internal set => SetProperty(ref _name, value); }
    public string Status { get => _status; internal set => SetProperty(ref _status, value); }
    public CommandGroupQueueItemViewModel(CommandGroupQueueItemConfig config) { Id = config.Id; GroupId = config.GroupId; }
    public CommandGroupQueueItemConfig ToConfig() => new() { Id = Id, GroupId = GroupId };
}

public sealed record CommandGroupRunResult(bool Success, bool Cancelled, string Message);

/// <summary>只调度所属工作区；任务使用不可变配置和目标快照，同一串口在组间互斥。</summary>
public sealed class CommandGroupSchedulerViewModel : ObservableObject, IDisposable
{
    private sealed class Run(CommandGroupEditorViewModel group, CommandGroupConfig snapshot,
        ICommandTarget[] targets, string[] resources, CancellationTokenSource cancellation)
    {
        public CommandGroupEditorViewModel Group { get; } = group;
        public CommandGroupConfig Snapshot { get; } = snapshot;
        public ICommandTarget[] Targets { get; } = targets;
        public string[] Resources { get; } = resources;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource<CommandGroupRunResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private readonly Func<IReadOnlyList<ICommandTarget>> _targets;
    private readonly Func<string, string?> _externalConflict;
    private readonly Func<ICommandTarget, string> _resourceKey;
    private readonly Action<string> _setStatus;
    private readonly object _gate = new();
    private readonly Dictionary<string, Run> _runs = [];
    private readonly Dictionary<string, string> _owners = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<CommandGroupEditorViewModel> _observed = [];
    private CancellationTokenSource? _queueCancellation;
    private Task _queueTask = Task.CompletedTask;
    private CommandGroupConfig[] _queueSnapshots = [];
    private bool _isQueueRunning;
    private bool _disposed;
    private string _queueStatus = "就绪";
    private CommandGroupEditorViewModel? _selectedGroup;
    private CommandGroupQueueItemViewModel? _selectedQueueItem;

    public CommandGroupSchedulerViewModel(ObservableCollection<CommandGroupEditorViewModel> groups,
        Func<IReadOnlyList<ICommandTarget>> targets, Action<string> setStatus,
        Func<string, string?>? externalConflict = null, Func<ICommandTarget, string>? resourceKey = null)
    {
        Groups = groups; _targets = targets; _setStatus = setStatus;
        _externalConflict = externalConflict ?? (_ => null); _resourceKey = resourceKey ?? (target => target.Id);
        RunGroupCommand = new RelayCommand(p => { if (p is CommandGroupEditorViewModel g) _ = StartGroupAsync(g); }, p => p is CommandGroupEditorViewModel g && CanRun(g));
        RunOnceCommand = new RelayCommand(p => { if (p is CommandGroupEditorViewModel g) _ = StartGroupAsync(g, 1); }, p => p is CommandGroupEditorViewModel g && CanRun(g));
        StopGroupCommand = new RelayCommand(p => { if (p is CommandGroupEditorViewModel g) CancelGroup(g.Id); }, p => p is CommandGroupEditorViewModel { IsRunning: true });
        RunSelectedCommand = new RelayCommand(RunSelected, () => ScopedGroups.Any(g => g.IsSelectedForRun && CanRun(g)));
        StopAllCommand = new RelayCommand(() => { _ = StopAsync(); }, () => HasRunningTasks);
        AddToQueueCommand = new RelayCommand(p => { if (p is CommandGroupEditorViewModel g) AddToQueue(g); },
            p => CanEditQueue && p is CommandGroupEditorViewModel { IsInfiniteLoop: false } g && Groups.Contains(g));
        RunQueueCommand = new RelayCommand(() => { _ = StartQueueAsync(); }, () => CanEditQueue && Queue.Count > 0);
        StopQueueCommand = new RelayCommand(() => _queueCancellation?.Cancel(), () => IsQueueRunning);
        RemoveQueueItemCommand = new RelayCommand(RemoveQueueItem, p => CanEditQueue && p is CommandGroupQueueItemViewModel);
        ClearQueueCommand = new RelayCommand(ClearQueue, () => CanEditQueue && Queue.Count > 0);
        MoveGroupUpCommand = new RelayCommand(() => MoveGroup(-1), () => SelectedGroup is not null && ScopedGroups.ToList().IndexOf(SelectedGroup) > 0);
        MoveGroupDownCommand = new RelayCommand(() => MoveGroup(1), () => SelectedGroup is not null && ScopedGroups.ToList().IndexOf(SelectedGroup) is var index && index >= 0 && index < ScopedGroups.Count - 1);
        SmartGroupCommand = new RelayCommand(SmartGroup, () => !_disposed && ScopedGroups.Count > 1);
        MoveQueueUpCommand = new RelayCommand(() => MoveQueueItem(-1), () => CanEditQueue && SelectedQueueItem is not null && Queue.IndexOf(SelectedQueueItem) > 0);
        MoveQueueDownCommand = new RelayCommand(() => MoveQueueItem(1), () => CanEditQueue && SelectedQueueItem is not null && Queue.IndexOf(SelectedQueueItem) < Queue.Count - 1);
        Groups.CollectionChanged += GroupsChanged; Queue.CollectionChanged += QueueChanged; ObserveGroups();
    }
    public ObservableCollection<CommandGroupEditorViewModel> Groups { get; }
    public Func<IReadOnlyList<CommandGroupEditorViewModel>>? GroupScope { get; set; }
    private IReadOnlyList<CommandGroupEditorViewModel> ScopedGroups => GroupScope?.Invoke() ?? Groups;
    internal void RefreshScope() { OnPropertyChanged(nameof(SelectedGroupPositionText)); RefreshCommands(); }
    public ObservableCollection<CommandGroupQueueItemViewModel> Queue { get; } = [];
    public event EventHandler? ConfigurationChanged;
    public CommandGroupEditorViewModel? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (!SetProperty(ref _selectedGroup, value)) return;
            OnPropertyChanged(nameof(SelectedGroupPositionText)); RefreshCommands();
        }
    }
    public string SelectedGroupPositionText
    {
        get
        {
            var index = SelectedGroup is null ? -1 : ScopedGroups.ToList().IndexOf(SelectedGroup);
            return index >= 0 ? $"第 {index + 1} / {ScopedGroups.Count} 组" : $"共 {ScopedGroups.Count} 组，未选择";
        }
    }
    public CommandGroupQueueItemViewModel? SelectedQueueItem { get => _selectedQueueItem; set { if (SetProperty(ref _selectedQueueItem, value)) RefreshCommands(); } }
    public bool IsQueueRunning { get => _isQueueRunning; private set { if (SetProperty(ref _isQueueRunning, value)) { OnPropertyChanged(nameof(CanEditQueue)); StateChanged(); } } }
    public bool CanEditQueue => !IsQueueRunning && !_disposed;
    public bool HasRunningTasks { get { lock (_gate) return _runs.Count > 0 || IsQueueRunning; } }
    public string QueueStatus { get => _queueStatus; private set => SetProperty(ref _queueStatus, value); }
    public RelayCommand RunGroupCommand { get; }
    public RelayCommand RunOnceCommand { get; }
    public RelayCommand StopGroupCommand { get; }
    public RelayCommand RunSelectedCommand { get; }
    public RelayCommand StopAllCommand { get; }
    public RelayCommand AddToQueueCommand { get; }
    public RelayCommand RunQueueCommand { get; }
    public RelayCommand StopQueueCommand { get; }
    public RelayCommand RemoveQueueItemCommand { get; }
    public RelayCommand ClearQueueCommand { get; }
    public RelayCommand MoveGroupUpCommand { get; }
    public RelayCommand MoveGroupDownCommand { get; }
    public RelayCommand SmartGroupCommand { get; }
    public RelayCommand MoveQueueUpCommand { get; }
    public RelayCommand MoveQueueDownCommand { get; }

    private bool CanRun(CommandGroupEditorViewModel group) => !_disposed && !group.IsRunning &&
        !(IsQueueRunning && _queueSnapshots.Any(g => g.Id == group.Id));
    public bool HasTarget(string id)
    {
        lock (_gate) return _runs.Values.Any(r => r.Snapshot.TargetIds.Contains(id)) ||
            IsQueueRunning && _queueSnapshots.Any(g => g.TargetIds.Contains(id));
    }
    public bool CanDelete(string id) { lock (_gate) return !_runs.ContainsKey(id) && !(IsQueueRunning && _queueSnapshots.Any(g => g.Id == id)); }
    public async Task StopAsync(string? targetId = null)
    {
        Task[] tasks;
        lock (_gate)
        {
            var stopQueue = targetId is null || _queueSnapshots.Any(g => g.TargetIds.Contains(targetId));
            if (stopQueue) _queueCancellation?.Cancel();
            var runs = _runs.Values.Where(r => targetId is null || r.Snapshot.TargetIds.Contains(targetId)).ToArray();
            foreach (var run in runs) run.Cancellation.Cancel();
            tasks = runs.Select(r => (Task)r.Completion.Task).Concat(stopQueue ? [_queueTask] : []).ToArray();
        }
        await Task.WhenAll(tasks);
    }
    private void CancelGroup(string id) { lock (_gate) if (_runs.TryGetValue(id, out var run)) run.Cancellation.Cancel(); }
    private void RunSelected()
    {
        foreach (var group in ScopedGroups.Where(g => g.IsSelectedForRun && !g.IsRunning).ToArray()) _ = StartGroupAsync(group);
    }
    public Task<CommandGroupRunResult> StartGroupAsync(CommandGroupEditorViewModel group, int? repeats = null,
        CancellationToken cancellationToken = default) => StartSnapshot(group, group.ToConfig(), repeats, cancellationToken, false);
    private Task<CommandGroupRunResult> StartSnapshot(CommandGroupEditorViewModel group, CommandGroupConfig snapshot,
        int? repeats, CancellationToken token, bool fromQueue)
    {
        string? error = null;
        Run? run = null;
        var available = _targets().ToDictionary(t => t.Id);
        var targets = snapshot.TargetIds.Distinct().Where(available.ContainsKey).Select(id => available[id]).ToArray();
        var resources = targets.Select(_resourceKey).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        lock (_gate)
        {
            if (_disposed || token.IsCancellationRequested) error = "任务已取消";
            else if (_runs.ContainsKey(group.Id) || !fromQueue && !CanRun(group)) error = "该命令组已经在运行或队列中";
            else if (snapshot.Commands.Count == 0 || snapshot.Commands.Any(string.IsNullOrWhiteSpace)) error = "命令组为空或包含空命令";
            else if (snapshot.TargetIds.Count == 0) error = "请在命令组设置中选择目标窗口";
            else error = snapshot.TargetIds.Select(_externalConflict).FirstOrDefault(reason => reason is not null);
            if (error is null && (targets.Length != snapshot.TargetIds.Distinct().Count() || targets.Any(t => !t.IsConnected)))
                error = "目标不存在、未连接或不可发送";
            if (error is null && resources.FirstOrDefault(_owners.ContainsKey) is { } occupied)
                error = $"串口冲突：{occupied} 正由“{_owners[occupied]}”使用";
            if (error is null)
            {
                run = new Run(group, snapshot, targets, resources, CancellationTokenSource.CreateLinkedTokenSource(token));
                _runs.Add(group.Id, run); foreach (var resource in resources) _owners.Add(resource, snapshot.Name);
                group.IsRunning = true; group.ExecutionStatus = "准备执行";
            }
        }
        if (error is not null)
        {
            if (!group.IsRunning) group.ExecutionStatus = error;
            _setStatus($"{snapshot.Name}：{error}");
            return Task.FromResult(new CommandGroupRunResult(false, token.IsCancellationRequested, error));
        }
        StateChanged();
        _ = ExecuteRunAsync(run!, Math.Max(1, repeats ?? snapshot.LoopCount), !repeats.HasValue && snapshot.IsInfiniteLoop);
        return run!.Completion.Task;
    }
    private async Task ExecuteRunAsync(Run run, int repeats, bool infiniteLoop)
    {
        var rounds = 0; var sent = 0; var failed = 0; var skipped = 0;
        CommandGroupRunResult outcome;
        try
        {
            await Task.Yield();
            var config = run.Snapshot;
            var group = new CommandGroup(config.Name, config.TargetIds.ToArray(), config.Commands.ToArray(), TimeSpan.FromMilliseconds(config.DelayMilliseconds), config.LineEnding, config.InterpretEscapes);
            while (infiniteLoop || rounds < repeats)
            {
                run.Cancellation.Token.ThrowIfCancellationRequested();
                var result = await CommandGroupExecutor.ExecuteAsync(group, run.Targets, run.Cancellation.Token);
                rounds++; sent += result.SentCount; failed += result.FailedCount; skipped += result.SkippedCount;
                run.Cancellation.Token.ThrowIfCancellationRequested();
                run.Group.ExecutionStatus = $"第 {rounds}/{(infiniteLoop ? "无限" : repeats.ToString())} 轮 · 成功 {sent} · 失败 {failed} · 跳过 {skipped}";
                if (failed > 0 || skipped > 0) break;
                if (infiniteLoop || rounds < repeats)
                {
                    if (config.LoopIntervalMilliseconds > 0) await Task.Delay(config.LoopIntervalMilliseconds, run.Cancellation.Token);
                    else await Task.Yield();
                }
            }
            outcome = new(failed == 0 && skipped == 0, false, $"{(failed == 0 && skipped == 0 ? "完成" : "异常停止")}：{rounds} 轮，成功 {sent}，失败 {failed}，跳过 {skipped}");
        }
        catch (OperationCanceledException) { outcome = new(false, true, $"已停止：{rounds} 轮，成功 {sent}"); }
        catch (Exception e) { outcome = new(false, false, $"执行失败：{e.Message}"); }
        finally
        {
            lock (_gate)
            {
                _runs.Remove(run.Group.Id); foreach (var resource in run.Resources) _owners.Remove(resource);
                run.Group.IsRunning = false; run.Cancellation.Dispose();
            }
        }
        run.Group.ExecutionStatus = outcome.Message; _setStatus($"{run.Snapshot.Name}：{outcome.Message}");
        StateChanged(); run.Completion.TrySetResult(outcome);
    }
    public bool AddToQueue(CommandGroupEditorViewModel group)
    {
        if (!CanEditQueue || !Groups.Contains(group)) return false;
        if (group.IsInfiniteLoop) { _setStatus("无限循环组不能加入队列，请先取消“无限循环”。"); return false; }
        var item = new CommandGroupQueueItemViewModel(new() { GroupId = group.Id }) { Name = group.Name };
        Queue.Add(item); SelectedQueueItem = item; return true;
    }
    public void LoadQueue(IEnumerable<CommandGroupQueueItemConfig> queue)
    {
        if (IsQueueRunning) throw new InvalidOperationException("队列运行中不能替换配置。");
        Queue.Clear(); foreach (var config in queue) Queue.Add(new(config) { Name = Groups.FirstOrDefault(g => g.Id == config.GroupId)?.Name ?? "命令组已删除" });
        SelectedQueueItem = Queue.FirstOrDefault();
    }
    public Task StartQueueAsync()
    {
        if (!CanEditQueue || Queue.Count == 0) return Task.CompletedTask;
        var entries = Queue.ToArray();
        var groups = entries.Select(e => Groups.FirstOrDefault(g => g.Id == e.GroupId)).ToArray();
        if (groups.Any(g => g is null || g.IsInfiniteLoop)) { QueueStatus = "队列包含已删除或无限循环的命令组，请先调整。"; return Task.CompletedTask; }
        _queueSnapshots = groups.Select(g => g!.ToConfig()).ToArray();
        foreach (var entry in entries) entry.Status = "等待执行";
        _queueCancellation = new(); IsQueueRunning = true;
        _queueTask = RunQueueAsync(entries, groups.Select(g => g!).ToArray(), _queueSnapshots, _queueCancellation);
        return _queueTask;
    }
    private async Task RunQueueAsync(CommandGroupQueueItemViewModel[] entries, CommandGroupEditorViewModel[] groups,
        CommandGroupConfig[] snapshots, CancellationTokenSource cancellation)
    {
        var completed = 0;
        try
        {
            await Task.Yield();
            for (var i = 0; i < entries.Length; i++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                entries[i].Status = "执行中"; QueueStatus = $"执行 {i + 1}/{entries.Length}：{snapshots[i].Name}";
                var result = await StartSnapshot(groups[i], snapshots[i], null, cancellation.Token, true);
                entries[i].Status = result.Message;
                if (!result.Success) { QueueStatus = $"队列停止于第 {i + 1} 项：{result.Message}"; return; }
                completed++;
            }
            QueueStatus = $"队列完成：{completed}/{entries.Length} 项";
        }
        catch (OperationCanceledException) { QueueStatus = $"队列已停止：完成 {completed}/{entries.Length} 项"; }
        catch (Exception e) { QueueStatus = $"队列异常：{e.Message}"; }
        finally
        {
            foreach (var entry in entries.Where(e => e.Status == "等待执行")) entry.Status = "未执行";
            _queueSnapshots = []; _queueCancellation = null; cancellation.Dispose(); IsQueueRunning = false; _setStatus(QueueStatus);
        }
    }
    private void MoveGroup(int offset)
    {
        if (SelectedGroup is null) return;
        var selected = SelectedGroup;
        var scope = ScopedGroups.ToArray(); var index = Array.IndexOf(scope, selected); var next = index + offset;
        if (index >= 0 && next >= 0 && next < scope.Length)
        {
            Groups.Move(Groups.IndexOf(selected), Groups.IndexOf(scope[next]));
            SelectedGroup = selected;
        }
    }
    private void SmartGroup()
    {
        if (_disposed || ScopedGroups.Count < 2) return;
        var selected = SelectedGroup;
        var scoped = ScopedGroups.ToArray();
        var positions = scoped.Select(group => Groups.IndexOf(group)).Order().ToArray();
        // 以稳定窗口 ID 的完整集合分桶，忽略勾选顺序和名称；长度前缀避免键碰撞。
        var buckets = scoped.GroupBy(group => string.Concat(group.Targets.Where(t => t.IsSelected)
            .Select(t => t.TargetId).Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal)
            .Select(id => $"{id.Length}:{id}")), StringComparer.Ordinal).ToArray();
        var ordered = buckets.OrderBy(bucket => bucket.Key.Length == 0 ? 1 : 0).SelectMany(bucket => bucket).ToArray();
        var fullOrder = Groups.ToArray();
        for (var index = 0; index < ordered.Length; index++) fullOrder[positions[index]] = ordered[index];
        for (var index = 0; index < fullOrder.Length; index++)
        {
            var previous = Groups.IndexOf(fullOrder[index]);
            if (previous != index) Groups.Move(previous, index);
        }
        SelectedGroup = selected;
        _setStatus($"已在当前列表按目标窗口整理为 {buckets.Length} 类，相同目标相邻，未选目标的组置后；其他列表和队列顺序不变。");
    }
    private void MoveQueueItem(int offset)
    {
        if (!CanEditQueue || SelectedQueueItem is null) return;
        var index = Queue.IndexOf(SelectedQueueItem); var next = index + offset;
        if (index >= 0 && next >= 0 && next < Queue.Count) Queue.Move(index, next);
    }
    private void RemoveQueueItem(object? item) { if (CanEditQueue && item is CommandGroupQueueItemViewModel entry) Queue.Remove(entry); }
    private void ClearQueue()
    {
        if (!CanEditQueue || Queue.Count == 0) return;
        Queue.Clear(); SelectedQueueItem = null;
        QueueStatus = "队列已清空"; _setStatus(QueueStatus);
    }
    private void QueueChanged(object? sender, NotifyCollectionChangedEventArgs e) { RefreshCommands(); ConfigurationChanged?.Invoke(this, EventArgs.Empty); }
    private void GroupsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ObserveGroups();
        if (!IsQueueRunning) foreach (var entry in Queue.Where(q => !Groups.Any(g => g.Id == q.GroupId)).ToArray()) Queue.Remove(entry);
        OnPropertyChanged(nameof(SelectedGroupPositionText));
        RefreshCommands(); ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }
    private void ObserveGroups()
    {
        foreach (var group in _observed) { group.PropertyChanged -= GroupChanged; group.Commands.CollectionChanged -= CommandsChanged; }
        _observed.Clear();
        foreach (var group in Groups) { group.PropertyChanged += GroupChanged; group.Commands.CollectionChanged += CommandsChanged; _observed.Add(group); group.RefreshSummary(); }
    }
    private void GroupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not CommandGroupEditorViewModel group) return;
        if (e.PropertyName is nameof(CommandGroupEditorViewModel.IsRunning) or nameof(CommandGroupEditorViewModel.ExecutionStatus) or nameof(CommandGroupEditorViewModel.Summary) or nameof(CommandGroupEditorViewModel.QueueAdmissionToolTip) or nameof(CommandGroupEditorViewModel.IsLoopCountEnabled)) return;
        RefreshCommands();
        if (e.PropertyName == nameof(CommandGroupEditorViewModel.IsSelectedForRun)) return;
        group.RefreshSummary();
        foreach (var entry in Queue.Where(q => q.GroupId == group.Id)) entry.Name = group.Name;
        ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }
    private void CommandsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (var group in Groups) group.RefreshSummary(); ConfigurationChanged?.Invoke(this, EventArgs.Empty);
    }
    private void StateChanged() { OnPropertyChanged(nameof(HasRunningTasks)); RefreshCommands(); }
    private void RefreshCommands()
    {
        foreach (var command in new[] { RunGroupCommand, RunOnceCommand, StopGroupCommand, RunSelectedCommand, StopAllCommand, AddToQueueCommand, RunQueueCommand, StopQueueCommand, RemoveQueueItemCommand, ClearQueueCommand, MoveGroupUpCommand, MoveGroupDownCommand, SmartGroupCommand, MoveQueueUpCommand, MoveQueueDownCommand }) command.RaiseCanExecuteChanged();
    }
    public void Dispose()
    {
        _disposed = true; _queueCancellation?.Cancel();
        lock (_gate) foreach (var run in _runs.Values.ToArray()) run.Cancellation.Cancel();
        Groups.CollectionChanged -= GroupsChanged; Queue.CollectionChanged -= QueueChanged;
        foreach (var group in _observed) { group.PropertyChanged -= GroupChanged; group.Commands.CollectionChanged -= CommandsChanged; }
    }
}
