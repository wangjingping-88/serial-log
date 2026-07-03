using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Collections.Specialized;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SerialLog.App.Infrastructure;
using SerialLog.Core.Collaboration;
using SerialLog.Core.Commands;
using SerialLog.Core.Configuration;
using SerialLog.Core.Logging;

namespace SerialLog.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const int DefaultWindowCount = 6;
    private readonly string _workspacePath;
    private readonly DispatcherTimer _autoSaveTimer;
    private readonly DispatcherTimer? _reconnectTimer;
    private readonly CollaborationHostService _collaborationHost = new();
    private readonly CollaborationClientService _collaborationClient = new();
    private string _logRootDirectory = @"D:\serial-log-data\logs";
    private string _collaborationRunStatusText = "未启动";
    private bool _isCollaborationRunning;
    private bool _isCollaborationReconnectPending;
    private bool _isCollaborationReconnectInProgress;
    private bool _isLoadingWorkspace;
    private bool _isDisposed;
    private DateTimeOffset _lastCollaborationReconnectAttemptUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPortRefreshAttemptUtc = DateTimeOffset.MinValue;
    private string _statusText = "就绪";

    public MainViewModel()
        : this(Path.Combine(@"D:\serial-log-data", "workspace.json"), startReconnectTimer: true)
    {
    }

    public MainViewModel(string workspacePath, bool startReconnectTimer = true)
    {
        _workspacePath = workspacePath;
        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _autoSaveTimer.Tick += AutoSaveTimer_Tick;

        Layout = new WorkspaceLayoutViewModel(SerialWindows, text => StatusText = text);
        CommandPanel = new CommandPanelViewModel(SerialWindows, text => StatusText = text);
        Collaboration = new CollaborationViewModel();
        Layout.PropertyChanged += ForwardLayoutPropertyChanged;
        CommandPanel.PropertyChanged += ForwardCommandPanelPropertyChanged;
        Collaboration.PropertyChanged += ForwardCollaborationPropertyChanged;
        CommandHistory.CollectionChanged += PersistedCollectionChanged;
        CommandGroups.CollectionChanged += PersistedCollectionChanged;
        ImportedAtCommandSets.CollectionChanged += PersistedCollectionChanged;
        _collaborationHost.ClientSnapshotReceived += CollaborationHost_ClientSnapshotReceived;
        _collaborationHost.LogLineReceived += CollaborationHost_LogLineReceived;
        _collaborationHost.ClientDisconnected += CollaborationHost_ClientDisconnected;
        _collaborationClient.CommandReceived += CollaborationClient_CommandReceived;
        _collaborationClient.Disconnected += CollaborationClient_Disconnected;

        SaveWorkspaceCommand = new RelayCommand(SaveWorkspace);
        AddWindowCommand = new RelayCommand(AddWindow);
        AddPageCommand = Layout.AddPageCommand;
        RemoveCurrentPageCommand = Layout.RemoveCurrentPageCommand;
        RemoveWindowCommand = new RelayCommand(RemoveWindow, parameter => parameter is SerialWindowViewModel && SerialWindows.Count > 1);
        ConnectAllCommand = new RelayCommand(ConnectAll);
        DisconnectAllCommand = new RelayCommand(DisconnectAll);
        StartCollaborationCommand = new AsyncRelayCommand(StartCollaborationAsync, () => WorkspaceMode != WorkspaceMode.Local && !IsCollaborationRunning);
        StopCollaborationCommand = new AsyncRelayCommand(StopCollaborationAsync, () => IsCollaborationRunning);

        _isLoadingWorkspace = true;
        try
        {
            LoadWorkspace();
            if (SerialWindows.Count == 0)
            {
                for (var i = 1; i <= DefaultWindowCount; i++)
                {
                    AddWindow($"串口 {i}");
                }
            }
        }
        finally
        {
            _isLoadingWorkspace = false;
        }

        Layout.RebuildCurrentPage();
        CommandPanel.SyncCommandGroupTargets();
        ScheduleAutoSave();

        if (!startReconnectTimer)
        {
            return;
        }

        _reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _reconnectTimer.Tick += (_, _) =>
        {
            AutoRefreshLocalPorts();

            foreach (var window in SerialWindows)
            {
                window.TryAutoReconnect();
            }

            TryAutoReconnectCollaboration();
        };
        _reconnectTimer.Start();
    }

    public WorkspaceLayoutViewModel Layout { get; }

    public CommandPanelViewModel CommandPanel { get; }

    public CollaborationViewModel Collaboration { get; }

    public ObservableCollection<SerialWindowViewModel> SerialWindows { get; } = [];

    public ObservableCollection<SerialWindowSlotViewModel> CurrentPageWindows => Layout.CurrentPageWindows;

    public ObservableCollection<string> CommandHistory => CommandPanel.CommandHistory;

    public ObservableCollection<string> ImportedAtCommands => CommandPanel.ImportedAtCommands;

    public ObservableCollection<AtCommandSetViewModel> ImportedAtCommandSets => CommandPanel.ImportedAtCommandSets;

    public ObservableCollection<CommandGroupEditorViewModel> CommandGroups => CommandPanel.CommandGroups;

    public IReadOnlyList<LineEnding> LineEndingOptions => CommandPanel.LineEndingOptions;

    public IReadOnlyList<WorkspaceModeOption> WorkspaceModeOptions => Collaboration.WorkspaceModeOptions;

    public IReadOnlyList<PcColorOption> PcColorOptions => Collaboration.PcColorOptions;

    public AsyncRelayCommand SendCommand => CommandPanel.SendCommand;

    public RelayCommand ToggleSingleCommandLoopCommand => CommandPanel.ToggleSingleCommandLoopCommand;

    public RelayCommand SaveWorkspaceCommand { get; }

    public RelayCommand AddWindowCommand { get; }

    public RelayCommand AddPageCommand { get; }

    public RelayCommand RemoveCurrentPageCommand { get; }

    public RelayCommand RemoveWindowCommand { get; }

    public RelayCommand ConnectAllCommand { get; }

    public RelayCommand DisconnectAllCommand { get; }

    public AsyncRelayCommand StartCollaborationCommand { get; }

    public AsyncRelayCommand StopCollaborationCommand { get; }

    public RelayCommand PreviousPageCommand => Layout.PreviousPageCommand;

    public RelayCommand NextPageCommand => Layout.NextPageCommand;

    public RelayCommand ToggleWindowExpansionCommand => Layout.ToggleWindowExpansionCommand;

    public RelayCommand AddCommandGroupCommand => CommandPanel.AddCommandGroupCommand;

    public RelayCommand DuplicateCommandGroupCommand => CommandPanel.DuplicateCommandGroupCommand;

    public RelayCommand DeleteCommandGroupCommand => CommandPanel.DeleteCommandGroupCommand;

    public RelayCommand AddCommandToGroupCommand => CommandPanel.AddCommandToGroupCommand;

    public RelayCommand RemoveCommandFromGroupCommand => CommandPanel.RemoveCommandFromGroupCommand;

    public RelayCommand ClearCommandHistoryCommand => CommandPanel.ClearCommandHistoryCommand;

    public RelayCommand FillSingleCommandFromHistoryCommand => CommandPanel.FillSingleCommandFromHistoryCommand;

    public RelayCommand AddHistoryCommandToGroupCommand => CommandPanel.AddHistoryCommandToGroupCommand;

    public RelayCommand RemoveImportedAtCommandCommand => CommandPanel.RemoveImportedAtCommandCommand;

    public RelayCommand AddAtCommandSetCommand => CommandPanel.AddAtCommandSetCommand;

    public RelayCommand DeleteAtCommandSetCommand => CommandPanel.DeleteAtCommandSetCommand;

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

    public WorkspaceMode WorkspaceMode
    {
        get => Collaboration.WorkspaceMode;
        set => Collaboration.WorkspaceMode = value;
    }

    public string LocalPcId
    {
        get => Collaboration.LocalPcId;
        set => Collaboration.LocalPcId = value;
    }

    public string LocalPcName
    {
        get => Collaboration.LocalPcName;
        set => Collaboration.LocalPcName = value;
    }

    public string LocalPcColor
    {
        get => Collaboration.LocalPcColor;
        set => Collaboration.LocalPcColor = value;
    }

    public PcColorOption? SelectedPcColorOption
    {
        get => Collaboration.SelectedPcColorOption;
        set => Collaboration.SelectedPcColorOption = value;
    }

    public string HostAddress
    {
        get => Collaboration.HostAddress;
        set => Collaboration.HostAddress = value;
    }

    public int HostPort
    {
        get => Collaboration.HostPort;
        set => Collaboration.HostPort = value;
    }

    public bool IsCollaborationNetworked => Collaboration.IsNetworked;

    public string CollaborationStatusText => Collaboration.ModeStatusText;

    public bool IsCollaborationRunning
    {
        get => _isCollaborationRunning;
        private set
        {
            if (SetProperty(ref _isCollaborationRunning, value))
            {
                OnPropertyChanged(nameof(CollaborationRunStatusText));
                StartCollaborationCommand.RaiseCanExecuteChanged();
                StopCollaborationCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string CollaborationRunStatusText
    {
        get => _collaborationRunStatusText;
        private set => SetProperty(ref _collaborationRunStatusText, value);
    }

    public bool IsCollaborationReconnectPending
    {
        get => _isCollaborationReconnectPending;
        private set => SetProperty(ref _isCollaborationReconnectPending, value);
    }

    public string AppVersionText => AppVersionInfo.VersionText;

    public string ProtocolVersionText => AppVersionInfo.ProtocolVersionText;

    public string AppBuildStatusText => AppVersionInfo.BuildStatusText;

    public string StartCollaborationActionText => WorkspaceMode switch
    {
        WorkspaceMode.Host => "启动主机",
        WorkspaceMode.Client => "连接主机",
        _ => "本地"
    };

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

    public bool IsCommandPanelVerticalShape => Layout.IsCommandPanelVerticalShape;

    public bool IsCommandPanelHorizontalShape => Layout.IsCommandPanelHorizontalShape;

    public bool IsCommandPanelDockedVertical => Layout.IsCommandPanelDockedVertical;

    public bool IsCommandPanelDockedHorizontal => Layout.IsCommandPanelDockedHorizontal;

    public int SerialGridRows => Layout.SerialGridRows;

    public int SerialGridColumns => Layout.SerialGridColumns;

    public Dock CommandPanelDockEdge => Layout.CommandPanelDockEdge;

    public double CommandPanelWidth => Layout.CommandPanelWidth;

    public double CommandPanelHeight => Layout.CommandPanelHeight;

    public double FloatingCommandPanelWidth => Layout.FloatingCommandPanelWidth;

    public double FloatingCommandPanelHeight => Layout.FloatingCommandPanelHeight;

    public double FloatingCommandPanelMinWidth => Layout.FloatingCommandPanelMinWidth;

    public double FloatingCommandPanelMinHeight => Layout.FloatingCommandPanelMinHeight;

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

    public AtCommandSetViewModel SelectedAtCommandSet
    {
        get => CommandPanel.SelectedAtCommandSet;
        set => CommandPanel.SelectedAtCommandSet = value;
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
        SaveWorkspace(updateStatus: true);
    }

    private void SaveWorkspace(bool updateStatus)
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
            AtCommandSets = CommandPanel.ToAtCommandSetConfigs().ToList(),
            SelectedAtCommandSetName = CommandPanel.SelectedAtCommandSetName,
            SerialWindows = SerialWindows.Where(window => !window.IsRemote).Select(window => new SerialWindowConfig
            {
                Id = window.Id,
                Title = window.Title,
                PortName = window.PortName,
                BaudRate = window.BaudRate,
                PageIndex = window.PageIndex,
                OwnerPcId = window.OwnerPcId,
                OwnerPcName = window.OwnerPcName,
                OwnerPcColor = window.OwnerPcColor,
                AutoSaveEnabled = window.AutoSaveEnabled
            }).ToList(),
            CommandGroups = CommandGroups.Select(group => group.ToConfig()).ToList()
        };

        Collaboration.SaveToConfig(config);
        WorkspaceConfigStore.Save(_workspacePath, config);
        if (updateStatus)
        {
            StatusText = $"工作区已保存：{_workspacePath}";
        }
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
        Collaboration.ApplyLocalOwner(window);
        RegisterSerialWindow(window);
        RemoveWindowCommand.RaiseCanExecuteChanged();
        _ = PublishLocalSnapshotIfClientRunningAsync();
    }

    private void ConnectAll()
    {
        var attempts = 0;
        var sessionDirectory = LogSessionPathFactory.CreateSessionDirectory(LogRootDirectory, DateTimeOffset.Now);
        foreach (var window in SerialWindows.Where(window => !window.IsRemote))
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
        _ = PublishLocalSnapshotIfClientRunningAsync();
    }

    private void AutoRefreshLocalPorts()
    {
        var now = DateTimeOffset.Now;
        if (now - _lastPortRefreshAttemptUtc < TimeSpan.FromSeconds(2))
        {
            return;
        }

        _lastPortRefreshAttemptUtc = now;
        foreach (var window in SerialWindows.Where(window => !window.IsRemote))
        {
            window.AutoRefreshPorts();
        }
    }

    private void DisconnectAll()
    {
        var disconnected = 0;
        foreach (var window in SerialWindows.Where(window => !window.IsRemote))
        {
            if (window.IsConnected)
            {
                disconnected++;
            }

            window.Disconnect();
        }

        StatusText = $"断开全部完成：已断开 {disconnected} 个窗口";
        _ = PublishLocalSnapshotIfClientRunningAsync();
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

        UnregisterSerialWindow(window);
        window.Dispose();
        CommandPanel.SyncCommandGroupTargets();
        RemoveWindowCommand.RaiseCanExecuteChanged();
        StatusText = $"已删除窗口：{window.Title}";
        _ = PublishLocalSnapshotIfClientRunningAsync();
    }

    private void LoadWorkspace()
    {
        var config = WorkspaceConfigStore.Load(_workspacePath);
        Collaboration.LoadFromConfig(config);
        LogRootDirectory = config.LogRootDirectory;
        CommandPanelDock = config.CommandPanelDock;
        SingleCommandLoopIntervalMilliseconds = config.SingleCommandLoopIntervalMilliseconds;
        SingleCommandLoopCount = config.SingleCommandLoopCount;
        foreach (var history in config.CommandHistory)
        {
            CommandHistory.Add(history);
        }

        CommandPanel.LoadAtCommandSets(config.AtCommandSets, config.SelectedAtCommandSetName);

        for (var index = 0; index < config.SerialWindows.Count; index++)
        {
            var windowConfig = config.SerialWindows[index];
            var window = new SerialWindowViewModel(windowConfig.Id, windowConfig.Title)
            {
                PortName = windowConfig.PortName,
                BaudRate = windowConfig.BaudRate,
                AutoSaveEnabled = windowConfig.AutoSaveEnabled,
                OwnerPcId = windowConfig.OwnerPcId,
                OwnerPcName = windowConfig.OwnerPcName,
                OwnerPcColor = windowConfig.OwnerPcColor,
                PageIndex = windowConfig.PageIndex >= 0 ? windowConfig.PageIndex : index / 6
            };
            window.ApplyLogRoot(LogRootDirectory);
            RegisterSerialWindow(window);
        }

        Collaboration.ApplyOwnership(SerialWindows);

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

    private void RegisterSerialWindow(SerialWindowViewModel window)
    {
        window.LinesReceived += SerialWindow_LinesReceived;
        window.PropertyChanged += SerialWindow_PropertyChanged;
        SerialWindows.Add(window);
    }

    private void UnregisterSerialWindow(SerialWindowViewModel window)
    {
        window.LinesReceived -= SerialWindow_LinesReceived;
        window.PropertyChanged -= SerialWindow_PropertyChanged;
    }

    private async Task StartCollaborationAsync()
    {
        try
        {
            if (WorkspaceMode == WorkspaceMode.Local)
            {
                CollaborationRunStatusText = "本地模式";
                StatusText = "本地模式不需要启动协作。";
                return;
            }

            if (WorkspaceMode == WorkspaceMode.Host)
            {
                await _collaborationClient.DisconnectAsync().ConfigureAwait(false);
                await _collaborationHost.StopAsync().ConfigureAwait(false);
                await _collaborationHost.StartAsync(IPAddress.Any, HostPort).ConfigureAwait(false);
                var actualPort = _collaborationHost.Port;
                RunOnUi(() =>
                {
                    HostPort = actualPort;
                    IsCollaborationRunning = true;
                    IsCollaborationReconnectPending = false;
                    CollaborationRunStatusText = "主机已启动";
                    StatusText = $"主机监听 {HostAddress}:{HostPort}";
                });
                return;
            }

            await _collaborationHost.StopAsync().ConfigureAwait(false);
            await _collaborationClient.ConnectAsync(HostAddress, HostPort, BuildLocalSnapshot()).ConfigureAwait(false);
            RunOnUi(() =>
            {
                IsCollaborationRunning = true;
                IsCollaborationReconnectPending = false;
                CollaborationRunStatusText = "已连接主机";
                StatusText = $"已连接主机 {HostAddress}:{HostPort}";
            });
        }
        catch (Exception ex)
        {
            RunOnUi(() =>
            {
                if (WorkspaceMode == WorkspaceMode.Client)
                {
                    BeginClientReconnect($"协作失败：{ex.Message}");
                    return;
                }

                IsCollaborationRunning = false;
                CollaborationRunStatusText = $"协作失败：{ex.Message}";
                StatusText = CollaborationRunStatusText;
            });
        }
        finally
        {
            RunOnUi(UpdateCollaborationCommands);
        }
    }

    private async Task StopCollaborationAsync()
    {
        try
        {
            await _collaborationClient.DisconnectAsync().ConfigureAwait(false);
            await _collaborationHost.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            RunOnUi(() =>
            {
                RemoveRemoteWindows();
                IsCollaborationRunning = false;
                IsCollaborationReconnectPending = false;
                CollaborationRunStatusText = "未启动";
                StatusText = "协作已停止";
                UpdateCollaborationCommands();
            });
        }
    }

    private CollaborationClientSnapshot BuildLocalSnapshot()
    {
        return new CollaborationClientSnapshot(
            LocalPcId,
            LocalPcName,
            LocalPcColor,
            SerialWindows
                .Where(window => !window.IsRemote)
                .Select(ToSnapshot)
                .ToList());
    }

    private static CollaborationWindowSnapshot ToSnapshot(SerialWindowViewModel window)
    {
        return new CollaborationWindowSnapshot(
            window.Id,
            window.Title,
            window.PortName,
            window.BaudRate,
            window.IsConnected,
            window.LineCount);
    }

    private void CollaborationHost_ClientSnapshotReceived(object? sender, CollaborationClientSnapshot snapshot)
    {
        RunOnUi(() => UpsertRemoteClientSnapshot(snapshot));
    }

    private void CollaborationHost_LogLineReceived(object? sender, CollaborationLogLine logLine)
    {
        RunOnUi(() => AppendRemoteLogLine(logLine));
    }

    private void CollaborationHost_ClientDisconnected(object? sender, string pcId)
    {
        RunOnUi(() =>
        {
            MarkRemoteClientDisconnected(pcId);
            StatusText = $"远程 PC 已断开：{pcId}";
        });
    }

    private void CollaborationClient_CommandReceived(object? sender, CollaborationCommand command)
    {
        RunOnUiAsync(() => SendIncomingCollaborationCommandAsync(command));
    }

    private void CollaborationClient_Disconnected(object? sender, string reason)
    {
        RunOnUi(() => BeginClientReconnect($"协作断开：{reason}"));
    }

    private void SerialWindow_LinesReceived(object? sender, IReadOnlyList<ReceivedLogLine> lines)
    {
        if (sender is not SerialWindowViewModel window)
        {
            return;
        }

        _ = PublishLocalLinesAsync(window, lines.ToArray());
    }

    private void SerialWindow_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SerialWindowViewModel window || window.IsRemote)
        {
            return;
        }

        if (e.PropertyName is nameof(SerialWindowViewModel.Title) or
            nameof(SerialWindowViewModel.PortName) or
            nameof(SerialWindowViewModel.BaudRate) or
            nameof(SerialWindowViewModel.PageIndex) or
            nameof(SerialWindowViewModel.AutoSaveEnabled) or
            nameof(SerialWindowViewModel.OwnerPcName) or
            nameof(SerialWindowViewModel.OwnerPcColor))
        {
            ScheduleAutoSave();
        }

        if (e.PropertyName is not (nameof(SerialWindowViewModel.Title) or
            nameof(SerialWindowViewModel.PortName) or
            nameof(SerialWindowViewModel.BaudRate) or
            nameof(SerialWindowViewModel.IsConnected)))
        {
            return;
        }

        _ = PublishLocalSnapshotIfClientRunningAsync();
    }

    private async Task PublishLocalSnapshotIfClientRunningAsync()
    {
        if (WorkspaceMode != WorkspaceMode.Client || !IsCollaborationRunning)
        {
            return;
        }

        try
        {
            await _collaborationClient.PublishSnapshotAsync(BuildLocalSnapshot()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RunOnUi(() =>
            {
                BeginClientReconnect($"协作断开：{ex.Message}");
            });
        }
    }

    private async Task PublishLocalLinesAsync(SerialWindowViewModel window, IReadOnlyList<ReceivedLogLine> lines)
    {
        if (window.IsRemote || WorkspaceMode != WorkspaceMode.Client || !IsCollaborationRunning)
        {
            return;
        }

        try
        {
            foreach (var line in lines)
            {
                await _collaborationClient.PublishLogLineAsync(window.Id, line).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            RunOnUi(() =>
            {
                BeginClientReconnect($"协作断开：{ex.Message}");
            });
        }
    }

    private async Task SendIncomingCollaborationCommandAsync(CollaborationCommand command)
    {
        var window = SerialWindows.FirstOrDefault(item => !item.IsRemote && item.Id == command.WindowId);
        if (window is null)
        {
            StatusText = $"远程命令目标不存在：{command.WindowId}";
            return;
        }

        if (!window.IsConnected)
        {
            StatusText = $"远程命令跳过，串口未连接：{window.Title}";
            return;
        }

        try
        {
            await window.SendAsync(command.Payload, CancellationToken.None);
            StatusText = $"已执行远程命令：{window.Title}";
        }
        catch (Exception ex)
        {
            StatusText = $"远程命令失败：{ex.Message}";
        }
    }

    private void UpsertRemoteClientSnapshot(CollaborationClientSnapshot snapshot)
    {
        var incomingRemoteIds = snapshot.Windows
            .Select(window => SerialWindowViewModel.CreateRemoteId(snapshot.PcId, window.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var staleWindow in SerialWindows
            .Where(window => window.IsRemote &&
                string.Equals(window.OwnerPcId, snapshot.PcId, StringComparison.OrdinalIgnoreCase) &&
                !incomingRemoteIds.Contains(window.Id))
            .ToList())
        {
            SerialWindows.Remove(staleWindow);
            UnregisterSerialWindow(staleWindow);
            staleWindow.Dispose();
        }

        foreach (var remoteSnapshot in snapshot.Windows)
        {
            var remoteId = SerialWindowViewModel.CreateRemoteId(snapshot.PcId, remoteSnapshot.Id);
            var existingWindow = SerialWindows.FirstOrDefault(window => window.Id == remoteId);
            if (existingWindow is null)
            {
                var remoteWindow = SerialWindowViewModel.CreateRemote(
                    snapshot,
                    remoteSnapshot,
                    (windowId, payload, cancellationToken) =>
                        _collaborationHost.SendCommandAsync(snapshot.PcId, windowId, payload, cancellationToken));
                remoteWindow.PageIndex = FindPageForNewWindow();
                RegisterSerialWindow(remoteWindow);
                continue;
            }

            existingWindow.UpdateRemoteSnapshot(
                snapshot,
                remoteSnapshot,
                (windowId, payload, cancellationToken) =>
                    _collaborationHost.SendCommandAsync(snapshot.PcId, windowId, payload, cancellationToken));
        }

        CommandPanel.SyncCommandGroupTargets();
        RemoveWindowCommand.RaiseCanExecuteChanged();
        StatusText = $"已接入远程 PC：{snapshot.PcName}";
    }

    private void AppendRemoteLogLine(CollaborationLogLine logLine)
    {
        var remoteId = SerialWindowViewModel.CreateRemoteId(logLine.PcId, logLine.WindowId);
        var window = SerialWindows.FirstOrDefault(item => item.Id == remoteId);
        window?.AppendRemoteLine(logLine.ToReceivedLogLine());
    }

    private void MarkRemoteClientDisconnected(string pcId)
    {
        foreach (var window in SerialWindows.Where(window =>
            window.IsRemote &&
            string.Equals(window.OwnerPcId, pcId, StringComparison.OrdinalIgnoreCase)))
        {
            window.SetRemoteOnline(false);
        }
    }

    private void RemoveRemoteWindows(string? pcId = null)
    {
        foreach (var window in SerialWindows
            .Where(window => window.IsRemote &&
                (string.IsNullOrWhiteSpace(pcId) ||
                    string.Equals(window.OwnerPcId, pcId, StringComparison.OrdinalIgnoreCase)))
            .ToList())
        {
            SerialWindows.Remove(window);
            UnregisterSerialWindow(window);
            window.Dispose();
        }

        CommandPanel.SyncCommandGroupTargets();
        RemoveWindowCommand.RaiseCanExecuteChanged();
    }

    private int FindPageForNewWindow()
    {
        for (var pageIndex = 0; pageIndex < PageCount; pageIndex++)
        {
            if (SerialWindows.Count(window => window.PageIndex == pageIndex) < 6)
            {
                return pageIndex;
            }
        }

        Layout.EnsurePageCount(PageCount + 1);
        return PageCount - 1;
    }

    private void BeginClientReconnect(string reason)
    {
        if (WorkspaceMode != WorkspaceMode.Client || _isDisposed)
        {
            return;
        }

        IsCollaborationRunning = false;
        IsCollaborationReconnectPending = true;
        CollaborationRunStatusText = $"{reason}，等待重连";
        StatusText = CollaborationRunStatusText;
        UpdateCollaborationCommands();
    }

    private void TryAutoReconnectCollaboration()
    {
        if (!IsCollaborationReconnectPending ||
            _isCollaborationReconnectInProgress ||
            WorkspaceMode != WorkspaceMode.Client ||
            _isDisposed)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        if (now - _lastCollaborationReconnectAttemptUtc < TimeSpan.FromSeconds(3))
        {
            return;
        }

        _lastCollaborationReconnectAttemptUtc = now;
        _isCollaborationReconnectInProgress = true;
        _ = TryAutoReconnectCollaborationAsync();
    }

    private async Task TryAutoReconnectCollaborationAsync()
    {
        try
        {
            await _collaborationClient.ConnectAsync(HostAddress, HostPort, BuildLocalSnapshot()).ConfigureAwait(false);
            RunOnUi(() =>
            {
                IsCollaborationRunning = true;
                IsCollaborationReconnectPending = false;
                CollaborationRunStatusText = "已重连主机";
                StatusText = $"已重连主机 {HostAddress}:{HostPort}";
                UpdateCollaborationCommands();
            });
        }
        catch (Exception ex)
        {
            RunOnUi(() => BeginClientReconnect($"重连失败：{ex.Message}"));
        }
        finally
        {
            _isCollaborationReconnectInProgress = false;
        }
    }

    private void UpdateCollaborationCommands()
    {
        OnPropertyChanged(nameof(CollaborationRunStatusText));
        OnPropertyChanged(nameof(StartCollaborationActionText));
        StartCollaborationCommand.RaiseCanExecuteChanged();
        StopCollaborationCommand.RaiseCanExecuteChanged();
    }

    private void PersistedCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ScheduleAutoSave();
    }

    private void AutoSaveTimer_Tick(object? sender, EventArgs e)
    {
        _autoSaveTimer.Stop();
        if (_isDisposed || _isLoadingWorkspace)
        {
            return;
        }

        try
        {
            SaveWorkspace(updateStatus: false);
        }
        catch (Exception ex)
        {
            StatusText = $"自动保存失败：{ex.Message}";
        }
    }

    private void ScheduleAutoSave()
    {
        if (_isDisposed || _isLoadingWorkspace)
        {
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(ScheduleAutoSave));
            return;
        }

        _autoSaveTimer.Stop();
        _autoSaveTimer.Start();
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action);
    }

    private static void RunOnUiAsync(Func<Task> action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            _ = action();
            return;
        }

        dispatcher.BeginInvoke(new Action(() => _ = action()));
    }

    private void ForwardLayoutPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(e.PropertyName);
        ScheduleAutoSave();
    }

    private void ForwardCommandPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(e.PropertyName);
        ScheduleAutoSave();
    }

    private void ForwardCollaborationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        Collaboration.ApplyOwnership(SerialWindows);
        OnPropertyChanged(e.PropertyName);
        OnPropertyChanged(nameof(IsCollaborationNetworked));
        OnPropertyChanged(nameof(CollaborationStatusText));
        UpdateCollaborationCommands();
        ScheduleAutoSave();
        _ = PublishLocalSnapshotIfClientRunningAsync();
    }

    public void Dispose()
    {
        _isDisposed = true;
        _autoSaveTimer.Stop();
        _reconnectTimer?.Stop();
        CommandHistory.CollectionChanged -= PersistedCollectionChanged;
        CommandGroups.CollectionChanged -= PersistedCollectionChanged;
        ImportedAtCommandSets.CollectionChanged -= PersistedCollectionChanged;
        Layout.PropertyChanged -= ForwardLayoutPropertyChanged;
        CommandPanel.PropertyChanged -= ForwardCommandPanelPropertyChanged;
        Collaboration.PropertyChanged -= ForwardCollaborationPropertyChanged;
        _collaborationHost.ClientSnapshotReceived -= CollaborationHost_ClientSnapshotReceived;
        _collaborationHost.LogLineReceived -= CollaborationHost_LogLineReceived;
        _collaborationHost.ClientDisconnected -= CollaborationHost_ClientDisconnected;
        _collaborationClient.CommandReceived -= CollaborationClient_CommandReceived;
        _collaborationClient.Disconnected -= CollaborationClient_Disconnected;
        _collaborationClient.DisconnectAsync().GetAwaiter().GetResult();
        _collaborationHost.StopAsync().GetAwaiter().GetResult();
        CommandPanel.Dispose();
        foreach (var window in SerialWindows)
        {
            UnregisterSerialWindow(window);
            window.Dispose();
        }
    }
}
