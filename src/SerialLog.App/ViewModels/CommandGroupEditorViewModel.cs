using System.Collections.ObjectModel;
using SerialLog.App.Infrastructure;
using SerialLog.Core.Commands;
using SerialLog.Core.Configuration;

namespace SerialLog.App.ViewModels;

public sealed class CommandGroupEditorViewModel : ObservableObject
{
    private readonly HashSet<string> _initialTargetIds = [];
    private string _name = "命令组";
    public ObservableCollection<string> ListIds { get; } = [];
    private string _newCommand = string.Empty;
    private int _delayMilliseconds = 500;
    private int _loopIntervalMilliseconds = 1000;
    private int _loopCount;
    private bool _isInfiniteLoop;
    private LineEnding _lineEnding = LineEnding.CrLf;
    private bool _isSelectedForRun;
    private bool _interpretEscapes = true;
    public bool InterpretEscapes { get => _interpretEscapes; set => SetProperty(ref _interpretEscapes, value); }
    private bool _isRunning;
    private string _executionStatus = "就绪";
    public string Id { get; private set; } = Guid.NewGuid().ToString("N");
    public bool IsSelectedForRun { get => _isSelectedForRun; set => SetProperty(ref _isSelectedForRun, value); }
    public bool IsRunning { get => _isRunning; internal set => SetProperty(ref _isRunning, value); }
    public string ExecutionStatus { get => _executionStatus; internal set => SetProperty(ref _executionStatus, value); }
    public string Summary => $"{Commands.Count} 条命令 · {Targets.Count(t => t.IsSelected)} 个目标 · {(IsInfiniteLoop ? "无限循环" : $"{Math.Max(1, LoopCount)} 次")}";
    public string QueueAdmissionToolTip => IsInfiniteLoop
        ? "已勾选无限循环，不能加入队列；请先取消“无限循环”。0 次表示不循环，只执行一轮。"
        : "按配置次数加入执行队列；0 次表示不循环，只执行一轮。";
    public bool IsLoopCountEnabled => !IsInfiniteLoop;
    public bool IsInfiniteLoop
    {
        get => _isInfiniteLoop;
        set
        {
            if (!SetProperty(ref _isInfiniteLoop, value)) return;
            OnPropertyChanged(nameof(IsLoopCountEnabled));
            OnPropertyChanged(nameof(QueueAdmissionToolTip));
        }
    }
    internal void RefreshSummary() => OnPropertyChanged(nameof(Summary));

    public CommandGroupEditorViewModel()
    {
        ListIds.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ListIds));
    }

    public CommandGroupEditorViewModel(CommandGroupConfig config) : this()
    {
        Id = config.Id;
        ListIds.Clear(); foreach (var id in config.ListIds) ListIds.Add(id);
        _interpretEscapes = config.InterpretEscapes;
        _initialTargetIds = config.TargetIds.ToHashSet();
        _name = config.Name;
        _delayMilliseconds = config.DelayMilliseconds;
        _loopIntervalMilliseconds = config.LoopIntervalMilliseconds;
        _loopCount = config.LoopCount;
        _isInfiniteLoop = config.IsInfiniteLoop;
        _lineEnding = config.LineEnding;
        foreach (var command in config.Commands)
        {
            Commands.Add(command);
        }
    }

    public ObservableCollection<string> Commands { get; } = [];

    public ObservableCollection<TargetSelectionViewModel> Targets { get; } = [];

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public string NewCommand
    {
        get => _newCommand;
        set => SetProperty(ref _newCommand, value);
    }

    public int DelayMilliseconds
    {
        get => _delayMilliseconds;
        set => SetProperty(ref _delayMilliseconds, Math.Max(0, value));
    }

    public int LoopIntervalMilliseconds
    {
        get => _loopIntervalMilliseconds;
        set => SetProperty(ref _loopIntervalMilliseconds, Math.Max(0, value));
    }

    public int LoopCount
    {
        get => _loopCount;
        set
        {
            if (SetProperty(ref _loopCount, Math.Max(0, value))) OnPropertyChanged(nameof(QueueAdmissionToolTip));
        }
    }

    public LineEnding LineEnding
    {
        get => _lineEnding;
        set => SetProperty(ref _lineEnding, value);
    }

    public CommandGroup ToCommandGroup()
    {
        return new CommandGroup(
            Name,
            Targets.Where(target => target.IsSelected).Select(target => target.TargetId).ToArray(),
            Commands.ToArray(),
            TimeSpan.FromMilliseconds(DelayMilliseconds),
            LineEnding,
            InterpretEscapes);
    }

    public CommandGroupConfig ToConfig()
    {
        return new CommandGroupConfig
        {
            Id = Id,
            ListIds = ListIds.ToList(),
            InterpretEscapes = InterpretEscapes,
            Name = Name,
            TargetIds = Targets.Where(target => target.IsSelected).Select(target => target.TargetId).ToList(),
            Commands = Commands.ToList(),
            DelayMilliseconds = DelayMilliseconds,
            LoopIntervalMilliseconds = LoopIntervalMilliseconds,
            LoopCount = LoopCount,
            IsInfiniteLoop = IsInfiniteLoop,
            LineEnding = LineEnding
        };
    }

    public bool WasTargetSelected(string targetId)
    {
        return _initialTargetIds.Contains(targetId);
    }

    public void ApplyConfig(CommandGroupConfig config)
    {
        if (config.Id != Id) throw new InvalidOperationException("不能修改命令组身份。");
        Name = config.Name; DelayMilliseconds = config.DelayMilliseconds;
        ListIds.Clear(); foreach (var id in config.ListIds) ListIds.Add(id);
        InterpretEscapes = config.InterpretEscapes;
        LoopIntervalMilliseconds = config.LoopIntervalMilliseconds; LoopCount = config.LoopCount;
        IsInfiniteLoop = config.IsInfiniteLoop; LineEnding = config.LineEnding;
        Commands.Clear(); foreach (var command in config.Commands) Commands.Add(command);
        _initialTargetIds.Clear(); foreach (var id in config.TargetIds) _initialTargetIds.Add(id);
        Targets.Clear(); RefreshSummary();
    }
}
