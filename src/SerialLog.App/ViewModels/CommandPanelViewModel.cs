using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SerialLog.App.Infrastructure;
using SerialLog.Core.Commands;
using SerialLog.Core.Configuration;

namespace SerialLog.App.ViewModels;

public sealed class CommandPanelViewModel : ObservableObject, IDisposable
{
    private readonly Action<string> _setStatus;
    private readonly Func<string, string, bool> _confirmDelete;
    private string _commandText = string.Empty;
    private string? _selectedHistoryCommand;
    private LineEnding _selectedLineEnding = LineEnding.CrLf;
    private int _singleCommandLoopIntervalMilliseconds = 1000;
    private int _singleCommandLoopCount;
    private bool _isSingleCommandLoopRunning;
    private bool _isCommandGroupLoopRunning;
    private int _selectedCommandPanelTabIndex;
    private CommandGroupEditorViewModel? _selectedCommandGroup;
    private AtCommandSetViewModel? _selectedAtCommandSet;
    private string? _selectedAtCommand;
    private string _statusText = string.Empty;
    private CancellationTokenSource? _singleCommandLoopCts;
    private CancellationTokenSource? _commandGroupLoopCts;
    private readonly ObservableCollection<string> _emptyImportedAtCommands = [];

    public CommandPanelViewModel(
        ObservableCollection<SerialWindowViewModel> serialWindows,
        Action<string> setStatus,
        Func<string, string, bool>? confirmDelete = null)
    {
        SerialWindows = serialWindows;
        _setStatus = setStatus;
        _confirmDelete = confirmDelete ?? ConfirmDeleteWithDialog;

        SendCommand = new AsyncRelayCommand(SendSingleCommandAsync);
        ToggleSingleCommandLoopCommand = new RelayCommand(ToggleSingleCommandLoop);
        AddCommandGroupCommand = new RelayCommand(AddCommandGroup);
        DuplicateCommandGroupCommand = new RelayCommand(DuplicateCommandGroup, () => SelectedCommandGroup is not null);
        DeleteCommandGroupCommand = new RelayCommand(DeleteCommandGroup, () => SelectedCommandGroup is not null);
        AddCommandToGroupCommand = new RelayCommand(AddCommandToGroup, () => SelectedCommandGroup is not null);
        RemoveCommandFromGroupCommand = new RelayCommand(RemoveCommandFromGroup);
        ClearCommandHistoryCommand = new RelayCommand(ClearCommandHistory);
        FillSingleCommandFromHistoryCommand = new RelayCommand(
            FillSingleCommandFromSelectedHistory,
            () => !string.IsNullOrWhiteSpace(SelectedHistoryCommand));
        AddHistoryCommandToGroupCommand = new RelayCommand(
            AddSelectedHistoryCommandToGroup,
            () => SelectedCommandGroup is not null && !string.IsNullOrWhiteSpace(SelectedHistoryCommand));
        RemoveImportedAtCommandCommand = new RelayCommand(RemoveImportedAtCommand);
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

    public RelayCommand AddCommandGroupCommand { get; }

    public RelayCommand DuplicateCommandGroupCommand { get; }

    public RelayCommand DeleteCommandGroupCommand { get; }

    public RelayCommand AddCommandToGroupCommand { get; }

    public RelayCommand RemoveCommandFromGroupCommand { get; }

    public RelayCommand ClearCommandHistoryCommand { get; }

    public RelayCommand FillSingleCommandFromHistoryCommand { get; }

    public RelayCommand AddHistoryCommandToGroupCommand { get; }

    public RelayCommand RemoveImportedAtCommandCommand { get; }

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
            var clamped = Math.Clamp(value, 0, 1);
            if (SetProperty(ref _selectedCommandPanelTabIndex, clamped))
            {
                OnPropertyChanged(nameof(IsSingleCommandTabSelected));
                OnPropertyChanged(nameof(IsCommandGroupTabSelected));
                AddHistoryCommandToGroupCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsSingleCommandTabSelected => SelectedCommandPanelTabIndex == 0;

    public bool IsCommandGroupTabSelected => SelectedCommandPanelTabIndex == 1;

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

    public bool IsCommandGroupLoopRunning
    {
        get => _isCommandGroupLoopRunning;
        private set
        {
            if (SetProperty(ref _isCommandGroupLoopRunning, value))
            {
                OnPropertyChanged(nameof(CommandGroupLoopActionText));
            }
        }
    }

    public string SingleCommandLoopActionText => IsSingleCommandLoopRunning ? "停止循环" : "循环";

    public string CommandGroupLoopActionText => IsCommandGroupLoopRunning ? "停止循环" : "循环";

    public CommandGroupEditorViewModel? SelectedCommandGroup
    {
        get => _selectedCommandGroup;
        set
        {
            if (SetProperty(ref _selectedCommandGroup, value))
            {
                DuplicateCommandGroupCommand.RaiseCanExecuteChanged();
                DeleteCommandGroupCommand.RaiseCanExecuteChanged();
                AddCommandToGroupCommand.RaiseCanExecuteChanged();
                AddAtCommandToGroupCommand.RaiseCanExecuteChanged();
                AddHistoryCommandToGroupCommand.RaiseCanExecuteChanged();
                ExecuteCommandGroupCommand.RaiseCanExecuteChanged();
                ToggleCommandGroupLoopCommand.RaiseCanExecuteChanged();
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

    public void SyncCommandGroupTargets()
    {
        foreach (var group in CommandGroups)
        {
            SyncCommandGroupTargets(group);
        }
    }

    public void SyncCommandGroupTargets(CommandGroupEditorViewModel group)
    {
        var selected = group.Targets.Count == 0
            ? SerialWindows.Where(window => group.WasTargetSelected(window.Id)).Select(window => window.Id).ToHashSet()
            : group.ToConfig().TargetIds.ToHashSet();
        group.Targets.Clear();
        foreach (var window in SerialWindows)
        {
            group.Targets.Add(new TargetSelectionViewModel(window.Id, window.Title, selected.Contains(window.Id)));
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

        var payload = CommandFormatter.ApplyLineEnding(CommandText.Trim(), SelectedLineEnding);
        var targets = SerialWindows.Where(window => window.IsSelectedForSend).ToArray();
        var sent = 0;
        var skipped = 0;

        foreach (var target in targets)
        {
            if (!target.IsConnected)
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
        if (SelectedCommandGroup is null)
        {
            return;
        }

        var commandCount = SelectedCommandGroup.Commands.Count;
        var targetCount = SelectedCommandGroup.Targets.Count(target => target.IsSelected);
        var delayMilliseconds = SelectedCommandGroup.DelayMilliseconds;
        SetStatus($"命令组执行中：{commandCount} 条命令，{targetCount} 个目标，命令间隔 {delayMilliseconds} ms");

        var result = await CommandGroupExecutor.ExecuteAsync(
            SelectedCommandGroup.ToCommandGroup(),
            SerialWindows,
            CancellationToken.None);
        SetStatus($"命令组完成：成功 {result.SentCount}，跳过 {result.SkippedCount}，失败 {result.FailedCount}，命令间隔 {delayMilliseconds} ms");
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
            .Where(window => window.IsSelectedForSend)
            .Select(window => window.Id)
            .ToArray();
        if (targetIds.Length == 0)
        {
            SetStatus("请选择目标窗口");
            return;
        }

        _singleCommandLoopCts = new CancellationTokenSource();
        IsSingleCommandLoopRunning = true;
        AddHistory(CommandText.Trim());
        _ = RunSingleCommandLoopAsync(
            CommandText.Trim(),
            SelectedLineEnding,
            targetIds,
            SingleCommandLoopCount,
            TimeSpan.FromMilliseconds(SingleCommandLoopIntervalMilliseconds),
            _singleCommandLoopCts);
    }

    private void ToggleCommandGroupLoop()
    {
        if (IsCommandGroupLoopRunning)
        {
            _commandGroupLoopCts?.Cancel();
            return;
        }

        if (SelectedCommandGroup is null)
        {
            return;
        }

        if (SelectedCommandGroup.Commands.Count == 0)
        {
            SetStatus("命令组为空");
            return;
        }

        var group = SelectedCommandGroup.ToCommandGroup();
        if (group.TargetIds.Count == 0)
        {
            SetStatus("请选择命令组目标窗口");
            return;
        }

        _commandGroupLoopCts = new CancellationTokenSource();
        IsCommandGroupLoopRunning = true;
        _ = RunCommandGroupLoopAsync(
            group,
            SelectedCommandGroup.LoopCount,
            TimeSpan.FromMilliseconds(SelectedCommandGroup.LoopIntervalMilliseconds),
            _commandGroupLoopCts);
    }

    private async Task RunSingleCommandLoopAsync(
        string command,
        LineEnding lineEnding,
        IReadOnlyList<string> targetIds,
        int loopCount,
        TimeSpan loopDelay,
        CancellationTokenSource cancellationTokenSource)
    {
        var group = new CommandGroup("单条命令循环", targetIds, [command], TimeSpan.Zero, lineEnding);
        await RunLoopAsync(
            "单条循环",
            group,
            loopCount,
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

    private async Task RunCommandGroupLoopAsync(
        CommandGroup group,
        int loopCount,
        TimeSpan loopDelay,
        CancellationTokenSource cancellationTokenSource)
    {
        await RunLoopAsync(
            "命令组循环",
            group,
            loopCount,
            loopDelay,
            cancellationTokenSource,
            () => IsCommandGroupLoopRunning = false,
            cts =>
            {
                if (ReferenceEquals(_commandGroupLoopCts, cts))
                {
                    _commandGroupLoopCts = null;
                }
            });
    }

    private async Task RunLoopAsync(
        string label,
        CommandGroup group,
        int loopCount,
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
        var loopCountText = loopCount <= 0 ? "无限" : loopCount.ToString();

        try
        {
            while (loopCount <= 0 || completedRounds < loopCount)
            {
                token.ThrowIfCancellationRequested();
                var result = await CommandGroupExecutor.ExecuteAsync(group, SerialWindows, token);
                completedRounds++;
                sent += result.SentCount;
                skipped += result.SkippedCount;
                failed += result.FailedCount;
                SetStatus($"{label}中：第 {completedRounds}/{loopCountText} 轮，成功 {sent}，跳过 {skipped}，失败 {failed}");

                if ((loopCount <= 0 || completedRounds < loopCount) && loopDelay > TimeSpan.Zero)
                {
                    await Task.Delay(loopDelay, token);
                }
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
        var group = new CommandGroupEditorViewModel { Name = $"命令组{CommandGroups.Count + 1}" };
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

        var copy = new CommandGroupEditorViewModel(SelectedCommandGroup.ToConfig())
        {
            Name = SelectedCommandGroup.Name + " 副本"
        };
        CommandGroups.Add(copy);
        SyncCommandGroupTargets(copy);
        SelectedCommandGroup = copy;
    }

    private void DeleteCommandGroup()
    {
        if (SelectedCommandGroup is null)
        {
            return;
        }

        var removedName = SelectedCommandGroup.Name;
        if (!ConfirmDelete("删除命令组", $"确定删除命令组“{removedName}”吗？"))
        {
            SetStatus($"已取消删除命令组：{removedName}");
            return;
        }

        var nextIndex = Math.Max(0, CommandGroups.IndexOf(SelectedCommandGroup) - 1);
        CommandGroups.Remove(SelectedCommandGroup);
        SelectedCommandGroup = CommandGroups.Count == 0 ? null : CommandGroups[nextIndex];
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
        if (SelectedCommandGroup is null || parameter is not string command)
        {
            return;
        }

        SelectedCommandGroup.Commands.Remove(command);
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
        return MessageBox.Show(
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
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
        text = string.Empty;

        var dialog = new Window
        {
            Title = "自定义命令导入",
            Width = 560,
            Height = 390,
            MinWidth = 460,
            MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current?.MainWindow,
            Background = Brushes.White
        };

        var root = new Grid { Margin = new Thickness(14) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var hint = new TextBlock
        {
            Text = "一行一条命令，空行和 # 注释会忽略。",
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 0, 0, 8)
        };
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

    private void SerialWindows_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncCommandGroupTargets();
    }

    private void SetStatus(string text)
    {
        StatusText = text;
        _setStatus(text);
    }

    public void Dispose()
    {
        _singleCommandLoopCts?.Cancel();
        _commandGroupLoopCts?.Cancel();
        SerialWindows.CollectionChanged -= SerialWindows_CollectionChanged;
    }
}
