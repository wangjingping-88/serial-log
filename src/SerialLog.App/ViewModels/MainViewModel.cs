using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SerialLog.App.Infrastructure;
using SerialLog.Core.Commands;
using SerialLog.Core.Configuration;

namespace SerialLog.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int PageSize = 6;
    private readonly string _workspacePath;
    private readonly DispatcherTimer? _reconnectTimer;
    private int _currentPageIndex;
    private string _logRootDirectory = @"D:\serial-log-data\logs";
    private string _commandText = string.Empty;
    private string? _selectedHistoryCommand;
    private LineEnding _selectedLineEnding = LineEnding.CrLf;
    private int _singleCommandLoopIntervalMilliseconds = 1000;
    private int _singleCommandLoopCount;
    private bool _isSingleCommandLoopRunning;
    private bool _isCommandGroupLoopRunning;
    private CancellationTokenSource? _singleCommandLoopCts;
    private CancellationTokenSource? _commandGroupLoopCts;
    private CommandPanelDock _commandPanelDock = CommandPanelDock.Bottom;
    private bool _isCommandPanelFloating;
    private int _selectedCommandPanelTabIndex;
    private CommandGroupEditorViewModel? _selectedCommandGroup;
    private string? _selectedAtCommand;
    private string _statusText = "就绪";

    public MainViewModel()
        : this(Path.Combine(@"D:\serial-log-data", "workspace.json"), startReconnectTimer: true)
    {
    }

    public MainViewModel(string workspacePath, bool startReconnectTimer = true)
    {
        _workspacePath = workspacePath;

        SendCommand = new AsyncRelayCommand(SendSingleCommandAsync);
        ToggleSingleCommandLoopCommand = new RelayCommand(ToggleSingleCommandLoop);
        SaveWorkspaceCommand = new RelayCommand(SaveWorkspace);
        AddWindowCommand = new RelayCommand(AddWindow);
        RemoveWindowCommand = new RelayCommand(RemoveWindow, parameter => parameter is SerialWindowViewModel && SerialWindows.Count > 1);
        ConnectAllCommand = new RelayCommand(ConnectAll);
        DisconnectAllCommand = new RelayCommand(DisconnectAll);
        PreviousPageCommand = new RelayCommand(() => CurrentPageIndex--, () => CurrentPageIndex > 0);
        NextPageCommand = new RelayCommand(() => CurrentPageIndex++, () => CurrentPageIndex < PageCount - 1);
        AddCommandGroupCommand = new RelayCommand(AddCommandGroup);
        DuplicateCommandGroupCommand = new RelayCommand(DuplicateCommandGroup, () => SelectedCommandGroup is not null);
        DeleteCommandGroupCommand = new RelayCommand(DeleteCommandGroup, () => SelectedCommandGroup is not null);
        AddCommandToGroupCommand = new RelayCommand(AddCommandToGroup, () => SelectedCommandGroup is not null);
        RemoveCommandFromGroupCommand = new RelayCommand(RemoveCommandFromGroup);
        ClearCommandHistoryCommand = new RelayCommand(ClearCommandHistory);
        RemoveImportedAtCommandCommand = new RelayCommand(RemoveImportedAtCommand);
        ExecuteCommandGroupCommand = new AsyncRelayCommand(ExecuteSelectedCommandGroupAsync, () => SelectedCommandGroup is not null);
        ToggleCommandGroupLoopCommand = new RelayCommand(ToggleCommandGroupLoop, () => SelectedCommandGroup is not null);
        ImportAtFileCommand = new RelayCommand(ImportAtFile);
        AppendAtFileCommand = new RelayCommand(AppendAtFile);
        ImportAtFromLogCommand = new RelayCommand(ImportAtFromLog);
        CustomAtImportCommand = new RelayCommand(ImportCustomAtCommands);
        FillSingleCommandFromAtCommandCommand = new RelayCommand(FillSingleCommandFromSelectedAtCommand, () => !string.IsNullOrWhiteSpace(SelectedAtCommand));
        AddAtCommandToGroupCommand = new RelayCommand(AddSelectedAtCommandToGroup, () => SelectedCommandGroup is not null && !string.IsNullOrWhiteSpace(SelectedAtCommand));
        SetCommandPanelDockCommand = new RelayCommand(SetCommandPanelDock);
        FloatCommandPanelCommand = new RelayCommand(FloatCommandPanel, () => !IsCommandPanelFloating);
        RestoreCommandPanelCommand = new RelayCommand(RestoreCommandPanel, () => IsCommandPanelFloating);

        LoadWorkspace();
        if (SerialWindows.Count == 0)
        {
            for (var i = 1; i <= PageSize; i++)
            {
                AddWindow($"串口 {i}");
            }
        }

        RebuildCurrentPage();
        SyncCommandGroupTargets();

        if (!startReconnectTimer)
        {
            return;
        }

        _reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _reconnectTimer.Tick += (_, _) =>
        {
            foreach (var window in SerialWindows)
            {
                window.TryAutoReconnect();
            }
        };
        _reconnectTimer.Start();
    }

    public ObservableCollection<SerialWindowViewModel> SerialWindows { get; } = [];

    public ObservableCollection<SerialWindowSlotViewModel> CurrentPageWindows { get; } = [];

    public ObservableCollection<string> CommandHistory { get; } = [];

    public ObservableCollection<string> ImportedAtCommands { get; } = [];

    public ObservableCollection<CommandGroupEditorViewModel> CommandGroups { get; } = [];

    public IReadOnlyList<LineEnding> LineEndingOptions { get; } =
        [LineEnding.None, LineEnding.Cr, LineEnding.Lf, LineEnding.CrLf];

    public AsyncRelayCommand SendCommand { get; }

    public RelayCommand ToggleSingleCommandLoopCommand { get; }

    public RelayCommand SaveWorkspaceCommand { get; }

    public RelayCommand AddWindowCommand { get; }

    public RelayCommand RemoveWindowCommand { get; }

    public RelayCommand ConnectAllCommand { get; }

    public RelayCommand DisconnectAllCommand { get; }

    public RelayCommand PreviousPageCommand { get; }

    public RelayCommand NextPageCommand { get; }

    public RelayCommand AddCommandGroupCommand { get; }

    public RelayCommand DuplicateCommandGroupCommand { get; }

    public RelayCommand DeleteCommandGroupCommand { get; }

    public RelayCommand AddCommandToGroupCommand { get; }

    public RelayCommand RemoveCommandFromGroupCommand { get; }

    public RelayCommand ClearCommandHistoryCommand { get; }

    public RelayCommand RemoveImportedAtCommandCommand { get; }

    public AsyncRelayCommand ExecuteCommandGroupCommand { get; }

    public RelayCommand ToggleCommandGroupLoopCommand { get; }

    public RelayCommand ImportAtFileCommand { get; }

    public RelayCommand AppendAtFileCommand { get; }

    public RelayCommand ImportAtFromLogCommand { get; }

    public RelayCommand CustomAtImportCommand { get; }

    public RelayCommand FillSingleCommandFromAtCommandCommand { get; }

    public RelayCommand AddAtCommandToGroupCommand { get; }

    public RelayCommand SetCommandPanelDockCommand { get; }

    public RelayCommand FloatCommandPanelCommand { get; }

    public RelayCommand RestoreCommandPanelCommand { get; }

    public int CurrentPageIndex
    {
        get => _currentPageIndex;
        set
        {
            var clamped = Math.Clamp(value, 0, Math.Max(0, PageCount - 1));
            if (SetProperty(ref _currentPageIndex, clamped))
            {
                OnPropertyChanged(nameof(PageLabel));
                RebuildCurrentPage();
                PreviousPageCommand.RaiseCanExecuteChanged();
                NextPageCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public int PageCount => Math.Max(1, (int)Math.Ceiling(SerialWindows.Count / (double)PageSize));

    public string PageLabel => $"{CurrentPageIndex + 1} / {PageCount}";

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
            }
        }
    }

    public bool IsSingleCommandTabSelected => SelectedCommandPanelTabIndex == 0;

    public bool IsCommandGroupTabSelected => SelectedCommandPanelTabIndex == 1;

    public string LogRootDirectory
    {
        get => _logRootDirectory;
        set
        {
            if (SetProperty(ref _logRootDirectory, value))
            {
                foreach (var window in SerialWindows)
                {
                    window.ApplyLogRoot(value);
                }
            }
        }
    }

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
                CommandText = value;
            }
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

    public CommandPanelDock CommandPanelDock
    {
        get => _commandPanelDock;
        set
        {
            if (SetProperty(ref _commandPanelDock, value))
            {
                OnPropertyChanged(nameof(IsCommandPanelDockedBottom));
                OnPropertyChanged(nameof(IsCommandPanelDockedTop));
                OnPropertyChanged(nameof(IsCommandPanelDockedLeft));
                OnPropertyChanged(nameof(IsCommandPanelDockedRight));
                OnPropertyChanged(nameof(IsCommandPanelDockedVertical));
                OnPropertyChanged(nameof(IsCommandPanelDockedHorizontal));
                OnPropertyChanged(nameof(CommandPanelDockEdge));
                OnPropertyChanged(nameof(CommandPanelWidth));
                OnPropertyChanged(nameof(CommandPanelHeight));
                OnPropertyChanged(nameof(CommandPanelMargin));
                OnPropertyChanged(nameof(CommandPanelVisibility));
                OnPropertyChanged(nameof(CommandPanelOrientationLabel));
                OnPropertyChanged(nameof(SerialGridRows));
                OnPropertyChanged(nameof(SerialGridColumns));
            }
        }
    }

    public bool IsCommandPanelFloating
    {
        get => _isCommandPanelFloating;
        set
        {
            if (SetProperty(ref _isCommandPanelFloating, value))
            {
                OnPropertyChanged(nameof(IsCommandPanelDockedBottom));
                OnPropertyChanged(nameof(IsCommandPanelDockedTop));
                OnPropertyChanged(nameof(IsCommandPanelDockedLeft));
                OnPropertyChanged(nameof(IsCommandPanelDockedRight));
                OnPropertyChanged(nameof(IsCommandPanelDockedVertical));
                OnPropertyChanged(nameof(IsCommandPanelDockedHorizontal));
                OnPropertyChanged(nameof(CommandPanelWidth));
                OnPropertyChanged(nameof(CommandPanelHeight));
                OnPropertyChanged(nameof(CommandPanelMargin));
                OnPropertyChanged(nameof(CommandPanelVisibility));
                OnPropertyChanged(nameof(CommandPanelOrientationLabel));
                OnPropertyChanged(nameof(SerialGridRows));
                OnPropertyChanged(nameof(SerialGridColumns));
                FloatCommandPanelCommand.RaiseCanExecuteChanged();
                RestoreCommandPanelCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsCommandPanelDockedBottom => !IsCommandPanelFloating && CommandPanelDock == CommandPanelDock.Bottom;

    public bool IsCommandPanelDockedTop => !IsCommandPanelFloating && CommandPanelDock == CommandPanelDock.Top;

    public bool IsCommandPanelDockedLeft => !IsCommandPanelFloating && CommandPanelDock == CommandPanelDock.Left;

    public bool IsCommandPanelDockedRight => !IsCommandPanelFloating && CommandPanelDock == CommandPanelDock.Right;

    public bool IsCommandPanelDockedVertical => IsCommandPanelDockedLeft || IsCommandPanelDockedRight;

    public bool IsCommandPanelDockedHorizontal => !IsCommandPanelFloating && !IsCommandPanelDockedVertical;

    public int SerialGridRows => IsCommandPanelDockedVertical ? 3 : 2;

    public int SerialGridColumns => IsCommandPanelDockedVertical ? 2 : 3;

    public Dock CommandPanelDockEdge => CommandPanelDock switch
    {
        CommandPanelDock.Top => Dock.Top,
        CommandPanelDock.Left => Dock.Left,
        CommandPanelDock.Right => Dock.Right,
        _ => Dock.Bottom
    };

    public double CommandPanelWidth => IsCommandPanelFloating ? 0 : IsCommandPanelDockedVertical ? 540 : double.NaN;

    public double CommandPanelHeight => IsCommandPanelFloating ? 0 : IsCommandPanelDockedHorizontal ? 300 : double.NaN;

    public Thickness CommandPanelMargin => IsCommandPanelFloating ? new Thickness(0) : CommandPanelDock switch
    {
        CommandPanelDock.Top => new Thickness(0, 12, 0, 8),
        CommandPanelDock.Left => new Thickness(0, 12, 8, 12),
        CommandPanelDock.Right => new Thickness(8, 12, 0, 12),
        _ => new Thickness(0, 8, 0, 12)
    };

    public Visibility CommandPanelVisibility => IsCommandPanelFloating ? Visibility.Collapsed : Visibility.Visible;

    public string CommandPanelOrientationLabel => IsCommandPanelFloating ? "命令区：浮动" : CommandPanelDock switch
    {
        CommandPanelDock.Top => "命令区：顶部",
        CommandPanelDock.Left => "命令区：左侧",
        CommandPanelDock.Right => "命令区：右侧",
        _ => "命令区：底部"
    };

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
                ExecuteCommandGroupCommand.RaiseCanExecuteChanged();
                ToggleCommandGroupLoopCommand.RaiseCanExecuteChanged();
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
        set => SetProperty(ref _statusText, value);
    }

    private async Task SendSingleCommandAsync()
    {
        if (string.IsNullOrWhiteSpace(CommandText))
        {
            StatusText = "请输入命令";
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
        StatusText = $"单条发送完成：成功 {sent}，跳过 {skipped}";
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
        StatusText = $"命令组执行中：{commandCount} 条命令，{targetCount} 个目标，命令间隔 {delayMilliseconds} ms";

        var result = await CommandGroupExecutor.ExecuteAsync(
            SelectedCommandGroup.ToCommandGroup(),
            SerialWindows,
            CancellationToken.None);
        StatusText = $"命令组完成：成功 {result.SentCount}，跳过 {result.SkippedCount}，失败 {result.FailedCount}，命令间隔 {delayMilliseconds} ms";
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
            StatusText = "请输入命令";
            return;
        }

        var targetIds = SerialWindows
            .Where(window => window.IsSelectedForSend)
            .Select(window => window.Id)
            .ToArray();
        if (targetIds.Length == 0)
        {
            StatusText = "请选择目标窗口";
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
            StatusText = "命令组为空";
            return;
        }

        var group = SelectedCommandGroup.ToCommandGroup();
        if (group.TargetIds.Count == 0)
        {
            StatusText = "请选择命令组目标窗口";
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
                StatusText = $"{label}中：第 {completedRounds}/{loopCountText} 轮，成功 {sent}，跳过 {skipped}，失败 {failed}";

                if ((loopCount <= 0 || completedRounds < loopCount) && loopDelay > TimeSpan.Zero)
                {
                    await Task.Delay(loopDelay, token);
                }
            }

            StatusText = $"{label}完成：共 {completedRounds} 轮，成功 {sent}，跳过 {skipped}，失败 {failed}";
        }
        catch (OperationCanceledException)
        {
            StatusText = $"{label}已停止：已执行 {completedRounds} 轮，成功 {sent}，跳过 {skipped}，失败 {failed}";
        }
        catch (Exception ex)
        {
            StatusText = $"{label}异常停止：已执行 {completedRounds} 轮，成功 {sent}，跳过 {skipped}，失败 {failed}；{ex.Message}";
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
        StatusText = "发送历史已清空";
    }

    private void AddWindow()
    {
        AddWindow($"串口 {SerialWindows.Count + 1}");
        CurrentPageIndex = PageCount - 1;
    }

    private void AddWindow(string title)
    {
        var window = new SerialWindowViewModel(Guid.NewGuid().ToString("N"), title)
        {
            AutoSaveEnabled = true
        };
        window.ApplyLogRoot(LogRootDirectory);
        SerialWindows.Add(window);
        OnPropertyChanged(nameof(PageCount));
        OnPropertyChanged(nameof(PageLabel));
        RebuildCurrentPage();
        SyncCommandGroupTargets();
        RemoveWindowCommand.RaiseCanExecuteChanged();
    }

    private void ConnectAll()
    {
        var attempts = 0;
        var sessionDirectory = LogSessionPathFactory.CreateSessionDirectory(LogRootDirectory, DateTimeOffset.Now);
        foreach (var window in SerialWindows)
        {
            window.RefreshPorts();
            if (string.IsNullOrWhiteSpace(window.PortName) || window.IsConnected)
            {
                continue;
            }

            window.Connect(sessionDirectory);
            attempts++;
        }

        StatusText = $"连接全部完成：已尝试 {attempts} 个窗口";
    }

    private void DisconnectAll()
    {
        var disconnected = 0;
        foreach (var window in SerialWindows)
        {
            if (window.IsConnected)
            {
                disconnected++;
            }

            window.Disconnect();
        }

        StatusText = $"断开全部完成：已断开 {disconnected} 个窗口";
    }

    private void RemoveWindow(object? parameter)
    {
        if (parameter is not SerialWindowViewModel window || SerialWindows.Count <= 1)
        {
            StatusText = "至少保留一个串口窗口";
            return;
        }

        var removed = SerialWindows.Remove(window);
        if (!removed)
        {
            return;
        }

        window.Dispose();
        OnPropertyChanged(nameof(PageCount));
        CurrentPageIndex = Math.Min(CurrentPageIndex, PageCount - 1);
        OnPropertyChanged(nameof(PageLabel));
        RebuildCurrentPage();
        SyncCommandGroupTargets();
        RemoveWindowCommand.RaiseCanExecuteChanged();
        NextPageCommand.RaiseCanExecuteChanged();
        PreviousPageCommand.RaiseCanExecuteChanged();
        StatusText = $"已删除窗口：{window.Title}";
    }

    public void MoveSerialWindow(string windowId, int targetIndex)
    {
        var sourceIndex = SerialWindows.ToList().FindIndex(window => window.Id == windowId);
        if (sourceIndex < 0)
        {
            return;
        }

        var clampedTarget = Math.Clamp(targetIndex, 0, SerialWindows.Count - 1);
        if (sourceIndex == clampedTarget)
        {
            return;
        }

        var title = SerialWindows[sourceIndex].Title;
        SerialWindows.Move(sourceIndex, clampedTarget);
        RebuildCurrentPage();
        SyncCommandGroupTargets();
        StatusText = $"已移动窗口：{title}";
    }

    private void AddCommandGroup()
    {
        var group = new CommandGroupEditorViewModel { Name = $"命令组 {CommandGroups.Count + 1}" };
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

        var nextIndex = Math.Max(0, CommandGroups.IndexOf(SelectedCommandGroup) - 1);
        CommandGroups.Remove(SelectedCommandGroup);
        SelectedCommandGroup = CommandGroups.Count == 0 ? null : CommandGroups[nextIndex];
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
        StatusText = "已填入命令编辑框，修改参数后点击“加入命令”";
    }

    private void FillSingleCommandFromSelectedAtCommand()
    {
        if (string.IsNullOrWhiteSpace(SelectedAtCommand))
        {
            return;
        }

        CommandText = SelectedAtCommand;
        SelectedCommandPanelTabIndex = 0;
        StatusText = "已填入单条命令编辑框";
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
        StatusText = $"已调整命令顺序：{sourceIndex + 1} -> {targetIndex + 1}";
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
        StatusText = $"已删除导入命令：{command}";
    }

    private void ImportAtFile()
    {
        if (!TryPickAtFile("导入 AT 命令文件", out var fileName))
        {
            return;
        }

        ReplaceImportedAtCommands(AtCommandImporter.Import(fileName));

        StatusText = $"已导入 {ImportedAtCommands.Count} 条 AT 命令";
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

        StatusText = $"追加完成：新增 {Math.Max(0, ImportedAtCommands.Count - before)} 条，总计 {ImportedAtCommands.Count} 条";
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

        StatusText = $"已从日志导入 {ImportedAtCommands.Count} 条 AT 命令";
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
            StatusText = "自定义导入未发现 AT 命令";
            return;
        }

        var before = ImportedAtCommands.Count;
        ReplaceImportedAtCommands(AtCommandImporter.AppendDistinct(
            ImportedAtCommands,
            commands,
            ResolveAtCommandConflict));

        StatusText = $"自定义导入完成：新增 {Math.Max(0, ImportedAtCommands.Count - before)} 条，总计 {ImportedAtCommands.Count} 条";
    }

    private void ReplaceImportedAtCommands(IReadOnlyList<string> commands)
    {
        ImportedAtCommands.Clear();
        foreach (var command in commands)
        {
            ImportedAtCommands.Add(command);
        }

        SelectedAtCommand = ImportedAtCommands.FirstOrDefault();
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

    private void LoadWorkspace()
    {
        var config = WorkspaceConfigStore.Load(_workspacePath);
        LogRootDirectory = config.LogRootDirectory;
        CommandPanelDock = config.CommandPanelDock;
        SingleCommandLoopIntervalMilliseconds = config.SingleCommandLoopIntervalMilliseconds;
        SingleCommandLoopCount = config.SingleCommandLoopCount;
        foreach (var history in config.CommandHistory)
        {
            CommandHistory.Add(history);
        }

        foreach (var windowConfig in config.SerialWindows)
        {
            var window = new SerialWindowViewModel(windowConfig.Id, windowConfig.Title)
            {
                PortName = windowConfig.PortName,
                BaudRate = windowConfig.BaudRate,
                AutoSaveEnabled = windowConfig.AutoSaveEnabled
            };
            window.ApplyLogRoot(LogRootDirectory);
            SerialWindows.Add(window);
        }

        foreach (var groupConfig in config.CommandGroups)
        {
            CommandGroups.Add(new CommandGroupEditorViewModel(groupConfig));
        }

        SelectedCommandGroup = CommandGroups.FirstOrDefault();
        CurrentPageIndex = config.SelectedPageIndex;
    }

    public void SaveWorkspace()
    {
        var config = new WorkspaceConfig
        {
            LogRootDirectory = LogRootDirectory,
            SelectedPageIndex = CurrentPageIndex,
            CommandPanelDock = CommandPanelDock,
            SingleCommandLoopIntervalMilliseconds = SingleCommandLoopIntervalMilliseconds,
            SingleCommandLoopCount = SingleCommandLoopCount,
            CommandHistory = CommandHistory.ToList(),
            SerialWindows = SerialWindows.Select(window => new SerialWindowConfig
            {
                Id = window.Id,
                Title = window.Title,
                PortName = window.PortName,
                BaudRate = window.BaudRate,
                AutoSaveEnabled = window.AutoSaveEnabled
            }).ToList(),
            CommandGroups = CommandGroups.Select(group => group.ToConfig()).ToList()
        };

        WorkspaceConfigStore.Save(_workspacePath, config);
        StatusText = $"工作区已保存：{_workspacePath}";
    }

    private void SetCommandPanelDock(object? parameter)
    {
        if (parameter is CommandPanelDock dock)
        {
            IsCommandPanelFloating = false;
            CommandPanelDock = dock;
            StatusText = CommandPanelOrientationLabel;
            return;
        }

        if (parameter is string text && Enum.TryParse<CommandPanelDock>(text, ignoreCase: true, out var parsed))
        {
            IsCommandPanelFloating = false;
            CommandPanelDock = parsed;
            StatusText = CommandPanelOrientationLabel;
        }
    }

    private void FloatCommandPanel()
    {
        IsCommandPanelFloating = true;
        StatusText = CommandPanelOrientationLabel;
    }

    private void RestoreCommandPanel()
    {
        CommandPanelDock = CommandPanelDock.Bottom;
        IsCommandPanelFloating = false;
        StatusText = CommandPanelOrientationLabel;
    }

    private void RebuildCurrentPage()
    {
        CurrentPageWindows.Clear();
        var pageWindows = SerialWindows.Skip(CurrentPageIndex * PageSize).Take(PageSize).ToArray();
        foreach (var window in pageWindows)
        {
            CurrentPageWindows.Add(new SerialWindowSlotViewModel(window));
        }

        if (pageWindows.Length < PageSize)
        {
            CurrentPageWindows.Add(new SerialWindowSlotViewModel(null));
        }
    }

    private void SyncCommandGroupTargets()
    {
        foreach (var group in CommandGroups)
        {
            SyncCommandGroupTargets(group);
        }
    }

    private void SyncCommandGroupTargets(CommandGroupEditorViewModel group)
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

    public void Dispose()
    {
        _reconnectTimer?.Stop();
        _singleCommandLoopCts?.Cancel();
        _commandGroupLoopCts?.Cancel();
        foreach (var window in SerialWindows)
        {
            window.Dispose();
        }
    }
}
