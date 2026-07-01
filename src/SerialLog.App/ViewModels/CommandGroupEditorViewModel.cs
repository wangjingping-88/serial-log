using System.Collections.ObjectModel;
using SerialLog.App.Infrastructure;
using SerialLog.Core.Commands;
using SerialLog.Core.Configuration;

namespace SerialLog.App.ViewModels;

public sealed class CommandGroupEditorViewModel : ObservableObject
{
    private readonly HashSet<string> _initialTargetIds = [];
    private string _name = "命令组";
    private string _newCommand = string.Empty;
    private int _delayMilliseconds = 500;
    private int _loopIntervalMilliseconds = 1000;
    private int _loopCount;
    private LineEnding _lineEnding = LineEnding.CrLf;

    public CommandGroupEditorViewModel()
    {
    }

    public CommandGroupEditorViewModel(CommandGroupConfig config)
    {
        _initialTargetIds = config.TargetIds.ToHashSet();
        _name = config.Name;
        _delayMilliseconds = config.DelayMilliseconds;
        _loopIntervalMilliseconds = config.LoopIntervalMilliseconds;
        _loopCount = config.LoopCount;
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
        set => SetProperty(ref _loopCount, Math.Max(0, value));
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
            LineEnding);
    }

    public CommandGroupConfig ToConfig()
    {
        return new CommandGroupConfig
        {
            Name = Name,
            TargetIds = Targets.Where(target => target.IsSelected).Select(target => target.TargetId).ToList(),
            Commands = Commands.ToList(),
            DelayMilliseconds = DelayMilliseconds,
            LoopIntervalMilliseconds = LoopIntervalMilliseconds,
            LoopCount = LoopCount,
            LineEnding = LineEnding
        };
    }

    public bool WasTargetSelected(string targetId)
    {
        return _initialTargetIds.Contains(targetId);
    }
}
