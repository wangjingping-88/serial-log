using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SerialLog.App.Infrastructure;
using SerialLog.Core.Commands;
using SerialLog.Core.Configuration;

namespace SerialLog.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int DefaultWindowCount = 6;
    private readonly string _workspacePath;
    private readonly DispatcherTimer? _reconnectTimer;
    private string _logRootDirectory = @"D:\serial-log-data\logs";
    private string _statusText = "就绪";

    public MainViewModel()
        : this(Path.Combine(@"D:\serial-log-data", "workspace.json"), startReconnectTimer: true)
    {
    }

    public MainViewModel(string workspacePath, bool startReconnectTimer = true)
    {
        _workspacePath = workspacePath;

        Layout = new WorkspaceLayoutViewModel(SerialWindows, text => StatusText = text);
        CommandPanel = new CommandPanelViewModel(SerialWindows, text => StatusText = text);
        Layout.PropertyChanged += ForwardLayoutPropertyChanged;
        CommandPanel.PropertyChanged += ForwardCommandPanelPropertyChanged;

        SaveWorkspaceCommand = new RelayCommand(SaveWorkspace);
        AddWindowCommand = new RelayCommand(AddWindow);
        AddPageCommand = Layout.AddPageCommand;
        RemoveCurrentPageCommand = Layout.RemoveCurrentPageCommand;
        RemoveWindowCommand = new RelayCommand(RemoveWindow, parameter => parameter is SerialWindowViewModel && SerialWindows.Count > 1);
        ConnectAllCommand = new RelayCommand(ConnectAll);
        DisconnectAllCommand = new RelayCommand(DisconnectAll);

        LoadWorkspace();
        if (SerialWindows.Count == 0)
        {
            for (var i = 1; i <= DefaultWindowCount; i++)
            {
                AddWindow($"串口 {i}");
            }
        }

        Layout.RebuildCurrentPage();
        CommandPanel.SyncCommandGroupTargets();

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

    public WorkspaceLayoutViewModel Layout { get; }

    public CommandPanelViewModel CommandPanel { get; }

    public ObservableCollection<SerialWindowViewModel> SerialWindows { get; } = [];

    public ObservableCollection<SerialWindowSlotViewModel> CurrentPageWindows => Layout.CurrentPageWindows;

    public ObservableCollection<string> CommandHistory => CommandPanel.CommandHistory;

    public ObservableCollection<string> ImportedAtCommands => CommandPanel.ImportedAtCommands;

    public ObservableCollection<CommandGroupEditorViewModel> CommandGroups => CommandPanel.CommandGroups;

    public IReadOnlyList<LineEnding> LineEndingOptions => CommandPanel.LineEndingOptions;

    public AsyncRelayCommand SendCommand => CommandPanel.SendCommand;

    public RelayCommand ToggleSingleCommandLoopCommand => CommandPanel.ToggleSingleCommandLoopCommand;

    public RelayCommand SaveWorkspaceCommand { get; }

    public RelayCommand AddWindowCommand { get; }

    public RelayCommand AddPageCommand { get; }

    public RelayCommand RemoveCurrentPageCommand { get; }

    public RelayCommand RemoveWindowCommand { get; }

    public RelayCommand ConnectAllCommand { get; }

    public RelayCommand DisconnectAllCommand { get; }

    public RelayCommand PreviousPageCommand => Layout.PreviousPageCommand;

    public RelayCommand NextPageCommand => Layout.NextPageCommand;

    public RelayCommand ToggleWindowExpansionCommand => Layout.ToggleWindowExpansionCommand;

    public RelayCommand AddCommandGroupCommand => CommandPanel.AddCommandGroupCommand;

    public RelayCommand DuplicateCommandGroupCommand => CommandPanel.DuplicateCommandGroupCommand;

    public RelayCommand DeleteCommandGroupCommand => CommandPanel.DeleteCommandGroupCommand;

    public RelayCommand AddCommandToGroupCommand => CommandPanel.AddCommandToGroupCommand;

    public RelayCommand RemoveCommandFromGroupCommand => CommandPanel.RemoveCommandFromGroupCommand;

    public RelayCommand ClearCommandHistoryCommand => CommandPanel.ClearCommandHistoryCommand;

    public RelayCommand RemoveImportedAtCommandCommand => CommandPanel.RemoveImportedAtCommandCommand;

    public AsyncRelayCommand ExecuteCommandGroupCommand => CommandPanel.ExecuteCommandGroupCommand;

    public RelayCommand ToggleCommandGroupLoopCommand => CommandPanel.ToggleCommandGroupLoopCommand;

    public RelayCommand ImportAtFileCommand => CommandPanel.ImportAtFileCommand;

    public RelayCommand AppendAtFileCommand => CommandPanel.AppendAtFileCommand;

    public RelayCommand ImportAtFromLogCommand => CommandPanel.ImportAtFromLogCommand;

    public RelayCommand CustomAtImportCommand => CommandPanel.CustomAtImportCommand;

    public RelayCommand FillSingleCommandFromAtCommandCommand => CommandPanel.FillSingleCommandFromAtCommandCommand;

    public RelayCommand AddAtCommandToGroupCommand => CommandPanel.AddAtCommandToGroupCommand;

    public RelayCommand SetCommandPanelDockCommand => Layout.SetCommandPanelDockCommand;

    public RelayCommand FloatCommandPanelCommand => Layout.FloatCommandPanelCommand;

    public RelayCommand RestoreCommandPanelCommand => Layout.RestoreCommandPanelCommand;

    public int CurrentPageIndex
    {
        get => Layout.CurrentPageIndex;
        set => Layout.CurrentPageIndex = value;
    }

    public int PageCount => Layout.PageCount;

    public string PageLabel => Layout.PageLabel;

    public int SelectedCommandPanelTabIndex
    {
        get => CommandPanel.SelectedCommandPanelTabIndex;
        set => CommandPanel.SelectedCommandPanelTabIndex = value;
    }

    public bool IsSingleCommandTabSelected => CommandPanel.IsSingleCommandTabSelected;

    public bool IsCommandGroupTabSelected => CommandPanel.IsCommandGroupTabSelected;

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
        get => CommandPanel.CommandText;
        set => CommandPanel.CommandText = value;
    }

    public string? SelectedHistoryCommand
    {
        get => CommandPanel.SelectedHistoryCommand;
        set => CommandPanel.SelectedHistoryCommand = value;
    }

    public LineEnding SelectedLineEnding
    {
        get => CommandPanel.SelectedLineEnding;
        set => CommandPanel.SelectedLineEnding = value;
    }

    public int SingleCommandLoopIntervalMilliseconds
    {
        get => CommandPanel.SingleCommandLoopIntervalMilliseconds;
        set => CommandPanel.SingleCommandLoopIntervalMilliseconds = value;
    }

    public int SingleCommandLoopCount
    {
        get => CommandPanel.SingleCommandLoopCount;
        set => CommandPanel.SingleCommandLoopCount = value;
    }

    public bool IsSingleCommandLoopRunning => CommandPanel.IsSingleCommandLoopRunning;

    public bool IsCommandGroupLoopRunning => CommandPanel.IsCommandGroupLoopRunning;

    public string SingleCommandLoopActionText => CommandPanel.SingleCommandLoopActionText;

    public string CommandGroupLoopActionText => CommandPanel.CommandGroupLoopActionText;

    public CommandPanelDock CommandPanelDock
    {
        get => Layout.CommandPanelDock;
        set => Layout.CommandPanelDock = value;
    }

    public bool IsCommandPanelFloating
    {
        get => Layout.IsCommandPanelFloating;
        set => Layout.IsCommandPanelFloating = value;
    }

    public bool IsCommandPanelDockedBottom => Layout.IsCommandPanelDockedBottom;

    public bool IsCommandPanelDockedTop => Layout.IsCommandPanelDockedTop;

    public bool IsCommandPanelDockedLeft => Layout.IsCommandPanelDockedLeft;

    public bool IsCommandPanelDockedRight => Layout.IsCommandPanelDockedRight;

    public bool IsCommandPanelDockedVertical => Layout.IsCommandPanelDockedVertical;

    public bool IsCommandPanelDockedHorizontal => Layout.IsCommandPanelDockedHorizontal;

    public int SerialGridRows => Layout.SerialGridRows;

    public int SerialGridColumns => Layout.SerialGridColumns;

    public Dock CommandPanelDockEdge => Layout.CommandPanelDockEdge;

    public double CommandPanelWidth => Layout.CommandPanelWidth;

    public double CommandPanelHeight => Layout.CommandPanelHeight;

    public Thickness CommandPanelMargin => Layout.CommandPanelMargin;

    public Visibility CommandPanelVisibility => Layout.CommandPanelVisibility;

    public string CommandPanelOrientationLabel => Layout.CommandPanelOrientationLabel;

    public CommandGroupEditorViewModel? SelectedCommandGroup
    {
        get => CommandPanel.SelectedCommandGroup;
        set => CommandPanel.SelectedCommandGroup = value;
    }

    public string? SelectedAtCommand
    {
        get => CommandPanel.SelectedAtCommand;
        set => CommandPanel.SelectedAtCommand = value;
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public void MoveSerialWindow(string windowId, int targetIndex)
    {
        Layout.MoveSerialWindow(windowId, targetIndex);
        CommandPanel.SyncCommandGroupTargets();
    }

    public void MoveSerialWindow(string windowId, int targetPageIndex, int targetPagePosition)
    {
        Layout.MoveSerialWindow(windowId, targetPageIndex, targetPagePosition);
        CommandPanel.SyncCommandGroupTargets();
    }

    public void MoveSelectedCommandInGroup(int sourceIndex, int targetIndex)
    {
        CommandPanel.MoveSelectedCommandInGroup(sourceIndex, targetIndex);
    }

    public void SaveWorkspace()
    {
        var config = new WorkspaceConfig
        {
            LogRootDirectory = LogRootDirectory,
            SelectedPageIndex = CurrentPageIndex,
            PageCount = PageCount,
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
                PageIndex = window.PageIndex,
                AutoSaveEnabled = window.AutoSaveEnabled
            }).ToList(),
            CommandGroups = CommandGroups.Select(group => group.ToConfig()).ToList()
        };

        WorkspaceConfigStore.Save(_workspacePath, config);
        StatusText = $"工作区已保存：{_workspacePath}";
    }

    private void AddWindow()
    {
        if (Layout.CurrentPageWindowCount >= 6)
        {
            AddPageCommand.Execute(null);
        }

        AddWindow($"串口 {SerialWindows.Count + 1}", CurrentPageIndex);
    }

    private void AddWindow(string title, int? pageIndex = null)
    {
        var window = new SerialWindowViewModel(Guid.NewGuid().ToString("N"), title)
        {
            AutoSaveEnabled = true,
            PageIndex = pageIndex ?? CurrentPageIndex
        };
        window.ApplyLogRoot(LogRootDirectory);
        SerialWindows.Add(window);
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
        CommandPanel.SyncCommandGroupTargets();
        RemoveWindowCommand.RaiseCanExecuteChanged();
        StatusText = $"已删除窗口：{window.Title}";
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

        for (var index = 0; index < config.SerialWindows.Count; index++)
        {
            var windowConfig = config.SerialWindows[index];
            var window = new SerialWindowViewModel(windowConfig.Id, windowConfig.Title)
            {
                PortName = windowConfig.PortName,
                BaudRate = windowConfig.BaudRate,
                AutoSaveEnabled = windowConfig.AutoSaveEnabled,
                PageIndex = windowConfig.PageIndex >= 0 ? windowConfig.PageIndex : index / 6
            };
            window.ApplyLogRoot(LogRootDirectory);
            SerialWindows.Add(window);
        }

        Layout.EnsurePageCount(Math.Max(
            config.PageCount,
            SerialWindows.Count == 0 ? 1 : SerialWindows.Max(window => window.PageIndex) + 1));

        foreach (var groupConfig in config.CommandGroups)
        {
            CommandGroups.Add(new CommandGroupEditorViewModel(groupConfig));
        }

        CommandPanel.SyncCommandGroupTargets();
        SelectedCommandGroup = CommandGroups.FirstOrDefault();
        CurrentPageIndex = config.SelectedPageIndex;
    }

    private void ForwardLayoutPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(e.PropertyName);
    }

    private void ForwardCommandPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(e.PropertyName);
    }

    public void Dispose()
    {
        _reconnectTimer?.Stop();
        CommandPanel.Dispose();
        foreach (var window in SerialWindows)
        {
            window.Dispose();
        }
    }
}
