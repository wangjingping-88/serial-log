using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;
using SerialLog.App.Infrastructure;
using SerialLog.Core.Commands;
using SerialLog.Core.Configuration;
using SerialLog.App.Views;

namespace SerialLog.App.ViewModels;

public sealed partial class CommandPanelViewModel : ObservableObject, IDisposable
{
    private readonly Action<string> _setStatus;
    private readonly Func<string, string, bool> _confirmDelete;
    private readonly Func<string, string?> _editImportedAtCommand;
    private readonly Func<string, string?> _editCommandInGroup;
    private readonly Func<string, Task> _copyCommandText;
    private readonly StaClipboardTextService _commandClipboard = new();
    private bool _isCommandCopyInProgress;
    private string _commandText = string.Empty;
    private string? _selectedHistoryCommand;
    private LineEnding _selectedLineEnding = LineEnding.CrLf;
    private int _singleCommandLoopIntervalMilliseconds = 1000;
    private int _singleCommandLoopCount;
    private bool _singleCommandInfiniteLoop;
    private bool _isSingleCommandLoopRunning;
    private bool _interpretCommandEscapes = true;
    public bool InterpretCommandEscapes { get => _interpretCommandEscapes; set => SetProperty(ref _interpretCommandEscapes, value); }
    private int _selectedCommandPanelTabIndex;
    private CommandGroupEditorViewModel? _selectedCommandGroup;
    private AtCommandSetViewModel? _selectedAtCommandSet;
    private string? _selectedAtCommand;
    private string _statusText = string.Empty;
    private CancellationTokenSource? _singleCommandLoopCts;
    private Task _singleLoopTask = Task.CompletedTask;
    private IReadOnlyList<string> _singleLoopTargets = [];
    public CommandGroupSchedulerViewModel GroupScheduler { get; }
    internal Func<string, bool>? GroupDeletionGuard { get; set; }
    internal bool CanDeleteCommandGroup(string id) => GroupScheduler.CanDelete(id) && (GroupDeletionGuard?.Invoke(id) ?? true);
    public bool IsCommandPanelVerticalShape => false;
    public Func<IReadOnlyList<CommandGroupImportSource>> ImportSources { get; set; } = () => [];
    public bool HasRunningLoopForTarget(string targetId) =>
        IsSingleCommandLoopRunning && _singleLoopTargets.Contains(targetId) ||
        GroupScheduler.HasTarget(targetId);

    public async Task StopLoopsAsync(string? targetId = null)
    {
        var tasks = new List<Task>();
        if (targetId is null || _singleLoopTargets.Contains(targetId))
        {
            _singleCommandLoopCts?.Cancel();
            tasks.Add(_singleLoopTask);
        }
        tasks.Add(GroupScheduler.StopAsync(targetId));
        await Task.WhenAll(tasks);
    }
    private readonly ObservableCollection<string> _emptyImportedAtCommands = [];
    private readonly HashSet<SerialWindowViewModel> _observedSerialWindows = [];
    private readonly HashSet<TargetSelectionViewModel> _observedCommandGroupTargets = [];

