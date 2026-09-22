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
    private readonly WorkspaceConfig? _initialConfiguration;
    private readonly Action? _saveCatalog;
    public string WorkspaceId { get; }
    private string _workspaceName = "默认测试";
    public string WorkspaceName
    {
        get => _workspaceName;
        set { if (SetProperty(ref _workspaceName, value)) _ = PublishLocalSnapshotIfClientRunningAsync(); }
    }
    public Action<SerialWindowViewModel>? ConfigurePortOwnership { get; set; }
    public Func<IReadOnlyList<SerialWindowViewModel>, IReadOnlySet<string>>? PrepareBatchConnections { get; set; }
    private string WorkspaceLogRootDirectory => string.IsNullOrEmpty(WorkspaceId)
        ? LogRootDirectory : Path.Combine(LogRootDirectory, LogSessionPathFactory.GetWorkspaceDirectoryName(WorkspaceName));

    public bool HasRunningTests => SerialWindows.Any(w => !w.IsRemote && (w.IsConnected || w.IsConnectionPending || w.WantsConnection)) ||
        CommandPanel.IsSingleCommandLoopRunning || CommandPanel.IsCommandGroupLoopRunning || IsCollaborationRunning || _collaborationRequested;

    public async Task StopWorkspaceAsync()
    {
        await CommandPanel.StopLoopsAsync();
        foreach (var window in SerialWindows.Where(w => !w.IsRemote)) window.Disconnect();
        await StopCollaborationAsync();
    }
    private readonly DispatcherTimer _autoSaveTimer;
    private readonly DispatcherTimer? _reconnectTimer;
    private readonly CollaborationHostService _collaborationHost = new();
    private readonly CollaborationClientService _collaborationClient = new();
    private readonly SemaphoreSlim _collaborationLifecycle = new(1, 1);
    private CancellationTokenSource _collaborationLifetime = new();
    private bool _collaborationRequested;
    private int _collaborationEpoch;
    private readonly Func<string, string, bool> _confirmDelete;
    // 同名工作区也必须分配不同批次，避免同一毫秒创建时共用日志目录。
    private static readonly object _logSessionLock = new();
    private readonly List<ShortcutBindingConfig> _shortcutBindings = [];
    private readonly Dictionary<string, CollaborationClientSnapshot> _sharedDirectory = new(StringComparer.Ordinal);
    private readonly List<RemoteWindowSubscription> _subscriptions = [];
    public IReadOnlyList<RemoteWindowSubscription> Subscriptions => _subscriptions;
    public IReadOnlyList<CollaborationClientSnapshot> SharedDirectory => _sharedDirectory.Values.ToArray();
    public event EventHandler? SharedDirectoryChanged;
    private string LocalConnectionId => CollaborationIdentity.Connection(LocalPcId, WorkspaceId);

    public async Task SetSubscriptionsAsync(IEnumerable<RemoteWindowSubscription> subscriptions)
    {
        var selected = subscriptions.DistinctBy(s => s.Id).ToList();
        var retained = selected.Select(s => SerialWindowViewModel.CreateRemoteId(s.Source.ConnectionId, s.Window.Id)).ToHashSet();
        foreach (var window in SerialWindows.Where(w => w.IsRemote && !retained.Contains(w.Id)).ToArray())
        {
            await CommandPanel.StopLoopsAsync(window.Id);
            SerialWindows.Remove(window);
            UnregisterSerialWindow(window);
            window.Dispose();
        }
        _subscriptions.Clear();
        _subscriptions.AddRange(selected);
        foreach (var snapshot in _sharedDirectory.Values.ToArray()) UpsertRemoteClientSnapshot(snapshot);
        _collaborationHost.SetSubscriptions(_subscriptions.Select(s => s.Id).ToArray());
        await _collaborationClient.SetSubscriptionsAsync(_subscriptions.Select(s => s.Id).ToArray());
        ScheduleAutoSave();
    }

    public void SetAllLocalSharing(bool enabled)
    {
        foreach (var window in SerialWindows.Where(w => !w.IsRemote)) window.IsShared = enabled;
    }
    private string _logRootDirectory = ApplicationDataPaths.LogDirectory;
    private int _maxLogFileSizeMegabytes = WorkspaceConfig.DefaultMaxLogFileSizeMegabytes;
    private int _receiveSilenceReconnectSeconds;
    private string? _currentLogSessionDirectory;
    private string _collaborationRunStatusText = "未启动";
    private bool _isCollaborationRunning;
    private bool _isCollaborationReconnectPending;
    private bool _isCollaborationReconnectInProgress;
    private bool _isLoadingWorkspace;
    private bool _isDisposed;
    private DateTimeOffset _lastCollaborationReconnectAttemptUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPortRefreshAttemptUtc = DateTimeOffset.MinValue;
    private string _themeColor = "#0B75B7";
    private string _statusText = "就绪";

    public MainViewModel()
        : this(ApplicationDataPaths.WorkspaceFile, startReconnectTimer: true)
    {
    }

    public MainViewModel(
        string workspacePath,
        bool startReconnectTimer = true,
        Func<string, string, bool>? confirmDelete = null,
        WorkspaceConfig? configuration = null,
        string workspaceId = "",
        string workspaceName = "默认测试",
        Action? saveCatalog = null)
    {
        _workspacePath = workspacePath;
        _initialConfiguration = configuration;
        _saveCatalog = saveCatalog;
        WorkspaceId = workspaceId;
        WorkspaceName = workspaceName;
        _confirmDelete = confirmDelete ?? ConfirmDeleteWithDialog;
        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _autoSaveTimer.Tick += AutoSaveTimer_Tick;

        Layout = new WorkspaceLayoutViewModel(SerialWindows, text => StatusText = text);
        CommandPanel = new CommandPanelViewModel(SerialWindows, text => StatusText = text, _confirmDelete);
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
        _collaborationClient.SnapshotReceived += CollaborationClient_SnapshotReceived;
        _collaborationClient.LogLineReceived += CollaborationClient_LogLineReceived;
        _collaborationClient.PeerDisconnected += CollaborationClient_PeerDisconnected;
        _collaborationClient.Disconnected += CollaborationClient_Disconnected;

        SaveWorkspaceCommand = new RelayCommand(SaveWorkspace);
        AddWindowCommand = new RelayCommand(AddWindow);
        AddPageCommand = Layout.AddPageCommand;
        RemoveCurrentPageCommand = new RelayCommand(RemoveCurrentPage, () => Layout.RemoveCurrentPageCommand.CanExecute(null));
        RemoveWindowCommand = new RelayCommand(RemoveWindow, parameter => parameter is SerialWindowViewModel && SerialWindows.Count > 1);
        ConnectAllCommand = new RelayCommand(ConnectAll);
        DisconnectAllCommand = new RelayCommand(DisconnectAll);
        ToggleAllConnectionsCommand = new RelayCommand(ToggleAllConnections);
        NewLogSessionCommand = new RelayCommand(StartNewLogSession);
        StartCollaborationCommand = new AsyncRelayCommand(StartCollaborationAsync, () => WorkspaceMode != WorkspaceMode.Local && !IsCollaborationRunning);
        StopCollaborationCommand = new AsyncRelayCommand(StopCollaborationAsync, () => IsCollaborationRunning || _collaborationRequested);
        ToggleCollaborationCommand = new AsyncRelayCommand(
            ToggleCollaborationAsync,
            () => WorkspaceMode != WorkspaceMode.Local);

        _isLoadingWorkspace = true;
        try
        {
            LoadWorkspace();
            if (SerialWindows.Count == 0 && configuration is null)
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

    public IReadOnlyList<ShortcutBindingConfig> ShortcutBindings => _shortcutBindings;

    public IReadOnlyList<WorkspaceModeOption> WorkspaceModeOptions => Collaboration.WorkspaceModeOptions;

    public IReadOnlyList<PcColorOption> PcColorOptions => Collaboration.PcColorOptions;

    public IReadOnlyList<PcColorOption> ThemeColorOptions
    {
        get
        {
            if (Collaboration.PcColorOptions.Any(option =>
                    string.Equals(option.Hex, ThemeColor, StringComparison.OrdinalIgnoreCase)))
            {
                return Collaboration.PcColorOptions;
            }

            return [.. Collaboration.PcColorOptions, new PcColorOption("自定义", ThemeColor)];
        }
    }

    public AsyncRelayCommand SendCommand => CommandPanel.SendCommand;

    public RelayCommand ToggleSingleCommandLoopCommand => CommandPanel.ToggleSingleCommandLoopCommand;

    public RelayCommand SaveWorkspaceCommand { get; }

    public RelayCommand AddWindowCommand { get; }

    public RelayCommand AddPageCommand { get; }

    public RelayCommand RemoveCurrentPageCommand { get; }

    public RelayCommand RemoveWindowCommand { get; }

    public RelayCommand ConnectAllCommand { get; }

    public RelayCommand DisconnectAllCommand { get; }

    public RelayCommand ToggleAllConnectionsCommand { get; }

    public RelayCommand NewLogSessionCommand { get; }

    public AsyncRelayCommand StartCollaborationCommand { get; }

    public AsyncRelayCommand StopCollaborationCommand { get; }

    public AsyncRelayCommand ToggleCollaborationCommand { get; }

    public RelayCommand PreviousPageCommand => Layout.PreviousPageCommand;

    public RelayCommand NextPageCommand => Layout.NextPageCommand;

    public RelayCommand ToggleWindowExpansionCommand => Layout.ToggleWindowExpansionCommand;

    public RelayCommand AddCommandGroupCommand => CommandPanel.AddCommandGroupCommand;

    public RelayCommand DuplicateCommandGroupCommand => CommandPanel.DuplicateCommandGroupCommand;

    public RelayCommand DeleteCommandGroupCommand => CommandPanel.DeleteCommandGroupCommand;

    public RelayCommand AddCommandToGroupCommand => CommandPanel.AddCommandToGroupCommand;

    public RelayCommand RemoveCommandFromGroupCommand => CommandPanel.RemoveCommandFromGroupCommand;

    public RelayCommand ClearCommandHistoryCommand => CommandPanel.ClearCommandHistoryCommand;

    public RelayCommand RemoveCommandHistoryItemCommand => CommandPanel.RemoveCommandHistoryItemCommand;

    public RelayCommand FillSingleCommandFromHistoryCommand => CommandPanel.FillSingleCommandFromHistoryCommand;

    public RelayCommand AddHistoryCommandToGroupCommand => CommandPanel.AddHistoryCommandToGroupCommand;

    public RelayCommand RemoveImportedAtCommandCommand => CommandPanel.RemoveImportedAtCommandCommand;

    public RelayCommand EditImportedAtCommandCommand => CommandPanel.EditImportedAtCommandCommand;

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

    public bool IsAllSingleCommandTargetsSelected
    {
        get => CommandPanel.IsAllSingleCommandTargetsSelected;
        set => CommandPanel.IsAllSingleCommandTargetsSelected = value;
    }

    public bool IsAllCommandGroupTargetsSelected
    {
        get => CommandPanel.IsAllCommandGroupTargetsSelected;
        set => CommandPanel.IsAllCommandGroupTargetsSelected = value;
    }

    public void ApplyCommandToActiveEditor(string command)
    {
        CommandPanel.ApplyCommandToActiveEditor(command);
    }

    public RelayCommand SetCommandPanelDockCommand => Layout.SetCommandPanelDockCommand;

    public RelayCommand FloatCommandPanelCommand => Layout.FloatCommandPanelCommand;

    public RelayCommand RestoreCommandPanelCommand => Layout.RestoreCommandPanelCommand;

    public RelayCommand ToggleCommandPanelVisibilityCommand => Layout.ToggleCommandPanelVisibilityCommand;

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

    public string LocalPcHeaderBrush => Collaboration.LocalPcHeaderBrush;

    public PcColorOption? SelectedPcColorOption
    {
        get => Collaboration.SelectedPcColorOption;
        set => Collaboration.SelectedPcColorOption = value;
    }

    public string ThemeColor
    {
        get => _themeColor;
        set
        {
            if (string.IsNullOrWhiteSpace(value) ||
                !SetProperty(ref _themeColor, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ThemeSoftBrush));
            OnPropertyChanged(nameof(ThemeColorOptions));
            OnPropertyChanged(nameof(SelectedThemeColorOption));
            Collaboration.LocalPcColor = value;
            ScheduleAutoSave();
        }
    }

    public string ThemeSoftBrush => SerialWindowViewModel.CreateOwnerHeaderBrush(ThemeColor);

    public PcColorOption? SelectedThemeColorOption
    {
        get => ThemeColorOptions.FirstOrDefault(option =>
            string.Equals(option.Hex, ThemeColor, StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null)
            {
                ThemeColor = value.Hex;
            }
        }
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
                OnPropertyChanged(nameof(ToggleCollaborationActionText));
                StartCollaborationCommand.RaiseCanExecuteChanged();
                StopCollaborationCommand.RaiseCanExecuteChanged();
                ToggleCollaborationCommand.RaiseCanExecuteChanged();
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

    public bool HasConnectedLocalSerialWindows => SerialWindows.Any(window => !window.IsRemote && window.IsConnected);

    public bool AreAllLocalSerialWindowsConnected
    {
        get
        {
            var localWindows = SerialWindows.Where(window => !window.IsRemote).ToArray();
            return localWindows.Length > 0 && localWindows.All(window => window.IsConnected);
        }
    }

    public string ToggleAllConnectionsActionText => HasConnectedLocalSerialWindows ? "断开全部" : "连接全部";

    public string StartCollaborationActionText => WorkspaceMode switch
    {
        WorkspaceMode.Host => "启动主机",
        WorkspaceMode.Client => "连接主机",
        _ => "本地"
    };

    public string ToggleCollaborationActionText => IsCollaborationRunning || _collaborationRequested
        ? WorkspaceMode == WorkspaceMode.Host ? "停止主机" : "断开主机"
        : StartCollaborationActionText;

    public int CurrentPageIndex
    {
        get => Layout.CurrentPageIndex;
        set => Layout.CurrentPageIndex = value;
    }

    public int PageCount => Layout.PageCount;

    public IReadOnlyList<int> PageNumbers => Layout.PageNumbers;

    public int SelectedPageNumber
    {
        get => Layout.SelectedPageNumber;
        set => Layout.SelectedPageNumber = value;
    }

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
                lock (_logSessionLock)
                {
                    _currentLogSessionDirectory = null;
                }

                foreach (var window in SerialWindows)
                {
                    window.ApplyLogRoot(value);
                }
                ScheduleAutoSave();
            }
        }
    }

    public IReadOnlyList<int> LogFileSizeOptions { get; } = [50, 100, 200, 500, 1024];

    public int MaxLogFileSizeMegabytes
    {
        get => _maxLogFileSizeMegabytes;
        set
        {
            var normalized = Math.Clamp(
                value,
                WorkspaceConfig.MinLogFileSizeMegabytes,
                WorkspaceConfig.MaxAllowedLogFileSizeMegabytes);
            if (!SetProperty(ref _maxLogFileSizeMegabytes, normalized))
            {
                return;
            }

            foreach (var window in SerialWindows)
            {
                window.ApplyMaxLogFileSizeMegabytes(normalized);
            }

            ScheduleAutoSave();
        }
    }

    public IReadOnlyList<int> ReceiveSilenceReconnectOptions { get; } = [0, 90, 300, 600, 1800, 3600];

    public int ReceiveSilenceReconnectSeconds
    {
        get => _receiveSilenceReconnectSeconds;
        set
        {
            var normalized = Math.Clamp(value, 0, WorkspaceConfig.MaxReceiveSilenceReconnectSeconds);
            if (!SetProperty(ref _receiveSilenceReconnectSeconds, normalized))
            {
                return;
            }

            foreach (var window in SerialWindows)
            {
                window.ApplyReceiveSilenceReconnectSeconds(normalized);
            }

            ScheduleAutoSave();
        }
    }

    public string? CurrentLogSessionDirectory
    {
        get
        {
            lock (_logSessionLock)
            {
                return _currentLogSessionDirectory;
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

    public bool IsCommandPanelHidden
    {
        get => Layout.IsCommandPanelHidden;
        set => Layout.IsCommandPanelHidden = value;
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

    public string CommandPanelVisibilityActionText => Layout.CommandPanelVisibilityActionText;

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

    public void SetShortcutBindings(IEnumerable<ShortcutBindingConfig> bindings)
    {
        _shortcutBindings.Clear();
        _shortcutBindings.AddRange(bindings.Select(binding => new ShortcutBindingConfig
        {
            ActionId = binding.ActionId,
            Gesture = binding.Gesture
        }));
        SaveWorkspace(updateStatus: false);
    }

    public void SaveWorkspace()
    {
        SaveWorkspace(updateStatus: true);
    }

    private void SaveWorkspace(bool updateStatus)
    {
        if (_saveCatalog is not null) _saveCatalog();
        else WorkspaceConfigStore.Save(_workspacePath, ExportConfiguration());
        if (updateStatus) StatusText = $"工作区已保存：{_workspacePath}";
    }

    public WorkspaceConfig ExportConfiguration()
    {
        var config = new WorkspaceConfig
        {
            LogRootDirectory = LogRootDirectory,
            CommandText = CommandText,
            SelectedLineEnding = SelectedLineEnding,
            SelectedCommandGroupName = SelectedCommandGroup?.Name,
            SelectedCommandPanelTabIndex = SelectedCommandPanelTabIndex,
            Subscriptions = CaptureSubscriptions(),
            MaxLogFileSizeMegabytes = MaxLogFileSizeMegabytes,
            ReceiveSilenceReconnectSeconds = ReceiveSilenceReconnectSeconds,
            ThemeColor = ThemeColor,
            SelectedPageIndex = CurrentPageIndex,
            PageCount = PageCount,
            CommandPanelDock = CommandPanelDock,
            IsCommandPanelFloating = IsCommandPanelFloating,
            IsCommandPanelHidden = IsCommandPanelHidden,
            ExpandedWindowIds = Layout.ExpandedWindowIds.ToList(),
            SingleCommandLoopIntervalMilliseconds = SingleCommandLoopIntervalMilliseconds,
            SingleCommandLoopCount = SingleCommandLoopCount,
            CommandHistory = CommandHistory.ToList(),
            AtCommandSets = CommandPanel.ToAtCommandSetConfigs().ToList(),
            SelectedAtCommandSetName = CommandPanel.SelectedAtCommandSetName,
            ShortcutBindings = _shortcutBindings.Select(binding => new ShortcutBindingConfig
            {
                ActionId = binding.ActionId,
                Gesture = binding.Gesture
            }).ToList(),
            SerialWindows = SerialWindows.Where(window => !window.IsRemote).Select(window => new SerialWindowConfig
            {
                Id = window.Id,
                Title = window.Title,
                PortName = window.PortName,
                BaudRate = window.BaudRate,
                PageIndex = window.PageIndex,
                PagePosition = window.PagePosition,
                OwnerPcId = window.OwnerPcId,
                OwnerPcName = window.OwnerPcName,
                OwnerPcColor = window.OwnerPcColor,
                AutoSaveEnabled = window.AutoSaveEnabled,
                IsShared = window.IsShared,
                IsSelectedForSend = window.IsSelectedForSend
            }).ToList(),
            CommandGroups = CommandGroups.Select(group => group.ToConfig()).ToList()
        };

        Collaboration.SaveToConfig(config);
        return config;
    }

    private List<RemoteWindowSubscription> CaptureSubscriptions()
    {
        foreach (var subscription in _subscriptions)
        {
            var window = SerialWindows.FirstOrDefault(w => w.Id == SerialWindowViewModel.CreateRemoteId(subscription.Source.ConnectionId, subscription.Window.Id));
            if (window is null) continue;
            subscription.AutoSaveEnabled = window.AutoSaveEnabled;
            subscription.IsSelectedForSend = window.IsSelectedForSend;
            subscription.PageIndex = window.PageIndex;
            subscription.PagePosition = window.PagePosition;
        }
        return _subscriptions.ToList();
    }

    private void AddWindow(object? parameter)
    {
        var targetSlot = parameter as SerialWindowSlotViewModel;
        if (targetSlot is null && !Layout.CurrentPageHasFreeSlot)
        {
            AddPageCommand.Execute(null);
            targetSlot = Layout.GetFirstFreeSlot(CurrentPageIndex);
        }

        targetSlot ??= Layout.GetFirstFreeSlot(CurrentPageIndex);
        AddWindow($"串口 {SerialWindows.Count + 1}", targetSlot.PageIndex, targetSlot.PagePosition);
    }

    private void AddWindow(string title, int? pageIndex = null, int? pagePosition = null)
    {
        var targetPageIndex = pageIndex ?? CurrentPageIndex;
        var window = new SerialWindowViewModel(Guid.NewGuid().ToString("N"), title)
        {
            AutoSaveEnabled = true,
            PageIndex = targetPageIndex,
            PagePosition = pagePosition ?? Layout.GetFirstFreeSlot(targetPageIndex).PagePosition
        };
        window.ApplyLogRoot(LogRootDirectory);
        window.ApplyMaxLogFileSizeMegabytes(MaxLogFileSizeMegabytes);
        Collaboration.ApplyLocalOwner(window);
        RegisterSerialWindow(window);
        RemoveWindowCommand.RaiseCanExecuteChanged();
        RaiseAllConnectionsStateChanged();
        _ = PublishLocalSnapshotIfClientRunningAsync();
    }

    private void ConnectAll()
    {
        var allowed = PrepareBatchConnections?.Invoke(SerialWindows.Where(w => !w.IsRemote).ToArray());
        var attempts = 0;
        string? sessionDirectory = null;
        foreach (var window in SerialWindows.Where(window => !window.IsRemote))
        {
            if (allowed is not null && !allowed.Contains(window.Id)) continue;
            window.RefreshPorts();
            if (string.IsNullOrWhiteSpace(window.PortName) || window.IsConnected)
            {
                continue;
            }

            try
            {
                sessionDirectory ??= GetOrCreateLogSessionDirectory();
            }
            catch (Exception exception)
            {
                StatusText = $"连接失败：日志目录不可用：{exception.Message}";
                return;
            }

            window.Connect(sessionDirectory);
            attempts++;
        }

        StatusText = $"连接全部完成：已尝试 {attempts} 个窗口";
        RaiseAllConnectionsStateChanged();
        _ = PublishLocalSnapshotIfClientRunningAsync();
    }

    private void StartNewLogSession()
    {
        string sessionDirectory;
        try
        {
            sessionDirectory = CreateNewLogSessionDirectory();
        }
        catch (Exception exception)
        {
            StatusText = $"新建日志批次失败：{exception.Message}";
            return;
        }

        foreach (var window in SerialWindows)
        {
            window.BeginNewLogSession(sessionDirectory);
        }

        StatusText = $"已新建日志批次：{sessionDirectory}";
    }

    private void ToggleAllConnections()
    {
        if (HasConnectedLocalSerialWindows)
        {
            DisconnectAll();
            return;
        }

        ConnectAll();
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
            disconnected++;
            window.Disconnect();
        }

        StatusText = $"断开全部完成：已断开 {disconnected} 个窗口";
        RaiseAllConnectionsStateChanged();
        _ = PublishLocalSnapshotIfClientRunningAsync();
    }

    private void RemoveWindow(object? parameter)
    {
        if (parameter is not SerialWindowViewModel window || SerialWindows.Count <= 1)
        {
            StatusText = "至少保留一个串口窗口";
            return;
        }

        if (!ConfirmDelete("删除窗口", $"确定删除窗口“{window.Title}”吗？当前界面中的日志会移除，已写入的日志文件不会删除。"))
        {
            StatusText = $"已取消删除窗口：{window.Title}";
            return;
        }

        var removed = SerialWindows.Remove(window);
        if (!removed)
        {
            return;
        }

        UnregisterSerialWindow(window);
        _ = CommandPanel.StopLoopsAsync(window.Id);
        if (window.IsRemote)
        {
            var remaining = _subscriptions.Where(s => SerialWindowViewModel.CreateRemoteId(s.Source.ConnectionId, s.Window.Id) != window.Id).ToArray();
            _ = SetSubscriptionsAsync(remaining);
        }
        window.Dispose();
        CommandPanel.SyncCommandGroupTargets();
        RemoveWindowCommand.RaiseCanExecuteChanged();
        RaiseAllConnectionsStateChanged();
        StatusText = $"已删除窗口：{window.Title}";
        _ = PublishLocalSnapshotIfClientRunningAsync();
    }

    private void RemoveCurrentPage()
    {
        if (!Layout.RemoveCurrentPageCommand.CanExecute(null))
        {
            StatusText = "仅当前页为空时可删除";
            return;
        }

        var pageLabel = PageLabel;
        if (!ConfirmDelete("删除页面", $"确定删除当前空白页（{pageLabel}）吗？"))
        {
            StatusText = $"已取消删除页面：{pageLabel}";
            return;
        }

        Layout.RemoveCurrentPageCommand.Execute(null);
    }

    private bool ConfirmDelete(string title, string message)
    {
        return _confirmDelete(title, message);
    }

    private static bool ConfirmDeleteWithDialog(string title, string message)
    {
        return SerialLog.App.Views.DeleteConfirmationWindow.Confirm(title, message);
    }

    private void LoadWorkspace()
    {
        var config = _initialConfiguration ?? WorkspaceConfigStore.Load(_workspacePath);
        CommandText = config.CommandText;
        SelectedLineEnding = config.SelectedLineEnding;
        SelectedCommandPanelTabIndex = config.SelectedCommandPanelTabIndex;
        _subscriptions.AddRange(config.Subscriptions);
        _collaborationHost.SetSubscriptions(_subscriptions.Select(s => s.Id).ToArray());
        _ = _collaborationClient.SetSubscriptionsAsync(_subscriptions.Select(s => s.Id).ToArray());
        Collaboration.LoadFromConfig(config);
        _themeColor = string.IsNullOrWhiteSpace(config.ThemeColor)
            ? Collaboration.LocalPcColor
            : config.ThemeColor;
        Collaboration.LocalPcColor = _themeColor;
        LogRootDirectory = ApplicationDataPaths.IsLegacyDefaultLogDirectory(config.LogRootDirectory)
            ? ApplicationDataPaths.LogDirectory
            : config.LogRootDirectory;
        MaxLogFileSizeMegabytes = config.MaxLogFileSizeMegabytes;
        ReceiveSilenceReconnectSeconds = config.ReceiveSilenceReconnectSeconds;
        CommandPanelDock = config.CommandPanelDock;
        IsCommandPanelHidden = config.IsCommandPanelHidden;
        IsCommandPanelFloating = config.IsCommandPanelFloating;
        SingleCommandLoopIntervalMilliseconds = config.SingleCommandLoopIntervalMilliseconds;
        SingleCommandLoopCount = config.SingleCommandLoopCount;
        _shortcutBindings.Clear();
        _shortcutBindings.AddRange(config.ShortcutBindings ?? []);
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
                IsShared = windowConfig.IsShared,
                IsSelectedForSend = windowConfig.IsSelectedForSend,
                OwnerPcId = windowConfig.OwnerPcId,
                OwnerPcName = windowConfig.OwnerPcName,
                OwnerPcColor = windowConfig.OwnerPcColor,
                PageIndex = windowConfig.PageIndex >= 0 ? windowConfig.PageIndex : index / 6,
                PagePosition = windowConfig.PagePosition >= 0 ? windowConfig.PagePosition : index % 6
            };
            window.ApplyLogRoot(LogRootDirectory);
            window.ApplyMaxLogFileSizeMegabytes(MaxLogFileSizeMegabytes);
            RegisterSerialWindow(window);
        }

        foreach (var subscription in _subscriptions)
        {
            var source = subscription.Source;
            Func<string, string, CancellationToken, Task>? sender = WorkspaceMode == WorkspaceMode.Host
                ? (windowId, payload, token) => _collaborationHost.SendCommandAsync(source.ConnectionId, windowId, payload, token)
                : null;
            var remote = SerialWindowViewModel.CreateRemote(source, subscription.Window with { IsConnected = false }, sender);
            remote.AutoSaveEnabled = subscription.AutoSaveEnabled;
            remote.IsSelectedForSend = subscription.IsSelectedForSend;
            remote.PageIndex = Math.Max(0, subscription.PageIndex);
            remote.PagePosition = subscription.PagePosition;
            remote.ApplyLogRoot(LogRootDirectory);
            remote.ApplyMaxLogFileSizeMegabytes(MaxLogFileSizeMegabytes);
            RegisterSerialWindow(remote);
        }
        Layout.RestoreExpandedWindowIds(config.ExpandedWindowIds);
        Collaboration.ApplyOwnership(SerialWindows);

        Layout.EnsurePageCount(Math.Max(
            config.PageCount,
            SerialWindows.Count == 0 ? 1 : SerialWindows.Max(window => window.PageIndex) + 1));

        foreach (var groupConfig in config.CommandGroups)
        {
            CommandGroups.Add(new CommandGroupEditorViewModel(groupConfig));
        }

        CommandPanel.SyncCommandGroupTargets();
        SelectedCommandGroup = CommandGroups.FirstOrDefault(g => g.Name == config.SelectedCommandGroupName) ?? CommandGroups.FirstOrDefault();
        CurrentPageIndex = config.SelectedPageIndex;
    }

    private void RegisterSerialWindow(SerialWindowViewModel window)
    {
        if (!window.IsRemote) ConfigurePortOwnership?.Invoke(window);
        window.ApplyReceiveSilenceReconnectSeconds(ReceiveSilenceReconnectSeconds);
        window.SetLogSessionDirectoryProvider(GetOrCreateLogSessionDirectory);
        window.LinesReceived += SerialWindow_LinesReceived;
        window.PropertyChanged += SerialWindow_PropertyChanged;
        SerialWindows.Add(window);
    }

    private string GetOrCreateLogSessionDirectory()
    {
        lock (_logSessionLock)
        {
            _currentLogSessionDirectory ??= CreateNewLogSessionDirectory();
            Directory.CreateDirectory(_currentLogSessionDirectory);
            return _currentLogSessionDirectory;
        }
    }

    private string CreateNewLogSessionDirectory()
    {
        lock (_logSessionLock)
        {
            var baseDirectory = LogSessionPathFactory.CreateSessionDirectory(
                WorkspaceLogRootDirectory,
                DateTimeOffset.Now);
            var sessionDirectory = baseDirectory;
            var suffix = 2;

            while (Directory.Exists(sessionDirectory)
                || string.Equals(sessionDirectory, _currentLogSessionDirectory, StringComparison.OrdinalIgnoreCase))
            {
                sessionDirectory = $"{baseDirectory}_{suffix++:D2}";
            }

            Directory.CreateDirectory(sessionDirectory);
            _currentLogSessionDirectory = sessionDirectory;
            return sessionDirectory;
        }
    }

    private void UnregisterSerialWindow(SerialWindowViewModel window)
    {
        window.LinesReceived -= SerialWindow_LinesReceived;
        window.PropertyChanged -= SerialWindow_PropertyChanged;
    }

    private Task StartCollaborationAsync()
    {
        ++_collaborationEpoch;
        _collaborationRequested = true;
        if (_collaborationLifetime.IsCancellationRequested)
        {
            _collaborationLifetime.Dispose();
            _collaborationLifetime = new CancellationTokenSource();
        }
        return RunCollaborationStartAsync();
    }

    private async Task RunCollaborationStartAsync()
    {
        await _collaborationLifecycle.WaitAsync();
        try { if (_collaborationRequested && !_isDisposed) await StartCollaborationCoreAsync(); }
        finally { _collaborationLifecycle.Release(); }
    }

    private async Task StartCollaborationCoreAsync()
    {
        try
        {
            if (WorkspaceMode == WorkspaceMode.Local)
            {
                _collaborationRequested = false;
                CollaborationRunStatusText = "本地模式";
                StatusText = "本地模式不需要启动协作。";
                return;
            }

            if (WorkspaceMode == WorkspaceMode.Host)
            {
                await _collaborationClient.DisconnectAsync();
                await _collaborationHost.StopAsync();
                await _collaborationHost.StartAsync(IPAddress.Any, HostPort);
                await _collaborationHost.PublishHostSnapshotAsync(BuildLocalSnapshot());
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

            await _collaborationHost.StopAsync();
            await _collaborationClient.ConnectAsync(HostAddress, HostPort, BuildLocalSnapshot(), _collaborationLifetime.Token);
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

                _collaborationRequested = false;
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
        ++_collaborationEpoch;
        _collaborationRequested = false;
        IsCollaborationReconnectPending = false;
        _collaborationLifetime.Cancel();
        await _collaborationLifecycle.WaitAsync();
        try
        {
            await _collaborationClient.DisconnectAsync();
            await _collaborationHost.StopAsync();
        }
        finally
        {
            RunOnUi(() =>
            {
                CaptureSubscriptions();
                _sharedDirectory.Clear();
                SharedDirectoryChanged?.Invoke(this, EventArgs.Empty);
                foreach (var window in SerialWindows.Where(w => w.IsRemote)) window.SetRemoteOnline(false);
                IsCollaborationRunning = false;
                IsCollaborationReconnectPending = false;
                CollaborationRunStatusText = "未启动";
                StatusText = "协作已停止";
                UpdateCollaborationCommands();
            });
            _collaborationLifecycle.Release();
        }
    }

    private Task ToggleCollaborationAsync()
    {
        return IsCollaborationRunning || _collaborationRequested
            ? StopCollaborationAsync()
            : StartCollaborationAsync();
    }

    private CollaborationClientSnapshot BuildLocalSnapshot()
    {
        return new CollaborationClientSnapshot(
            LocalPcId,
            LocalPcName,
            ThemeColor,
            SerialWindows
                .Where(window => !window.IsRemote && window.IsShared)
                .Select(ToSnapshot)
                .ToList(), WorkspaceId, WorkspaceName);
    }

    private static CollaborationWindowSnapshot ToSnapshot(SerialWindowViewModel window)
    {
        return new CollaborationWindowSnapshot(
            window.Id,
            window.Title,
            window.PortName,
            window.BaudRate,
            window.IsConnected,
            window.LineCount, window.IsShared);
    }

    private void CollaborationHost_ClientSnapshotReceived(object? sender, CollaborationClientSnapshot snapshot)
    {
        RunCollaborationOnUi(() => UpsertRemoteClientSnapshot(snapshot));
    }

    private void CollaborationHost_LogLineReceived(object? sender, CollaborationLogLine logLine)
    {
        RunCollaborationOnUi(() => AppendRemoteLogLine(logLine));
    }

    private void CollaborationHost_ClientDisconnected(object? sender, string pcId)
    {
        RunCollaborationOnUi(() =>
        {
            MarkRemoteClientDisconnected(pcId);
            StatusText = $"远程 PC 已断开：{pcId}";
        });
    }

    private void CollaborationClient_CommandReceived(object? sender, CollaborationCommand command)
    {
        RunCollaborationOnUi(() => _ = SendIncomingCollaborationCommandAsync(command));
    }

    private void CollaborationClient_SnapshotReceived(object? sender, CollaborationClientSnapshot snapshot)
    {
        if (string.Equals(snapshot.ConnectionId, LocalConnectionId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        RunCollaborationOnUi(() => UpsertRemoteClientSnapshot(snapshot));
    }

    private void CollaborationClient_LogLineReceived(object? sender, CollaborationLogLine logLine)
    {
        if (string.Equals(logLine.ConnectionId, LocalConnectionId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        RunCollaborationOnUi(() => AppendRemoteLogLine(logLine));
    }

    private void CollaborationClient_PeerDisconnected(
        object? sender,
        CollaborationPeerDisconnected peerDisconnected)
    {
        if (string.Equals(peerDisconnected.ConnectionId, LocalConnectionId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        RunCollaborationOnUi(() =>
        {
            MarkRemoteClientDisconnected(peerDisconnected.ConnectionId);
            StatusText = $"远端 PC 已断开：{peerDisconnected.PcId}";
        });
    }

    private void CollaborationClient_Disconnected(object? sender, string reason)
    {
        RunCollaborationOnUi(() =>
        {
            _sharedDirectory.Clear();
            SharedDirectoryChanged?.Invoke(this, EventArgs.Empty);
            foreach (var window in SerialWindows.Where(w => w.IsRemote)) window.SetRemoteOnline(false);
            BeginClientReconnect($"协作断开：{reason}");
        });
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
        if (sender is not SerialWindowViewModel window)
        {
            return;
        }

        if (e.PropertyName is nameof(SerialWindowViewModel.Title) or
            nameof(SerialWindowViewModel.PortName) or
            nameof(SerialWindowViewModel.BaudRate) or
            nameof(SerialWindowViewModel.PageIndex) or
            nameof(SerialWindowViewModel.PagePosition) or
            nameof(SerialWindowViewModel.AutoSaveEnabled) or
            nameof(SerialWindowViewModel.IsShared) or
            nameof(SerialWindowViewModel.IsSelectedForSend) or
            nameof(SerialWindowViewModel.OwnerPcName) or
            nameof(SerialWindowViewModel.OwnerPcColor))
        {
            ScheduleAutoSave();
        }

        if (window.IsRemote || e.PropertyName is not (nameof(SerialWindowViewModel.Title) or
            nameof(SerialWindowViewModel.IsShared) or
            nameof(SerialWindowViewModel.PortName) or
            nameof(SerialWindowViewModel.BaudRate) or
            nameof(SerialWindowViewModel.IsConnected)))
        {
            return;
        }

        if (e.PropertyName == nameof(SerialWindowViewModel.IsConnected))
        {
            RaiseAllConnectionsStateChanged();
        }

        _ = PublishLocalSnapshotIfClientRunningAsync();
    }

    private void RaiseAllConnectionsStateChanged()
    {
        OnPropertyChanged(nameof(HasConnectedLocalSerialWindows));
        OnPropertyChanged(nameof(AreAllLocalSerialWindowsConnected));
        OnPropertyChanged(nameof(ToggleAllConnectionsActionText));
    }

    private async Task PublishLocalSnapshotIfClientRunningAsync()
    {
        if (!IsCollaborationRunning)
        {
            return;
        }

        try
        {
            var snapshot = BuildLocalSnapshot();
            if (WorkspaceMode == WorkspaceMode.Host)
            {
                await _collaborationHost.PublishHostSnapshotAsync(snapshot);
            }
            else if (WorkspaceMode == WorkspaceMode.Client)
            {
                await _collaborationClient.PublishSnapshotAsync(snapshot);
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

    private async Task PublishLocalLinesAsync(SerialWindowViewModel window, IReadOnlyList<ReceivedLogLine> lines)
    {
        if (window.IsRemote || !window.IsShared || !IsCollaborationRunning)
        {
            return;
        }

        try
        {
            if (WorkspaceMode == WorkspaceMode.Host)
            {
                await _collaborationHost.PublishHostLogLinesAsync(
                    lines.Select(line => new CollaborationLogLine(
                        LocalPcId,
                        window.Id,
                        line.Timestamp,
                        line.Text, WorkspaceId)).ToArray());
            }
            else if (WorkspaceMode == WorkspaceMode.Client)
            {
                await _collaborationClient.PublishLogLinesAsync(window.Id, lines);
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

        if (!window.IsConnected || !window.IsShared || command.WorkspaceId != WorkspaceId)
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
        if (string.Equals(snapshot.ConnectionId, LocalConnectionId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _sharedDirectory[snapshot.ConnectionId] = snapshot;
        SharedDirectoryChanged?.Invoke(this, EventArgs.Empty);

        Func<string, string, CancellationToken, Task>? remoteCommandSender =
            WorkspaceMode == WorkspaceMode.Host
                ? (windowId, payload, cancellationToken) =>
                    _collaborationHost.SendCommandAsync(
                        snapshot.ConnectionId,
                        windowId,
                        payload,
                        cancellationToken)
                : null;

        var incomingRemoteIds = snapshot.Windows
            .Select(window => SerialWindowViewModel.CreateRemoteId(snapshot.ConnectionId, window.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var staleWindow in SerialWindows
            .Where(window => window.IsRemote &&
                string.Equals(window.OwnerPcId, snapshot.ConnectionId, StringComparison.OrdinalIgnoreCase) &&
                !incomingRemoteIds.Contains(window.Id))
            .ToList())
        {
            staleWindow.SetRemoteOnline(false);
            staleWindow.StatusText = "对方已停止共享";
        }

        foreach (var remoteSnapshot in snapshot.Windows)
        {
            var subscription = _subscriptions.FirstOrDefault(s => s.Source.ConnectionId == snapshot.ConnectionId && s.Window.Id == remoteSnapshot.Id);
            if (subscription is null || !remoteSnapshot.IsShared) continue;
            subscription.Source = snapshot with { Windows = [] };
            subscription.Window = remoteSnapshot;
            var remoteId = SerialWindowViewModel.CreateRemoteId(snapshot.ConnectionId, remoteSnapshot.Id);
            var existingWindow = SerialWindows.FirstOrDefault(window => window.Id == remoteId);
            if (existingWindow is null)
            {
                var remoteWindow = SerialWindowViewModel.CreateRemote(
                    snapshot,
                    remoteSnapshot,
                    remoteCommandSender);
                remoteWindow.ApplyLogRoot(LogRootDirectory);
                remoteWindow.ApplyMaxLogFileSizeMegabytes(MaxLogFileSizeMegabytes);
                remoteWindow.AutoSaveEnabled = subscription.AutoSaveEnabled;
                remoteWindow.IsSelectedForSend = subscription.IsSelectedForSend;
                if (subscription.PageIndex >= PageCount) Layout.EnsurePageCount(subscription.PageIndex + 1);
                var pageIndex = subscription.PageIndex >= 0 && Layout.PageHasFreeSlot(subscription.PageIndex)
                    ? subscription.PageIndex : FindPageForNewWindow();
                remoteWindow.PageIndex = pageIndex;
                remoteWindow.PagePosition = pageIndex == subscription.PageIndex && Layout.IsPagePositionFree(pageIndex, subscription.PagePosition)
                    ? subscription.PagePosition : Layout.GetFirstFreeSlot(pageIndex).PagePosition;
                RegisterSerialWindow(remoteWindow);
                continue;
            }

            existingWindow.UpdateRemoteSnapshot(
                snapshot,
                remoteSnapshot,
                remoteCommandSender);
        }

        CommandPanel.SyncCommandGroupTargets();
        RemoveWindowCommand.RaiseCanExecuteChanged();
        StatusText = $"已接入远程 PC：{snapshot.PcName}";
    }

    private void AppendRemoteLogLine(CollaborationLogLine logLine)
    {
        if (!_subscriptions.Any(s => s.Id == logLine.SubscriptionId) ||
            !_sharedDirectory.TryGetValue(logLine.ConnectionId, out var source) ||
            !source.Windows.Any(w => w.Id == logLine.WindowId && w.IsShared)) return;
        var remoteId = SerialWindowViewModel.CreateRemoteId(logLine.ConnectionId, logLine.WindowId);
        var window = SerialWindows.FirstOrDefault(item => item.Id == remoteId);
        window?.AppendRemoteLine(logLine.ToReceivedLogLine());
    }

    private void MarkRemoteClientDisconnected(string pcId)
    {
        _sharedDirectory.Remove(pcId);
        SharedDirectoryChanged?.Invoke(this, EventArgs.Empty);
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
            if (Layout.PageHasFreeSlot(pageIndex))
            {
                return pageIndex;
            }
        }

        Layout.EnsurePageCount(PageCount + 1);
        return PageCount - 1;
    }

    private void BeginClientReconnect(string reason)
    {
        if (WorkspaceMode != WorkspaceMode.Client || _isDisposed || !_collaborationRequested)
        {
            return;
        }

        IsCollaborationRunning = false;
        if (reason.Contains("不兼容", StringComparison.Ordinal))
        {
            _collaborationRequested = false;
            IsCollaborationReconnectPending = false;
            CollaborationRunStatusText = reason;
            StatusText = reason;
            UpdateCollaborationCommands();
            return;
        }
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
            await RunCollaborationStartAsync();
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
        OnPropertyChanged(nameof(ToggleCollaborationActionText));
        StartCollaborationCommand.RaiseCanExecuteChanged();
        StopCollaborationCommand.RaiseCanExecuteChanged();
        ToggleCollaborationCommand.RaiseCanExecuteChanged();
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

    private void RunCollaborationOnUi(Action action)
    {
        var epoch = _collaborationEpoch;
        RunOnUi(() => { if (!_isDisposed && epoch == _collaborationEpoch) action(); });
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
        RemoveCurrentPageCommand.RaiseCanExecuteChanged();
        ScheduleAutoSave();
    }

    private void ForwardCommandPanelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(e.PropertyName);
        ScheduleAutoSave();
    }

    private void ForwardCollaborationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_collaborationRequested && e.PropertyName == nameof(CollaborationViewModel.WorkspaceMode)) _ = StopCollaborationAsync();
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
        if (_isDisposed) return;
        _isDisposed = true;
        _collaborationRequested = false;
        _collaborationLifetime.Cancel();
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
        _collaborationClient.SnapshotReceived -= CollaborationClient_SnapshotReceived;
        _collaborationClient.LogLineReceived -= CollaborationClient_LogLineReceived;
        _collaborationClient.PeerDisconnected -= CollaborationClient_PeerDisconnected;
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