    public CommandPanelViewModel(
        ObservableCollection<SerialWindowViewModel> serialWindows,
        Action<string> setStatus,
        Func<string, string, bool>? confirmDelete = null,
        Func<string, string?>? editImportedAtCommand = null,
        Func<string, string?>? editCommandInGroup = null,
        Func<string, Task>? copyCommandText = null)
    {
        SerialWindows = serialWindows;
        _setStatus = setStatus;
        _confirmDelete = confirmDelete ?? ConfirmDeleteWithDialog;
        _editImportedAtCommand = editImportedAtCommand ?? EditImportedAtCommandWithDialog;
        _editCommandInGroup = editCommandInGroup ?? (command => EditCommandWithDialog(command, "编辑命令组中的命令", "保存后替换选中的命令，不会新增条目。"));
        _copyCommandText = copyCommandText ?? _commandClipboard.SetTextAsync;
        GroupScheduler = new(CommandGroups, () => SerialWindows.Where(w => w.CanSendCommands).Cast<ICommandTarget>().ToArray(), SetStatus,
            id => SerialWindows.Any(w => w.Id == id && w.IsFirmwareBusy) ? "目标正在烧录" :
                IsSingleCommandLoopRunning && _singleLoopTargets.Contains(id) ? "目标被单条命令循环占用" : null,
            target => target is SerialWindowViewModel window ? window.CommandResourceKey : target.Id);
        GroupScheduler.ConfigurationChanged += (_, _) =>
        { OnPropertyChanged(nameof(CommandGroups)); ClearCommandGroupsCommand?.RaiseCanExecuteChanged(); };
        GroupScheduler.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CommandGroupSchedulerViewModel.HasRunningTasks))
            { OnPropertyChanged(nameof(IsCommandGroupLoopRunning)); OnPropertyChanged(nameof(CommandGroupLoopActionText)); DeleteCommandGroupCommand?.RaiseCanExecuteChanged(); ClearCommandGroupsCommand?.RaiseCanExecuteChanged(); }
            if (e.PropertyName == nameof(CommandGroupSchedulerViewModel.SelectedGroup)) SelectedCommandGroup = GroupScheduler.SelectedGroup;
        };
        CommandGroupSettingsCommand = new RelayCommand(OpenCommandGroupSettings);

        SendCommand = new AsyncRelayCommand(SendSingleCommandAsync);
        ToggleSingleCommandLoopCommand = new RelayCommand(ToggleSingleCommandLoop);
        AddCommandGroupCommand = new RelayCommand(AddCommandGroup);
        DuplicateCommandGroupCommand = new RelayCommand(DuplicateCommandGroup, () => SelectedCommandGroup is not null);
        DeleteCommandGroupCommand = new RelayCommand(DeleteCommandGroup,
            () => SelectedCommandGroup is { } group && CanDeleteCommandGroup(group.Id));
        ClearCommandGroupsCommand = new RelayCommand(ClearCommandGroups,
            () => VisibleCommandGroups.Any(group => CanEditCommandGroupList || CanDeleteCommandGroup(group.Id)));
        AddCommandToGroupCommand = new RelayCommand(AddCommandToGroup, () => SelectedCommandGroup is not null);
        RemoveCommandFromGroupCommand = new RelayCommand(RemoveCommandFromGroup, CanOperateGroupCommand);
        EditCommandInGroupCommand = new RelayCommand(EditCommandInGroup, CanOperateGroupCommand);
        CopyCommandFromGroupCommand = new RelayCommand(async parameter => await CopyCommandFromGroupAsync(parameter),
            parameter => !_isCommandCopyInProgress && CanOperateGroupCommand(parameter));
        ClearCommandHistoryCommand = new RelayCommand(ClearCommandHistory);
        RemoveCommandHistoryItemCommand = new RelayCommand(RemoveCommandHistoryItem);
        FillSingleCommandFromHistoryCommand = new RelayCommand(
            FillSingleCommandFromSelectedHistory,
            () => !string.IsNullOrWhiteSpace(SelectedHistoryCommand));
        AddHistoryCommandToGroupCommand = new RelayCommand(
            AddSelectedHistoryCommandToGroup,
            () => SelectedCommandGroup is not null && !string.IsNullOrWhiteSpace(SelectedHistoryCommand));
        RemoveImportedAtCommandCommand = new RelayCommand(RemoveImportedAtCommand);
        EditImportedAtCommandCommand = new RelayCommand(EditImportedAtCommand);
        AddAtCommandSetCommand = new RelayCommand(AddAtCommandSet);
        DeleteAtCommandSetCommand = new RelayCommand(DeleteAtCommandSet, () => ImportedAtCommandSets.Count > 1);
        ExecuteCommandGroupCommand = new AsyncRelayCommand(ExecuteSelectedCommandGroupAsync, () => SelectedCommandGroup is not null);
        ToggleCommandGroupLoopCommand = new RelayCommand(ToggleCommandGroupLoop, () => SelectedCommandGroup is not null);
        ImportAtFileCommand = new RelayCommand(ImportAtFile);
        AppendAtFileCommand = new RelayCommand(AppendAtFile);
        ImportAtFromLogCommand = new RelayCommand(ImportAtFromLog);
        CustomAtImportCommand = new RelayCommand(ImportCustomAtCommands);
        FillSingleCommandFromAtCommandCommand = new RelayCommand(
            FillSingleCommandFromSelectedAtCommand,
            () => !string.IsNullOrWhiteSpace(SelectedAtCommand));
        AddAtCommandToGroupCommand = new RelayCommand(
            AddSelectedAtCommandToGroup,
            () => SelectedCommandGroup is not null && !string.IsNullOrWhiteSpace(SelectedAtCommand));

        ImportedAtCommandSets.Add(new AtCommandSetViewModel("默认"));
        SelectedAtCommandSet = ImportedAtCommandSets[0];

        SerialWindows.CollectionChanged += SerialWindows_CollectionChanged;
        RefreshObservedSerialWindows();
        InitializeCommandGroupLists();
    }

    public ObservableCollection<SerialWindowViewModel> SerialWindows { get; }

    public ObservableCollection<string> CommandHistory { get; } = [];

    public ObservableCollection<AtCommandSetViewModel> ImportedAtCommandSets { get; } = [];

    public ObservableCollection<string> ImportedAtCommands => SelectedAtCommandSet?.Commands ?? _emptyImportedAtCommands;

    public ObservableCollection<CommandGroupEditorViewModel> CommandGroups { get; } = [];

    public IReadOnlyList<LineEnding> LineEndingOptions { get; } =
        [LineEnding.None, LineEnding.Cr, LineEnding.Lf, LineEnding.CrLf];

    public AsyncRelayCommand SendCommand { get; }

    public RelayCommand ToggleSingleCommandLoopCommand { get; }
    public RelayCommand CommandGroupSettingsCommand { get; }

    public RelayCommand AddCommandGroupCommand { get; }

    public RelayCommand DuplicateCommandGroupCommand { get; }

    public RelayCommand DeleteCommandGroupCommand { get; }
    public RelayCommand ClearCommandGroupsCommand { get; }

    public RelayCommand AddCommandToGroupCommand { get; }

    public RelayCommand RemoveCommandFromGroupCommand { get; }
    public RelayCommand EditCommandInGroupCommand { get; }
    public RelayCommand CopyCommandFromGroupCommand { get; }

    public RelayCommand ClearCommandHistoryCommand { get; }

    public RelayCommand RemoveCommandHistoryItemCommand { get; }

    public RelayCommand FillSingleCommandFromHistoryCommand { get; }

    public RelayCommand AddHistoryCommandToGroupCommand { get; }

    public RelayCommand RemoveImportedAtCommandCommand { get; }

    public RelayCommand EditImportedAtCommandCommand { get; }

    public RelayCommand AddAtCommandSetCommand { get; }

    public RelayCommand DeleteAtCommandSetCommand { get; }

    public AsyncRelayCommand ExecuteCommandGroupCommand { get; }

    public RelayCommand ToggleCommandGroupLoopCommand { get; }

    public RelayCommand ImportAtFileCommand { get; }

    public RelayCommand AppendAtFileCommand { get; }

    public RelayCommand ImportAtFromLogCommand { get; }

    public RelayCommand CustomAtImportCommand { get; }

    public RelayCommand FillSingleCommandFromAtCommandCommand { get; }

    public RelayCommand AddAtCommandToGroupCommand { get; }

    public int SelectedCommandPanelTabIndex
    {
        get => _selectedCommandPanelTabIndex;
        set
        {
            var clamped = Math.Clamp(value, 0, 2);
            if (SetProperty(ref _selectedCommandPanelTabIndex, clamped))
            {
                OnPropertyChanged(nameof(IsSingleCommandTabSelected));
                OnPropertyChanged(nameof(IsCommandGroupTabSelected));
                OnPropertyChanged(nameof(IsYmodemTabSelected));
                OnPropertyChanged(nameof(IsAllSingleCommandTargetsSelected));
                OnPropertyChanged(nameof(IsAllCommandGroupTargetsSelected));
                RemoveCurrentListMemberCommand?.RaiseCanExecuteChanged();
                AddHistoryCommandToGroupCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsSingleCommandTabSelected => SelectedCommandPanelTabIndex == 0;

    public bool IsCommandGroupTabSelected => SelectedCommandPanelTabIndex == 1;
    public bool IsYmodemTabSelected => SelectedCommandPanelTabIndex == 2;

    public string CommandText
    {
        get => _commandText;
        set => SetProperty(ref _commandText, value);
    }

    public string? SelectedHistoryCommand
    {
        get => _selectedHistoryCommand;
        set
        {
            if (SetProperty(ref _selectedHistoryCommand, value) && !string.IsNullOrWhiteSpace(value))
            {
                ApplySelectedHistoryCommand(value);
            }

            FillSingleCommandFromHistoryCommand.RaiseCanExecuteChanged();
            AddHistoryCommandToGroupCommand.RaiseCanExecuteChanged();
        }
    }

    public LineEnding SelectedLineEnding
    {
        get => _selectedLineEnding;
        set => SetProperty(ref _selectedLineEnding, value);
    }

    public int SingleCommandLoopIntervalMilliseconds
    {
        get => _singleCommandLoopIntervalMilliseconds;
        set => SetProperty(ref _singleCommandLoopIntervalMilliseconds, Math.Max(0, value));
    }

    public int SingleCommandLoopCount
    {
        get => _singleCommandLoopCount;
        set => SetProperty(ref _singleCommandLoopCount, Math.Max(0, value));
    }

    public bool SingleCommandInfiniteLoop
    {
        get => _singleCommandInfiniteLoop;
        set
        {
            if (SetProperty(ref _singleCommandInfiniteLoop, value))
                OnPropertyChanged(nameof(IsSingleCommandLoopCountEnabled));
        }
    }

    public bool IsSingleCommandLoopCountEnabled => !SingleCommandInfiniteLoop;

    public bool IsSingleCommandLoopRunning
    {
        get => _isSingleCommandLoopRunning;
        private set
        {
            if (SetProperty(ref _isSingleCommandLoopRunning, value))
            {
                OnPropertyChanged(nameof(SingleCommandLoopActionText));
            }
        }
    }

    public bool IsCommandGroupLoopRunning => GroupScheduler.HasRunningTasks;

    public string SingleCommandLoopActionText => IsSingleCommandLoopRunning ? "停止循环" : "循环";

    public string CommandGroupLoopActionText => SelectedCommandGroup?.IsRunning == true ? "停止循环" : "循环";

    public CommandGroupEditorViewModel? SelectedCommandGroup
    {
        get => _selectedCommandGroup;
        set
        {
            if (SetProperty(ref _selectedCommandGroup, value))
            {
                GroupScheduler.SelectedGroup = value;
                DuplicateCommandGroupCommand.RaiseCanExecuteChanged();
                DeleteCommandGroupCommand.RaiseCanExecuteChanged();
                AddCommandToGroupCommand.RaiseCanExecuteChanged();
                RemoveCommandFromGroupCommand.RaiseCanExecuteChanged();
                EditCommandInGroupCommand.RaiseCanExecuteChanged();
                CopyCommandFromGroupCommand.RaiseCanExecuteChanged();
                AddAtCommandToGroupCommand.RaiseCanExecuteChanged();
                AddHistoryCommandToGroupCommand.RaiseCanExecuteChanged();
                ExecuteCommandGroupCommand.RaiseCanExecuteChanged();
                ToggleCommandGroupLoopCommand.RaiseCanExecuteChanged();
                RemoveCurrentListMemberCommand?.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(IsAllCommandGroupTargetsSelected));
            }
        }
    }

    public AtCommandSetViewModel SelectedAtCommandSet
    {
        get => _selectedAtCommandSet ?? ImportedAtCommandSets.First();
        set
        {
            if (value is null)
            {
                return;
            }

            if (SetProperty(ref _selectedAtCommandSet, value))
            {
                OnPropertyChanged(nameof(ImportedAtCommands));
                OnPropertyChanged(nameof(SelectedAtCommandSetName));
                SelectedAtCommand = ImportedAtCommands.FirstOrDefault();
                DeleteAtCommandSetCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string? SelectedAtCommand
    {
        get => _selectedAtCommand;
        set
        {
            if (SetProperty(ref _selectedAtCommand, value))
            {
                FillSingleCommandFromAtCommandCommand.RaiseCanExecuteChanged();
                AddAtCommandToGroupCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsAllSingleCommandTargetsSelected
    {
        get
        {
            var targets = SerialWindows.Where(window => window.CanSendCommands && (!IsYmodemTabSelected || !window.IsRemote)).ToArray();
            return targets.Length > 0 && targets.All(window => window.IsSelectedForSend);
        }
        set
        {
            foreach (var window in SerialWindows.Where(window => window.CanSendCommands && (!IsYmodemTabSelected || !window.IsRemote)))
            {
                window.IsSelectedForSend = value;
            }

            OnPropertyChanged();
        }
    }

    public bool IsAllCommandGroupTargetsSelected
    {
        get
        {
            var targets = SelectedCommandGroup?.Targets;
            return targets is { Count: > 0 } && targets.All(target => target.IsSelected);
        }
        set
        {
            if (SelectedCommandGroup is null)
            {
                return;
            }

            foreach (var target in SelectedCommandGroup.Targets)
            {
                target.IsSelected = value;
            }

            OnPropertyChanged();
        }
    }

    public void SyncCommandGroupTargets()
    {
        foreach (var group in CommandGroups)
        {
            SyncCommandGroupTargets(group);
        }

        RefreshObservedCommandGroupTargets();
        OnPropertyChanged(nameof(IsAllCommandGroupTargetsSelected));
    }

    public void SyncCommandGroupTargets(CommandGroupEditorViewModel group)
    {
        var selected = group.Targets.Count == 0
            ? SerialWindows.Where(window => group.WasTargetSelected(window.Id)).Select(window => window.Id).ToHashSet()
            : group.ToConfig().TargetIds.ToHashSet();
        group.Targets.Clear();
        foreach (var window in SerialWindows.Where(window => !window.IsRemote || window.CanSendCommands))
        {
            group.Targets.Add(new TargetSelectionViewModel(window.Id, window.Title, selected.Contains(window.Id)));
        }

        RefreshObservedCommandGroupTargets();
        OnPropertyChanged(nameof(IsAllCommandGroupTargetsSelected));
        group.RefreshSummary();
    }

    public void ApplyCommandToActiveEditor(string command)
    {
        if (!string.IsNullOrWhiteSpace(command))
        {
            ApplySelectedHistoryCommand(command);
        }
    }

    public void MoveSelectedCommandInGroup(int sourceIndex, int targetIndex)
    {
        if (SelectedCommandGroup is null)
        {
            return;
        }

        var commands = SelectedCommandGroup.Commands;
        if (sourceIndex < 0 ||
            targetIndex < 0 ||
            sourceIndex >= commands.Count ||
            targetIndex >= commands.Count ||
            sourceIndex == targetIndex)
        {
            return;
        }

        commands.Move(sourceIndex, targetIndex);
        SetStatus($"已调整命令顺序：{sourceIndex + 1} -> {targetIndex + 1}");
    }

    private async Task SendSingleCommandAsync()
    {
        if (string.IsNullOrWhiteSpace(CommandText))
        {
            SetStatus("请输入命令");
            return;
        }

        var payload = CommandFormatter.ApplyLineEnding(CommandText.Trim(), SelectedLineEnding, InterpretCommandEscapes);
        var targets = SerialWindows
            .Where(window => window.CanSendCommands && window.IsSelectedForSend)
            .ToArray();
        var sent = 0;
        var skipped = 0;

        foreach (var target in targets)
        {
            if (!target.IsConnected || GroupScheduler.HasTarget(target.Id))
            {
                skipped++;
                continue;
            }

            try
            {
                await target.SendAsync(payload, CancellationToken.None);
                sent++;
            }
            catch
            {
                skipped++;
            }
        }

        AddHistory(CommandText.Trim());
        SetStatus($"单条发送完成：成功 {sent}，跳过 {skipped}");
    }

    private async Task ExecuteSelectedCommandGroupAsync()
    {
        if (SelectedCommandGroup is { } group) await GroupScheduler.StartGroupAsync(group, 1);
    }

    private void ToggleSingleCommandLoop()
    {
        if (IsSingleCommandLoopRunning)
        {
            _singleCommandLoopCts?.Cancel();
            return;
        }

        if (string.IsNullOrWhiteSpace(CommandText))
        {
            SetStatus("请输入命令");
            return;
        }

        var targetIds = SerialWindows
            .Where(window => window.CanSendCommands && window.IsSelectedForSend)
            .Select(window => window.Id)
            .ToArray();
        if (targetIds.Length == 0)
        {
            SetStatus("请选择目标窗口");
            return;
        }

        if (targetIds.Any(GroupScheduler.HasTarget))
        { SetStatus("目标被命令组或队列占用，请先停止相关任务。"); return; }

        _singleCommandLoopCts = new CancellationTokenSource();
        IsSingleCommandLoopRunning = true;
        AddHistory(CommandText.Trim());
        _singleLoopTargets = targetIds;
        _singleLoopTask = RunSingleCommandLoopAsync(
            CommandText.Trim(),
            SelectedLineEnding,
            targetIds,
            SingleCommandLoopCount,
            SingleCommandInfiniteLoop,
            TimeSpan.FromMilliseconds(SingleCommandLoopIntervalMilliseconds),
            _singleCommandLoopCts);
    }

    private void ToggleCommandGroupLoop()
    {
        if (SelectedCommandGroup is not { } group) return;
        if (group.IsRunning) GroupScheduler.StopGroupCommand.Execute(group);
        else _ = GroupScheduler.StartGroupAsync(group);
    }

    private async Task RunSingleCommandLoopAsync(
        string command,
        LineEnding lineEnding,
        IReadOnlyList<string> targetIds,
        int loopCount,
        bool infiniteLoop,
        TimeSpan loopDelay,
        CancellationTokenSource cancellationTokenSource)
    {
        var group = new CommandGroup("单条命令循环", targetIds, [command], TimeSpan.Zero, lineEnding, InterpretCommandEscapes);
        await RunLoopAsync(
            "单条循环",
            group,
            loopCount,
            infiniteLoop,
            loopDelay,
            cancellationTokenSource,
            () => IsSingleCommandLoopRunning = false,
            cts =>
            {
                if (ReferenceEquals(_singleCommandLoopCts, cts))
                {
                    _singleCommandLoopCts = null;
                }
            });
    }


    private async Task RunLoopAsync(
        string label,
        CommandGroup group,
        int loopCount,
        bool infiniteLoop,
        TimeSpan loopDelay,
        CancellationTokenSource cancellationTokenSource,
        Action markStopped,
        Action<CancellationTokenSource> clearSource)
    {
        var token = cancellationTokenSource.Token;
        var completedRounds = 0;
        var sent = 0;
        var skipped = 0;
        var failed = 0;
        loopCount = Math.Max(1, loopCount);
        var loopCountText = infiniteLoop ? "无限" : loopCount.ToString();

        try
        {
            while (infiniteLoop || completedRounds < loopCount)
            {
                token.ThrowIfCancellationRequested();
                var result = await CommandGroupExecutor.ExecuteAsync(
                    group,
                    SerialWindows.Where(window => window.CanSendCommands).ToArray(),
                    token);
                completedRounds++;
                sent += result.SentCount;
                skipped += result.SkippedCount;
                failed += result.FailedCount;
                SetStatus($"{label}中：第 {completedRounds}/{loopCountText} 轮，成功 {sent}，跳过 {skipped}，失败 {failed}");

                if ((infiniteLoop || completedRounds < loopCount) && loopDelay > TimeSpan.Zero)
                {
                    await Task.Delay(loopDelay, token);
                }
                else if (infiniteLoop || completedRounds < loopCount) await Task.Yield();
            }

            SetStatus($"{label}完成：共 {completedRounds} 轮，成功 {sent}，跳过 {skipped}，失败 {failed}");
        }
        catch (OperationCanceledException)
        {
            SetStatus($"{label}已停止：已执行 {completedRounds} 轮，成功 {sent}，跳过 {skipped}，失败 {failed}");
        }
        catch (Exception ex)
        {
            SetStatus($"{label}异常停止：已执行 {completedRounds} 轮，成功 {sent}，跳过 {skipped}，失败 {failed}，{ex.Message}");
        }
        finally
        {
            markStopped();
            clearSource(cancellationTokenSource);
            cancellationTokenSource.Dispose();
        }
    }

    private void AddHistory(string command)
    {
        if (CommandHistory.Contains(command))
        {
            CommandHistory.Remove(command);
        }

        CommandHistory.Insert(0, command);
        while (CommandHistory.Count > 100)
        {
            CommandHistory.RemoveAt(CommandHistory.Count - 1);
        }
    }

    private void ClearCommandHistory()
    {
        CommandHistory.Clear();
        SetStatus("发送历史已清空");
    }

    private void RemoveCommandHistoryItem(object? parameter)
    {
        if (parameter is not string command)
        {
            return;
        }

        var index = CommandHistory.IndexOf(command);
        if (index < 0)
        {
            return;
        }

        if (string.Equals(SelectedHistoryCommand, command, StringComparison.Ordinal))
        {
            SelectedHistoryCommand = null;
        }

        CommandHistory.RemoveAt(index);
        SetStatus($"已删除历史命令：{command}");
    }

    private void ApplySelectedHistoryCommand(string command)
    {
        if (IsCommandGroupTabSelected && SelectedCommandGroup is not null)
        {
            SelectedCommandGroup.NewCommand = command;
            return;
        }

        CommandText = command;
    }

    private void FillSingleCommandFromSelectedHistory()
    {
        if (string.IsNullOrWhiteSpace(SelectedHistoryCommand))
        {
            return;
        }

        CommandText = SelectedHistoryCommand;
        SelectedCommandPanelTabIndex = 0;
        SetStatus("已填入单条命令编辑框");
    }

    private void AddSelectedHistoryCommandToGroup()
    {
        if (SelectedCommandGroup is null || string.IsNullOrWhiteSpace(SelectedHistoryCommand))
        {
            return;
        }

        SelectedCommandGroup.NewCommand = SelectedHistoryCommand;
        SelectedCommandPanelTabIndex = 1;
        SetStatus("已填入命令组编辑框，修改参数后点击“加入命令”");
    }

    private void AddCommandGroup()
    {
        GroupListSearchText = string.Empty;
        var group = new CommandGroupEditorViewModel { Name = UniqueGroupName($"命令组{CommandGroups.Count + 1}") };
        group.ListIds.Clear(); foreach (var id in NewGroupListIds) group.ListIds.Add(id);
        CommandGroups.Add(group);
        SyncCommandGroupTargets(group);
        SelectedCommandGroup = group;
    }

    private void DuplicateCommandGroup()
    {
        if (SelectedCommandGroup is null)
        {
            return;
        }

        GroupListSearchText = string.Empty;
        var config = SelectedCommandGroup.ToConfig();
        config.Id = Guid.NewGuid().ToString("N"); config.Name = UniqueGroupName(config.Name + " 副本");
        var copy = new CommandGroupEditorViewModel(config);
        CommandGroups.Add(copy);
        SyncCommandGroupTargets(copy);
        SelectedCommandGroup = copy;
    }

    private void OpenCommandGroupSettings()
    {
        var window = new CommandGroupSettingsWindow(this) { Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow };
        if (window.ShowDialog() == true) ApplyCommandGroupSettings(window.Result, window.SelectedGroupId, window.ListsResult, window.SelectedListId);
    }
    public bool CanApplyCommandGroupSettings(IReadOnlyList<CommandGroupConfig> configs, out string reason,
        IReadOnlyList<CommandGroupListConfig>? lists = null, string? selectedListId = null)
    {
        lists ??= CommandGroupLists.Select(list => list.ToConfig()).ToArray();
        selectedListId ??= SelectedCommandGroupList?.Id ?? string.Empty;
        var listIds = lists.Select(list => list.Id).ToHashSet(StringComparer.Ordinal);
        if (lists.Any(list => !Guid.TryParseExact(list.Id, "N", out _) || string.IsNullOrWhiteSpace(list.Name)) ||
            listIds.Count != lists.Count || lists.Select(list => list.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != lists.Count ||
            selectedListId.Length > 0 && !listIds.Contains(selectedListId) ||
            configs.Any(group => group.ListIds is null || group.ListIds.Any(id => !listIds.Contains(id)) || group.ListIds.Distinct().Count() != group.ListIds.Count))
        { reason = "列表名称、身份或成员关系无效，请检查列表名称是否为空或重复。"; return false; }
        if (CommandGroups.Any(g => !configs.Any(c => c.Id == g.Id) && !CanDeleteCommandGroup(g.Id)))
        { reason = "不能删除正在运行或被运行中队列引用的命令组，请先停止任务。"; return false; }
        reason = string.Empty; return true;
    }
    public void ApplyCommandGroupSettings(IReadOnlyList<CommandGroupConfig> configs, string? selectedId,
        IReadOnlyList<CommandGroupListConfig>? lists = null, string? selectedListId = null)
    {
        if (!CanApplyCommandGroupSettings(configs, out var reason, lists, selectedListId)) throw new InvalidOperationException(reason);
        if (lists is not null) LoadCommandGroupLists(lists, selectedListId);
        foreach (var removed in CommandGroups.Where(g => !configs.Any(c => c.Id == g.Id)).ToArray()) CommandGroups.Remove(removed);
        for (var index = 0; index < configs.Count; index++)
        {
            var config = configs[index]; var group = CommandGroups.FirstOrDefault(g => g.Id == config.Id);
            if (group is null) { group = new(config); CommandGroups.Insert(index, group); }
            else { group.ApplyConfig(config); CommandGroups.Move(CommandGroups.IndexOf(group), index); }
            SyncCommandGroupTargets(group);
        }
        SelectedCommandGroup = CommandGroups.FirstOrDefault(g => g.Id == selectedId) ?? CommandGroups.FirstOrDefault();
        RefreshCommandGroupLists();
        OnPropertyChanged(nameof(CommandGroups));
    }
    private string UniqueGroupName(string proposed)
    {
        var name = proposed; var number = 2;
        while (CommandGroups.Any(g => g.Name == name)) name = $"{proposed} ({number++})";
        return name;
    }
    public void ImportCommandGroups(IEnumerable<CommandGroupConfig> configs)
    {
        GroupListSearchText = string.Empty;
        foreach (var source in configs)
        {
            var copy = new CommandGroupConfig { Id = Guid.NewGuid().ToString("N"), Name = UniqueGroupName(source.Name), ListIds = NewGroupListIds.ToList(),
                Commands = source.Commands.ToList(), TargetIds = [], DelayMilliseconds = source.DelayMilliseconds,
                LoopIntervalMilliseconds = source.LoopIntervalMilliseconds, LoopCount = source.LoopCount, IsInfiniteLoop = source.IsInfiniteLoop,
                LineEnding = source.LineEnding, InterpretEscapes = source.InterpretEscapes };
            var group = new CommandGroupEditorViewModel(copy); CommandGroups.Add(group); SyncCommandGroupTargets(group); SelectedCommandGroup = group;
        }
        SetStatus("已导入命令组，请在当前工作区重新选择目标窗口。");
    }

    private void ClearCommandGroups()
    {
        if (SelectedCommandGroupList?.Id.Length > 0) { ClearCurrentListMembers(); return; }
        var candidates = VisibleCommandGroups.Where(group => CanDeleteCommandGroup(group.Id)).ToArray();
        if (candidates.Length == 0) return;
        var preserved = VisibleCommandGroups.Count - candidates.Length;
        var message = $"确定清空“全部”中的 {candidates.Length} 个空闲命令组吗？这些组也会从所有分类中删除，队列中引用它们的条目将一并移除；其他工作区不受影响。";
        if (preserved > 0) message += $"\n{preserved} 个正在运行或被运行中队列引用的组会保留，不会停止任何任务。";
        if (!ConfirmDelete("清空命令组列表", message)) return;
        var removed = 0;
        foreach (var group in candidates.Where(group => CanDeleteCommandGroup(group.Id)))
            if (CommandGroups.Remove(group)) removed++;
        if (SelectedCommandGroup is null || !CommandGroups.Contains(SelectedCommandGroup))
            SelectedCommandGroup = CommandGroups.FirstOrDefault();
        SetStatus($"已清空 {removed} 个命令组，保留 {CommandGroups.Count} 个；未停止运行中的任务。");
    }

    private void DeleteCommandGroup()
    {
        if (SelectedCommandGroup is null)
        {
            return;
        }

        var removedName = SelectedCommandGroup.Name;
        if (!CanDeleteCommandGroup(SelectedCommandGroup.Id))
        { SetStatus("命令组正在运行或被运行中的队列引用，请先停止任务。"); return; }
        if (!ConfirmDelete("删除命令组", $"确定删除命令组“{removedName}”吗？队列中引用该组的条目也会移除。"))
        {
            SetStatus($"已取消删除命令组：{removedName}");
            return;
        }

        var nextIndex = Math.Max(0, VisibleCommandGroups.IndexOf(SelectedCommandGroup) - 1);
        CommandGroups.Remove(SelectedCommandGroup);
        SelectedCommandGroup = VisibleCommandGroups.Count == 0 ? null : VisibleCommandGroups[Math.Min(nextIndex, VisibleCommandGroups.Count - 1)];
        SetStatus($"已删除命令组：{removedName}");
    }

    private void AddCommandToGroup()
    {
        if (SelectedCommandGroup is null || string.IsNullOrWhiteSpace(SelectedCommandGroup.NewCommand))
        {
            return;
        }

        SelectedCommandGroup.Commands.Add(SelectedCommandGroup.NewCommand.Trim());
        SelectedCommandGroup.NewCommand = string.Empty;
    }

    private void AddSelectedAtCommandToGroup()
    {
        if (SelectedCommandGroup is null || string.IsNullOrWhiteSpace(SelectedAtCommand))
        {
            return;
        }

        SelectedCommandGroup.NewCommand = SelectedAtCommand;
        SetStatus("已填入命令编辑框，修改参数后点击“加入命令”");
    }

    private void FillSingleCommandFromSelectedAtCommand()
    {
        if (string.IsNullOrWhiteSpace(SelectedAtCommand))
        {
            return;
        }

        CommandText = SelectedAtCommand;
        SelectedCommandPanelTabIndex = 0;
        SetStatus("已填入单条命令编辑框");
    }

    private void RemoveCommandFromGroup(object? parameter)
    {
        if (!TryGetGroupCommand(parameter, out var group, out var index)) return;
        group.Commands.RemoveAt(index);
        OnPropertyChanged(nameof(CommandGroups));
    }

    private bool CanOperateGroupCommand(object? parameter) => TryGetGroupCommand(parameter, out _, out _);

    private bool TryGetGroupCommand(object? parameter, out CommandGroupEditorViewModel group, out int index)
    {
        group = SelectedCommandGroup!;
        index = -1;
        if (group is null) return false;
        // UI 传入行索引，避免相同内容的命令误操作第一条；兼容旧入口的字符串参数。
        index = parameter is int row ? row : parameter is string command ? group.Commands.IndexOf(command) : -1;
        return index >= 0 && index < group.Commands.Count;
    }

    private void EditCommandInGroup(object? parameter)
    {
        if (!TryGetGroupCommand(parameter, out var group, out var index)) return;
        var original = group.Commands[index];
        var edited = _editCommandInGroup(original);
        if (edited is null) return;
        edited = edited.Trim();
        if (edited.Length == 0) { SetStatus("命令不能为空，原命令已保留"); return; }
        if (index >= group.Commands.Count || !string.Equals(original, group.Commands[index], StringComparison.Ordinal))
        { SetStatus("命令列表已变化，请重新选择要编辑的命令"); return; }
        if (string.Equals(original, edited, StringComparison.Ordinal)) return;
        group.Commands[index] = edited;
        OnPropertyChanged(nameof(CommandGroups));
        SetStatus("已保存编辑后的命令");
    }

    internal async Task CopyCommandFromGroupAsync(object? parameter)
    {
        if (_isCommandCopyInProgress || !TryGetGroupCommand(parameter, out var group, out var index)) return;
        var text = group.Commands[index];
        _isCommandCopyInProgress = true;
        CopyCommandFromGroupCommand.RaiseCanExecuteChanged();
        try
        {
            var operation = _copyCommandText(text);
            if (await Task.WhenAny(operation, Task.Delay(TimeSpan.FromSeconds(2))) != operation)
                SetStatus("系统剪贴板繁忙，正在后台复制命令...");
            await operation;
            SetStatus("已复制完整命令");
        }
        catch (Exception e) { SetStatus($"复制命令失败：{e.Message}"); }
        finally { _isCommandCopyInProgress = false; CopyCommandFromGroupCommand.RaiseCanExecuteChanged(); }
    }

    private void RemoveImportedAtCommand(object? parameter)
    {
        if (parameter is not string command)
        {
            return;
        }

        var index = ImportedAtCommands.IndexOf(command);
        if (index < 0)
        {
            return;
        }

        ImportedAtCommands.RemoveAt(index);
        SelectedAtCommand = ImportedAtCommands.Count == 0
            ? null
            : ImportedAtCommands[Math.Min(index, ImportedAtCommands.Count - 1)];
        SetStatus($"已删除导入命令：{command}");
    }

    private void EditImportedAtCommand(object? parameter)
    {
        if (parameter is not string command)
        {
            return;
        }

        var index = ImportedAtCommands.IndexOf(command);
        if (index < 0)
        {
            return;
        }

        var editedCommand = _editImportedAtCommand(command);
        if (editedCommand is null)
        {
            return;
        }

        editedCommand = editedCommand.Trim();
        if (editedCommand.Length == 0)
        {
            SetStatus("导入命令不能为空");
            return;
        }

        if (string.Equals(command, editedCommand, StringComparison.Ordinal))
        {
            return;
        }

        ImportedAtCommands[index] = editedCommand;
        SelectedAtCommand = editedCommand;
        SetStatus($"已编辑导入命令：{editedCommand}");
    }

    private void AddAtCommandSet()
    {
        var name = CreateUniqueAtCommandSetName();
        var commandSet = new AtCommandSetViewModel(name);
        ImportedAtCommandSets.Add(commandSet);
        SelectedAtCommandSet = commandSet;
        DeleteAtCommandSetCommand.RaiseCanExecuteChanged();
        SetStatus($"已新建命令集：{name}");
    }

    private void DeleteAtCommandSet()
    {
        if (ImportedAtCommandSets.Count <= 1 || SelectedAtCommandSet is null)
        {
            SetStatus("至少保留一套导入命令");
            return;
        }

        var selectedSet = _selectedAtCommandSet ?? ImportedAtCommandSets.First();
        var selectedIndex = ImportedAtCommandSets.IndexOf(selectedSet);
        if (selectedIndex < 0)
        {
            selectedIndex = 0;
            selectedSet = ImportedAtCommandSets[0];
        }

        var removedName = selectedSet.Name;
        if (!ConfirmDelete("删除命令集", $"确定删除命令集“{removedName}”吗？"))
        {
            SetStatus($"已取消删除命令集：{removedName}");
            return;
        }

        var nextIndex = Math.Min(selectedIndex, ImportedAtCommandSets.Count - 2);
        ImportedAtCommandSets.RemoveAt(selectedIndex);

        _selectedAtCommandSet = null;
        OnPropertyChanged(nameof(SelectedAtCommandSet));
        SelectedAtCommandSet = ImportedAtCommandSets[Math.Clamp(nextIndex, 0, ImportedAtCommandSets.Count - 1)];
        DeleteAtCommandSetCommand.RaiseCanExecuteChanged();
        SetStatus($"已删除命令集：{removedName}");
    }

    private bool ConfirmDelete(string title, string message)
    {
        return _confirmDelete(title, message);
    }

    private static bool ConfirmDeleteWithDialog(string title, string message)
    {
        return SerialLog.App.Views.DeleteConfirmationWindow.Confirm(title, message);
    }

    private void ImportAtFile()
    {
        if (!TryPickAtFile("导入 AT 命令文件", out var fileName))
        {
            return;
        }

        ReplaceImportedAtCommands(AtCommandImporter.Import(fileName));

        SetStatus($"已导入 {ImportedAtCommands.Count} 条 AT 命令");
    }

    private void AppendAtFile()
    {
        if (!TryPickAtFile("追加 AT 命令文件", out var fileName))
        {
            return;
        }

        var before = ImportedAtCommands.Count;
        ReplaceImportedAtCommands(AtCommandImporter.AppendDistinct(
            ImportedAtCommands,
            AtCommandImporter.Import(fileName),
            ResolveAtCommandConflict));

        SetStatus($"追加完成：新增 {Math.Max(0, ImportedAtCommands.Count - before)} 条，总计 {ImportedAtCommands.Count} 条");
    }

    private static AtCommandConflictChoice ResolveAtCommandConflict(AtCommandConflict conflict)
    {
        var result = MessageBox.Show(
            $"发现同名 AT 命令参数不同：\n\n命令：{conflict.CommandName}\n\n当前列表：\n{conflict.ExistingCommand}\n\n新文件：\n{conflict.NewCommand}\n\n点击“是”使用新文件命令，点击“否”保留当前命令。",
            "AT 命令冲突",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        return result == MessageBoxResult.Yes
            ? AtCommandConflictChoice.UseNew
            : AtCommandConflictChoice.KeepExisting;
    }

    private void ImportAtFromLog()
    {
        var sourceWindows = SerialWindows.Where(window => window.IsSelectedForSend).ToArray();
        if (sourceWindows.Length == 0)
        {
            sourceWindows = SerialWindows.ToArray();
        }

        var text = string.Join(
            Environment.NewLine,
            sourceWindows.SelectMany(window => window.Lines.Select(line => line.Text)));
        ReplaceImportedAtCommands(AtCommandImporter.ImportFromText(text));

        SetStatus($"已从日志导入 {ImportedAtCommands.Count} 条 AT 命令");
    }

    private void ImportCustomAtCommands()
    {
        if (!TryReadCustomAtCommands(out var text))
        {
            return;
        }

        var commands = AtCommandImporter.ImportFromText(text);
        if (commands.Count == 0)
        {
            SetStatus("自定义导入未发现 AT 命令");
            return;
        }

        var before = ImportedAtCommands.Count;
        ReplaceImportedAtCommands(AtCommandImporter.AppendDistinct(
            ImportedAtCommands,
            commands,
            ResolveAtCommandConflict));

        SetStatus($"自定义导入完成：新增 {Math.Max(0, ImportedAtCommands.Count - before)} 条，总计 {ImportedAtCommands.Count} 条");
    }

    private void ReplaceImportedAtCommands(IReadOnlyList<string> commands)
    {
        ImportedAtCommands.Clear();
        foreach (var command in commands)
        {
            ImportedAtCommands.Add(command);
        }

        SelectedAtCommand = ImportedAtCommands.FirstOrDefault();
        OnPropertyChanged(nameof(ImportedAtCommands));
    }

    public IReadOnlyList<AtCommandSetConfig> ToAtCommandSetConfigs()
    {
        return ImportedAtCommandSets.Select(commandSet => commandSet.ToConfig()).ToList();
    }

    public string? SelectedAtCommandSetName => _selectedAtCommandSet?.Name;

    public void LoadAtCommandSets(IEnumerable<AtCommandSetConfig>? configs, string? selectedName)
    {
        ImportedAtCommandSets.Clear();

        if (configs is not null)
        {
            foreach (var config in configs)
            {
                ImportedAtCommandSets.Add(new AtCommandSetViewModel(config.Name, config.Commands));
            }
        }

        if (ImportedAtCommandSets.Count == 0)
        {
            ImportedAtCommandSets.Add(new AtCommandSetViewModel("默认"));
        }

        var selected = ImportedAtCommandSets.FirstOrDefault(commandSet =>
            string.Equals(commandSet.Name, selectedName, StringComparison.OrdinalIgnoreCase)) ??
            ImportedAtCommandSets[0];
        SelectedAtCommandSet = selected;
        DeleteAtCommandSetCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(ImportedAtCommandSets));
        OnPropertyChanged(nameof(ImportedAtCommands));
    }

    private string CreateUniqueAtCommandSetName()
    {
        for (var index = 1; ; index++)
        {
            var name = $"命令集 {index + 1}";
            if (ImportedAtCommandSets.All(commandSet =>
                !string.Equals(commandSet.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                return name;
            }
        }
    }

    private static bool TryPickAtFile(string title, out string fileName)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = "AT/C files (*.txt;*.at;*.c;*.h)|*.txt;*.at;*.c;*.h|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog() == true)
        {
            fileName = dialog.FileName;
            return true;
        }

        fileName = string.Empty;
        return false;
    }

    private static bool TryReadCustomAtCommands(out string text)
    {
        var input = new TextBox
        {
            AcceptsReturn = true,
            AcceptsTab = true,
            FontFamily = new FontFamily("Consolas"),
            MinHeight = 210,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        System.Windows.Input.InputMethod.SetIsInputMethodEnabled(input, true);
        text = string.Empty;

        var dialog = new Window
        {
            Title = "自定义命令导入",
            Width = 560,
            Height = 390,
            MinWidth = 460,
            MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current?.MainWindow
        };
        dialog.SetResourceReference(Window.BackgroundProperty, "BackgroundBrush");

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var hint = new TextBlock
        {
            Text = "一行一条命令，空行和 # 注释会忽略。",
            Margin = new Thickness(0, 0, 0, 8)
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Grid.SetRow(hint, 0);
        root.Children.Add(hint);

        Grid.SetRow(input, 1);
        root.Children.Add(input);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };

        var importButton = new Button
        {
            Content = "导入",
            MinWidth = 76,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        importButton.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton"); importButton.FontWeight = FontWeights.Normal;
        importButton.Click += (_, _) =>
        {
            dialog.DialogResult = true;
            dialog.Close();
        };

        var cancelButton = new Button
        {
            Content = "取消",
            MinWidth = 76,
            IsCancel = true
        };

        actions.Children.Add(importButton);
        actions.Children.Add(cancelButton);
        Grid.SetRow(actions, 2);
        root.Children.Add(actions);

        dialog.Content = root;
        if (dialog.ShowDialog() != true)
        {
            return false;
        }

        text = input.Text;
        return true;
    }

    private static string? EditImportedAtCommandWithDialog(string command)
        => EditCommandWithDialog(command, "编辑导入命令", "修改后保存到当前命令集。");

    private static string? EditCommandWithDialog(string command, string title, string hintText)
    {
        var input = new TextBox
        {
            Text = command,
            FontFamily = new FontFamily("Consolas"),
            MinWidth = 440,
            MinHeight = 30,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        System.Windows.Input.InputMethod.SetIsInputMethodEnabled(input, true);

        var dialog = new Window
        {
            Title = title,
            Width = 560,
            Height = 170,
            MinWidth = 460,
            MinHeight = 150,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current?.MainWindow,
            ResizeMode = ResizeMode.NoResize
        };
        dialog.SetResourceReference(Window.BackgroundProperty, "BackgroundBrush");

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var hint = new TextBlock
        {
            Text = hintText,
            Margin = new Thickness(0, 0, 0, 8)
        };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        Grid.SetRow(hint, 0);
        root.Children.Add(hint);

        Grid.SetRow(input, 1);
        root.Children.Add(input);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var saveButton = new Button
        {
            Content = "保存",
            MinWidth = 76,
            Margin = new Thickness(0, 0, 8, 0),
            IsDefault = true
        };
        saveButton.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButton"); saveButton.FontWeight = FontWeights.Normal;
        saveButton.Click += (_, _) => dialog.DialogResult = true;
        var cancelButton = new Button
        {
            Content = "取消",
            MinWidth = 76,
            IsCancel = true
        };
        actions.Children.Add(saveButton);
        actions.Children.Add(cancelButton);
        Grid.SetRow(actions, 2);
        root.Children.Add(actions);

        dialog.Content = root;
        dialog.ContentRendered += (_, _) => InitializeCommandEditorInput(input);

        return dialog.ShowDialog() == true ? input.Text : null;
    }

    internal static void InitializeCommandEditorInput(TextBox input)
    {
        input.Focus();
        // 从末尾向开头选中，保留默认全选，同时把活动端和长命令视图留在开头。
        input.CaretIndex = input.Text.Length;
        EditingCommands.SelectToDocumentStart.Execute(null, input);
        input.ScrollToHome();
    }

    private void SerialWindows_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshObservedSerialWindows();
        SyncCommandGroupTargets();
        OnPropertyChanged(nameof(IsAllSingleCommandTargetsSelected));
    }

    private void RefreshObservedSerialWindows()
    {
        foreach (var window in _observedSerialWindows)
        {
            window.PropertyChanged -= SerialWindow_PropertyChanged;
        }

        _observedSerialWindows.Clear();
        foreach (var window in SerialWindows)
        {
            window.PropertyChanged += SerialWindow_PropertyChanged;
            _observedSerialWindows.Add(window);
        }
    }

    private void RefreshObservedCommandGroupTargets()
    {
        foreach (var target in _observedCommandGroupTargets)
        {
            target.PropertyChanged -= CommandGroupTarget_PropertyChanged;
        }

        _observedCommandGroupTargets.Clear();
        foreach (var target in CommandGroups.SelectMany(group => group.Targets))
        {
            target.PropertyChanged += CommandGroupTarget_PropertyChanged;
            _observedCommandGroupTargets.Add(target);
        }
    }

    private void SerialWindow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SerialWindowViewModel.Title) && sender is SerialWindowViewModel window)
            foreach (var target in CommandGroups.SelectMany(group => group.Targets).Where(t => t.TargetId == window.Id))
                target.Title = window.Title;
        if (e.PropertyName is nameof(SerialWindowViewModel.IsSelectedForSend) or nameof(SerialWindowViewModel.CanSendCommands))
        {
            OnPropertyChanged(nameof(IsAllSingleCommandTargetsSelected));
        }
    }

    private void CommandGroupTarget_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TargetSelectionViewModel.IsSelected))
        {
            OnPropertyChanged(nameof(IsAllCommandGroupTargetsSelected));
            foreach (var group in CommandGroups) group.RefreshSummary();
            OnPropertyChanged(nameof(CommandGroups));
        }
    }

    private void SetStatus(string text)
    {
        StatusText = text;
        _setStatus(text);
    }

    public void Dispose()
    {
        DisposeCommandGroupLists();
        _singleCommandLoopCts?.Cancel();
        GroupScheduler.Dispose();
        SerialWindows.CollectionChanged -= SerialWindows_CollectionChanged;
        foreach (var window in _observedSerialWindows)
        {
            window.PropertyChanged -= SerialWindow_PropertyChanged;
        }

        foreach (var target in _observedCommandGroupTargets)
        {
            target.PropertyChanged -= CommandGroupTarget_PropertyChanged;
        }
    }
}
