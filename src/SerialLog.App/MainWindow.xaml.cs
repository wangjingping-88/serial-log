using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using SerialLog.App.Behaviors;
using SerialLog.App.Controls;
using SerialLog.App.Shortcuts;
using SerialLog.App.Updates;
using SerialLog.App.ViewModels;
using SerialLog.App.Views;
using SerialLog.Update;

namespace SerialLog.App;

public partial class MainWindow : Window
{
    private const string SerialWindowDragDataFormat = "SerialLog.SerialWindowId";
    private const string CommandPanelDragDataFormat = "SerialLog.CommandPanel";
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const uint ChooseColorRgbInit = 0x00000001;
    private const uint ChooseColorFullOpen = 0x00000002;
    private const string OnlineHelpUrl = "https://wangjingping-88.github.io/serial-log/help/";
    private readonly MainViewModel _viewModel;
    private readonly ShortcutManager _shortcutManager;
    private readonly UpdateService _updateService;
    private readonly CancellationTokenSource _updateCancellation = new();
    private Point _serialDragStartPoint;
    private string? _serialDragWindowId;
    private Point _commandPanelDragStartPoint;
    private bool _isCommandPanelHeaderPressed;
    private FloatingCommandWindow? _floatingCommandWindow;
    private HwndSource? _windowSource;
    private SerialWindowViewModel? _activeLogWindow;
    private bool _isTitleBarMenuNavigationActive;
    private bool _startupUpdateCheckStarted;
    private bool _isCheckingForUpdates;
    private int _lastPageIndex;
    private int _pageTransitionVersion;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        DataContext = _viewModel;
        _shortcutManager = new ShortcutManager(_viewModel.ShortcutBindings);
        _updateService = new UpdateService(UpdatePaths.DefaultUpdateRoot, AppContext.BaseDirectory);
        _lastPageIndex = _viewModel.CurrentPageIndex;
        ApplyThemeResources();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _windowSource = PresentationSource.FromVisual(this) as HwndSource;
        _windowSource?.AddHook(WindowMessageHook);
        DisableInputMethod(_windowSource?.Handle ?? IntPtr.Zero);
        WindowState = WindowState.Maximized;
        Keyboard.Focus(WorkspaceViewport);
    }

    [DllImport("imm32.dll")]
    private static extern IntPtr ImmAssociateContext(IntPtr windowHandle, IntPtr inputContext);

    private static void DisableInputMethod(IntPtr windowHandle)
    {
        if (windowHandle != IntPtr.Zero)
        {
            ImmAssociateContext(windowHandle, IntPtr.Zero);
        }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _updateCancellation.Cancel();
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _floatingCommandWindow?.CloseFromMainWindow();
        _viewModel.SaveWorkspace();
        _viewModel.Dispose();
        _updateCancellation.Dispose();
        base.OnClosing(e);
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _updateService.TryConfirmStartedUpdate(Environment.GetCommandLineArgs(), out _);

        if (_startupUpdateCheckStarted)
        {
            return;
        }

        _startupUpdateCheckStarted = true;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), _updateCancellation.Token);
            await CheckForUpdatesAsync(isManual: false, force: true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
        base.OnClosed(e);
    }

    private IntPtr WindowMessageHook(
        IntPtr hwnd,
        int message,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (message != WmGetMinMaxInfo)
        {
            return IntPtr.Zero;
        }

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var monitorInfo = new MonitorInfo
        {
            Size = Marshal.SizeOf<MonitorInfo>()
        };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
        {
            return IntPtr.Zero;
        }

        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        minMaxInfo.MaxPosition.X = monitorInfo.WorkArea.Left - monitorInfo.MonitorArea.Left;
        minMaxInfo.MaxPosition.Y = monitorInfo.WorkArea.Top - monitorInfo.MonitorArea.Top;
        minMaxInfo.MaxSize.X = monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left;
        minMaxInfo.MaxSize.Y = monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top;
        minMaxInfo.MaxTrackSize = minMaxInfo.MaxSize;
        Marshal.StructureToPtr(minMaxInfo, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr windowHandle, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitorHandle, ref MonitorInfo monitorInfo);

    [DllImport("comdlg32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChooseColor(ref ChooseColorData chooseColor);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorArea;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ChooseColorData
    {
        public int Size;
        public IntPtr OwnerHandle;
        public IntPtr InstanceHandle;
        public uint ResultColor;
        public IntPtr CustomColors;
        public uint Flags;
        public IntPtr CustomData;
        public IntPtr Hook;
        public IntPtr TemplateName;
    }

    private void ChooseCustomThemeColorButton_Click(object sender, RoutedEventArgs e)
    {
        if (ColorConverter.ConvertFromString(_viewModel.ThemeColor) is not Color currentColor)
        {
            return;
        }

        var customColors = Marshal.AllocCoTaskMem(16 * sizeof(int));
        try
        {
            Marshal.Copy(new int[16], 0, customColors, 16);
            var dialog = new ChooseColorData
            {
                Size = Marshal.SizeOf<ChooseColorData>(),
                OwnerHandle = new WindowInteropHelper(this).Handle,
                ResultColor = ToColorReference(currentColor),
                CustomColors = customColors,
                Flags = ChooseColorRgbInit | ChooseColorFullOpen
            };

            if (!ChooseColor(ref dialog))
            {
                return;
            }

            var red = (byte)(dialog.ResultColor & 0xFF);
            var green = (byte)((dialog.ResultColor >> 8) & 0xFF);
            var blue = (byte)((dialog.ResultColor >> 16) & 0xFF);
            _viewModel.ThemeColor = $"#{red:X2}{green:X2}{blue:X2}";
            _viewModel.SaveWorkspace();
        }
        finally
        {
            Marshal.FreeCoTaskMem(customColors);
        }
    }

    private static uint ToColorReference(Color color)
    {
        return color.R | ((uint)color.G << 8) | ((uint)color.B << 16);
    }

    private void BrowseLogRootDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        BrowseLogRootDirectory();
    }

    private void OpenCurrentLogSessionButton_Click(object sender, RoutedEventArgs e)
    {
        var sessionDirectory = _viewModel.CurrentLogSessionDirectory;
        if (string.IsNullOrWhiteSpace(sessionDirectory) || !Directory.Exists(sessionDirectory))
        {
            MessageBox.Show(
                this,
                "当前尚未创建日志会话。请先连接串口或点击“新建会话”。",
                "打开当前会话日志",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(sessionDirectory)
            {
                UseShellExecute = true
            });
            CloseOpenTitleBarMenus();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"无法打开当前日志会话目录。\n\n{exception.Message}",
                "打开当前会话日志失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void BrowseLogRootDirectory()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择日志保存目录",
            Multiselect = false
        };

        if (Directory.Exists(_viewModel.LogRootDirectory))
        {
            dialog.InitialDirectory = _viewModel.LogRootDirectory;
        }

        if (dialog.ShowDialog(this) == true)
        {
            _viewModel.LogRootDirectory = dialog.FolderName;
            _viewModel.SaveWorkspace();
        }
    }

    private void ToggleWindowStateButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void MinimizeWindowButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseWindowButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = CloseOpenTitleBarMenus();
            return;
        }

        if (IsAnyTitleBarMenuOpen() ||
            IsTextEditingElement(Keyboard.FocusedElement as DependencyObject))
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (!_shortcutManager.TryGetAction(key, Keyboard.Modifiers, out var actionId))
        {
            return;
        }

        e.Handled = ExecuteShortcutAction(actionId);
    }

    private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (IsTextEditingElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var focusedElement = Keyboard.FocusedElement as DependencyObject;
        if (!IsTextEditingElement(focusedElement))
        {
            return;
        }

        var focusScope = FocusManager.GetFocusScope(focusedElement);
        FocusManager.SetFocusedElement(focusScope, null);
        Keyboard.Focus(WorkspaceViewport);
    }

    private bool ExecuteShortcutAction(string actionId)
    {
        if (actionId == ShortcutActionIds.OpenDocumentation)
        {
            OpenOnlineDocumentation();
            return true;
        }

        if (actionId == ShortcutActionIds.BrowseLogDirectory)
        {
            BrowseLogRootDirectory();
            return true;
        }
        if (actionId == ShortcutActionIds.ClearActiveWindowLog)
        {
            return ClearActiveWindowLog();
        }

        if (actionId == ShortcutActionIds.ClearAllWindowLogs)
        {
            return ClearAllWindowLogs();
        }

        if (actionId == ShortcutActionIds.ToggleActiveWindowConnection)
        {
            return ToggleActiveWindowConnection();
        }

        if (actionId == ShortcutActionIds.ToggleActiveWindowLogFollow)
        {
            return ToggleActiveWindowLogFollow();
        }

        if (actionId == ShortcutActionIds.ToggleAllWindowLogFollow)
        {
            return ToggleAllWindowLogFollow();
        }


        ICommand? command = actionId switch
        {
            ShortcutActionIds.AddPage => _viewModel.AddPageCommand,
            ShortcutActionIds.RemoveCurrentPage => _viewModel.RemoveCurrentPageCommand,
            ShortcutActionIds.PreviousPage => _viewModel.PreviousPageCommand,
            ShortcutActionIds.NextPage => _viewModel.NextPageCommand,
            ShortcutActionIds.AddSerialWindow => _viewModel.AddWindowCommand,
            ShortcutActionIds.ToggleAllConnections => _viewModel.ToggleAllConnectionsCommand,
            ShortcutActionIds.ToggleCommandPanel => _viewModel.ToggleCommandPanelVisibilityCommand,
            ShortcutActionIds.NewLogSession => _viewModel.NewLogSessionCommand,
            ShortcutActionIds.ToggleCollaboration => _viewModel.ToggleCollaborationCommand,
            _ => null
        };

        if (command?.CanExecute(null) != true)
        {
            return false;
        }

        command.Execute(null);
        return true;
    }


    private bool ToggleActiveWindowConnection()
    {
        if (!TryGetActiveLogWindow(out var window))
        {
            _viewModel.StatusText = "\u8BF7\u5148\u70B9\u51FB\u8981\u8FDE\u63A5\u6216\u65AD\u5F00\u7684\u4E32\u53E3\u7A97\u53E3";
            return true;
        }

        if (window.IsRemote)
        {
            _viewModel.StatusText = "\u8FDC\u7AEF\u7A97\u53E3\u4E0D\u53EF\u5728\u672C\u673A\u8FDE\u63A5\u6216\u65AD\u5F00";
            return true;
        }

        if (window.ToggleConnectionCommand.CanExecute(null))
        {
            window.ToggleConnectionCommand.Execute(null);
        }

        return true;
    }

    private bool ToggleActiveWindowLogFollow()
    {
        if (!TryGetActiveLogWindow(out var window))
        {
            _viewModel.StatusText = "\u8BF7\u5148\u70B9\u51FB\u8981\u6682\u505C\u6216\u6062\u590D\u8DDF\u968F\u7684\u65E5\u5FD7\u7A97\u53E3";
            return true;
        }

        window.IsLogAutoScrollPaused = !window.IsLogAutoScrollPaused;
        _viewModel.StatusText = window.IsLogAutoScrollPaused
            ? $"\u5DF2\u6682\u505C\u65E5\u5FD7\u8DDF\u968F\uFF1A{window.Title}"
            : $"\u5DF2\u6062\u590D\u65E5\u5FD7\u8DDF\u968F\uFF1A{window.Title}";
        return true;
    }

    private bool ToggleAllWindowLogFollow()
    {
        if (_viewModel.SerialWindows.Count == 0)
        {
            return true;
        }

        var shouldPause = _viewModel.SerialWindows.Any(window => !window.IsLogAutoScrollPaused);
        foreach (var window in _viewModel.SerialWindows)
        {
            window.IsLogAutoScrollPaused = shouldPause;
        }

        _viewModel.StatusText = shouldPause
            ? "\u5DF2\u6682\u505C\u5168\u90E8\u7A97\u53E3\u65E5\u5FD7\u8DDF\u968F"
            : "\u5DF2\u6062\u590D\u5168\u90E8\u7A97\u53E3\u65E5\u5FD7\u8DDF\u968F";
        return true;
    }

    private bool TryGetActiveLogWindow(out SerialWindowViewModel window)
    {
        if (_activeLogWindow is not null && _viewModel.SerialWindows.Contains(_activeLogWindow))
        {
            window = _activeLogWindow;
            return true;
        }

        window = null!;
        return false;
    }
    private bool ClearActiveWindowLog()
    {
        if (_activeLogWindow is null || !_viewModel.SerialWindows.Contains(_activeLogWindow))
        {
            _viewModel.StatusText = "\u8BF7\u5148\u70B9\u51FB\u8981\u6E05\u7A7A\u7684\u65E5\u5FD7\u7A97\u53E3";
            return true;
        }

        _activeLogWindow.ClearCommand.Execute(null);
        _viewModel.StatusText = $"\u5DF2\u6E05\u7A7A\u7A97\u53E3\u65E5\u5FD7\uFF1A{_activeLogWindow.Title}";
        return true;
    }

    private bool ClearAllWindowLogs()
    {
        foreach (var window in _viewModel.SerialWindows)
        {
            window.ClearCommand.Execute(null);
        }

        _viewModel.StatusText = $"\u5DF2\u6E05\u7A7A\u5168\u90E8\u7A97\u53E3\u65E5\u5FD7\uFF1A\u5171 {_viewModel.SerialWindows.Count} \u4E2A\u7A97\u53E3";
        return true;
    }
    private void OpenDocumentationButton_Click(object sender, RoutedEventArgs e)
    {
        CloseOpenTitleBarMenus();
        OpenOnlineDocumentation();
    }

    private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isCheckingForUpdates)
        {
            return;
        }

        CloseOpenTitleBarMenus();
        _isCheckingForUpdates = true;
        _viewModel.StatusText = "正在检查更新...";
        try
        {
            await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Render);
            var dialog = new UpdateCheckWindow(
                async cancellationToken =>
                {
                    using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken,
                        _updateCancellation.Token);
                    return await _updateService.CheckForUpdatesAsync(
                        AppVersionInfo.VersionText,
                        force: true,
                        linkedCancellation.Token);
                },
                AppVersionInfo.VersionText)
            {
                Owner = this
            };

            dialog.ShowDialog();
            if (dialog.OpenReleasePageRequested)
            {
                OpenReleasePage();
            }

            if (dialog.Result is not null)
            {
                switch (dialog.Result.Status)
                {
                    case UpdateCheckStatus.NoUpdate:
                        _viewModel.StatusText = "当前已是最新版本";
                        break;
                    case UpdateCheckStatus.Failed:
                        _viewModel.StatusText = "检查更新失败";
                        Trace.TraceWarning("检查更新失败：{0}", dialog.Result.ErrorMessage);
                        break;
                    case UpdateCheckStatus.UpdateAvailable:
                        _viewModel.StatusText = dialog.Result.Release is not null
                            ? $"发现新版本 {dialog.Result.Release.TagName}"
                            : "发现新版本";
                        await WaitForModalWindowTransitionAsync();
                        HandleUpdateCheckResult(dialog.Result, isManual: true);
                        break;
                }
            }
            else if (dialog.WasCanceled && IsLoaded && !_updateCancellation.IsCancellationRequested)
            {
                _viewModel.StatusText = "已取消检查更新";
            }
        }
        finally
        {
            _isCheckingForUpdates = false;
            if (_viewModel.StatusText == "正在检查更新...")
            {
                _viewModel.StatusText = "检查更新已结束";
            }
        }
    }

    private async Task WaitForModalWindowTransitionAsync()
    {
        await Dispatcher.InvokeAsync(() =>
        {
            Activate();
            InvalidateVisual();
            UpdateLayout();
        }, DispatcherPriority.ContextIdle);

        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler? renderingHandler = null;
        renderingHandler = (_, _) =>
        {
            CompositionTarget.Rendering -= renderingHandler;
            rendered.TrySetResult();
        };

        CompositionTarget.Rendering += renderingHandler;
        InvalidateVisual();

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            await rendered.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            CompositionTarget.Rendering -= renderingHandler;
        }

        await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
    }

    private async Task CheckForUpdatesAsync(bool isManual, bool force = false)
    {
        if (isManual)
        {
            _viewModel.StatusText = "正在检查更新...";
        }

        UpdateCheckResult result;
        try
        {
            result = await _updateService.CheckForUpdatesAsync(
                AppVersionInfo.VersionText,
                force: force || isManual,
                _updateCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            result = UpdateCheckResult.Failed(exception.Message);
        }

        if (!IsLoaded || _updateCancellation.IsCancellationRequested)
        {
            return;
        }

        HandleUpdateCheckResult(result, isManual);
    }

    private void HandleUpdateCheckResult(UpdateCheckResult result, bool isManual)
    {
        switch (result.Status)
        {
            case UpdateCheckStatus.Skipped:
                return;
            case UpdateCheckStatus.NoUpdate:
                if (isManual)
                {
                    _viewModel.StatusText = "当前已是最新版本";
                    MessageBox.Show(
                        this,
                        $"当前版本 {AppVersionInfo.VersionText} 已是最新版本。",
                        "检查更新",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                return;
            case UpdateCheckStatus.Failed:
                Trace.TraceWarning("检查更新失败：{0}", result.ErrorMessage);
                if (isManual)
                {
                    _viewModel.StatusText = "检查更新失败";
                    var openReleasePage = MessageBox.Show(
                        this,
                        $"检查更新失败。\n\n{result.ErrorMessage}\n\n是否打开 GitHub Release 页面？",
                        "检查更新",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (openReleasePage == MessageBoxResult.Yes)
                    {
                        OpenReleasePage();
                    }
                }

                return;
            case UpdateCheckStatus.UpdateAvailable when result.Release is not null:
                ShowAvailableUpdate(result.Release);
                return;
        }
    }

    private void ShowAvailableUpdate(UpdateReleaseInfo release)
    {
        var canInstall = PortableUpdateCoordinator.CanInstallInPlace(
            _updateService,
            AppContext.BaseDirectory,
            out var restrictionReason);
        var dialog = new UpdateWindow(
            _updateService,
            release,
            AppVersionInfo.VersionText,
            AppContext.BaseDirectory,
            canInstall,
            restrictionReason)
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true || dialog.PreparedUpdate is null)
        {
            return;
        }

        try
        {
            _viewModel.StatusText = "正在安装更新...";
            _viewModel.SaveWorkspace();
            PortableUpdateCoordinator.StartUpdater(dialog.PreparedUpdate);
            Close();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"无法启动更新助手，当前版本未发生变化。\n\n{exception.Message}",
                "更新失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OpenReleasePage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_updateService.ReleasesPageUri.AbsoluteUri)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"无法打开 GitHub Release 页面。\n\n{exception.Message}",
                "打开失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OpenOnlineDocumentation()
    {
        try
        {
            Process.Start(new ProcessStartInfo(OnlineHelpUrl)
            {
                UseShellExecute = true
            });
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                this,
                $"无法打开在线操作说明。\n\n{exception.Message}",
                "打开操作说明失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OpenShortcutSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        CloseOpenTitleBarMenus();
        var dialog = new ShortcutSettingsWindow(_shortcutManager.ExportBindings())
        {
            Owner = this
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _shortcutManager.ApplyBindings(dialog.ResultBindings);
        _viewModel.SetShortcutBindings(_shortcutManager.ExportBindings());
        _viewModel.StatusText = "快捷键已更新";
    }

    private void ShowAboutButton_Click(object sender, RoutedEventArgs e)
    {
        CloseOpenTitleBarMenus();
        MessageBox.Show(
            this,
            $"版本 {AppVersionInfo.VersionText}",
            "关于 Serial Log",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void TitleBarMenuToggle_MouseEnter(object sender, MouseEventArgs e)
    {
        if (!_isTitleBarMenuNavigationActive ||
            sender is not ToggleButton toggle)
        {
            return;
        }

        var targetPopup = GetTitleBarMenuPopup(toggle);

        if (targetPopup is null || targetPopup.IsOpen)
        {
            return;
        }

        CloseOpenTitleBarMenus(endMenuNavigation: false);
        targetPopup.IsOpen = true;
    }

    private void TitleBarMenuToggle_Click(object sender, RoutedEventArgs e)
    {
        _isTitleBarMenuNavigationActive = sender is ToggleButton { IsChecked: true };
    }

    private void WorkspaceViewport_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isTitleBarMenuNavigationActive)
        {
            return;
        }

        CloseOpenTitleBarMenus();
    }

    private void MainWindow_Deactivated(object? sender, EventArgs e)
    {
        CloseOpenTitleBarMenus();
    }

    private Popup? GetTitleBarMenuPopup(ToggleButton toggle)
    {
        return ReferenceEquals(toggle, PageMenuToggle) ? PageMenuPopup :
            ReferenceEquals(toggle, CollaborationMenuToggle) ? CollaborationMenuPopup :
            ReferenceEquals(toggle, ThemeMenuToggle) ? ThemeMenuPopup :
            ReferenceEquals(toggle, ViewMenuToggle) ? ViewMenuPopup :
            ReferenceEquals(toggle, LogMenuToggle) ? LogMenuPopup :
            ReferenceEquals(toggle, HelpMenuToggle) ? HelpMenuPopup :
            null;
    }

    private bool CloseOpenTitleBarMenus(bool endMenuNavigation = true)
    {
        if (endMenuNavigation)
        {
            _isTitleBarMenuNavigationActive = false;
        }

        var closedMenu = false;
        foreach (var popup in new[] { PageMenuPopup, CollaborationMenuPopup, ThemeMenuPopup, ViewMenuPopup, LogMenuPopup, HelpMenuPopup })
        {
            if (!popup.IsOpen)
            {
                continue;
            }

            popup.IsOpen = false;
            closedMenu = true;
        }

        return closedMenu;
    }

    private bool IsAnyTitleBarMenuOpen()
    {
        return PageMenuPopup.IsOpen ||
            CollaborationMenuPopup.IsOpen ||
            ThemeMenuPopup.IsOpen ||
            ViewMenuPopup.IsOpen ||
            LogMenuPopup.IsOpen ||
            HelpMenuPopup.IsOpen;
    }

    private static bool IsTextEditingElement(DependencyObject? element)
    {
        return FindAncestor<TextBoxBase>(element) is not null ||
            FindAncestor<PasswordBox>(element) is not null ||
            FindAncestor<ComboBox>(element) is not null;
    }

    private void SerialWindowCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _serialDragStartPoint = e.GetPosition(null);
        _serialDragWindowId = null;

        if (sender is not FrameworkElement { DataContext: SerialWindowSlotViewModel { IsAddSlot: false, Window: { } window } })
        {
            return;
        }

        _activeLogWindow = window;
        if (IsInteractiveElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        _serialDragWindowId = window.Id;
    }

    private void SerialWindowCard_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || string.IsNullOrWhiteSpace(_serialDragWindowId))
        {
            return;
        }

        var current = e.GetPosition(null);
        if (Math.Abs(current.X - _serialDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _serialDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var data = new DataObject(SerialWindowDragDataFormat, _serialDragWindowId);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
    }

    private void SerialWindowCard_Drop(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SerialWindowSlotViewModel slot } ||
            !TryGetDraggedSerialWindowId(e, out var windowId))
        {
            return;
        }

        _viewModel.MoveSerialWindow(windowId, slot.PageIndex, slot.PagePosition);
        e.Handled = true;
    }

    private void CommandPanelHeader_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isCommandPanelHeaderPressed = false;
        if (IsInteractiveElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        _commandPanelDragStartPoint = e.GetPosition(null);
        _isCommandPanelHeaderPressed = true;
    }

    private void CommandPanelHeader_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isCommandPanelHeaderPressed ||
            e.LeftButton != MouseButtonState.Pressed ||
            _viewModel.IsCommandPanelFloating)
        {
            return;
        }

        var current = e.GetPosition(null);
        if (Math.Abs(current.X - _commandPanelDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _commandPanelDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(CommandPanelDragDataFormat, true), DragDropEffects.Move);
        _isCommandPanelHeaderPressed = false;

        var position = Mouse.GetPosition(this);
        if (position.X < 0 || position.Y < 0 || position.X > ActualWidth || position.Y > ActualHeight)
        {
            _viewModel.FloatCommandPanelCommand.Execute(null);
        }
    }

    private void PreviousPageButton_Drop(object sender, DragEventArgs e)
    {
        if (!TryGetDraggedSerialWindowId(e, out var windowId) || _viewModel.CurrentPageIndex <= 0)
        {
            return;
        }

        var targetPage = _viewModel.CurrentPageIndex - 1;
        _viewModel.MoveSerialWindow(windowId, targetPage, 0);
        _viewModel.CurrentPageIndex = targetPage;
        e.Handled = true;
    }

    private void NextPageButton_Drop(object sender, DragEventArgs e)
    {
        if (!TryGetDraggedSerialWindowId(e, out var windowId) || _viewModel.CurrentPageIndex >= _viewModel.PageCount - 1)
        {
            return;
        }

        var targetPage = _viewModel.CurrentPageIndex + 1;
        _viewModel.MoveSerialWindow(windowId, targetPage, 0);
        _viewModel.CurrentPageIndex = targetPage;
        e.Handled = true;
    }

    private void BaudRateComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { DataContext: SerialWindowViewModel window, SelectedItem: not null } comboBox)
        {
            window.BaudRateText = comboBox.SelectedItem.ToString() ?? string.Empty;
        }
    }

    private void LogListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox listBox)
        {
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            ListBoxAutoScroll.Resume(listBox);
            e.Handled = true;
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        if (e.Key == Key.A)
        {
            listBox.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.C)
        {
            CopySelectedLogLines(listBox);
            e.Handled = true;
        }
    }

    private void LogListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox)
        {
            return;
        }

        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item is null)
        {
            return;
        }

        if (!item.IsSelected)
        {
            listBox.SelectedItems.Clear();
            item.IsSelected = true;
        }

        item.Focus();
    }

    private void LogListBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is ListBox { DataContext: SerialWindowViewModel window })
        {
            _activeLogWindow = window;
        }
    }

    private void CopySelectedLogMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Parent: ContextMenu { PlacementTarget: ListBox listBox } })
        {
            return;
        }

        CopySelectedLogLines(listBox);
    }

    private void CopySelectedLogLines(ListBox listBox)
    {
        var selectedLines = listBox.Items
            .OfType<LogLineViewModel>()
            .Where(line => listBox.SelectedItems.Contains(line))
            .Select(line => line.CopyText)
            .ToArray();
        if (selectedLines.Length == 0)
        {
            return;
        }

        Clipboard.SetText(string.Join(Environment.NewLine, selectedLines));
        _viewModel.StatusText = selectedLines.Length == 1
            ? "已复制 1 行日志"
            : $"已复制 {selectedLines.Length} 行日志";
    }

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentPageIndex))
        {
            QueuePageTransition();
        }

        if (e.PropertyName == nameof(MainViewModel.ThemeColor))
        {
            ApplyThemeResources();
        }

        if (e.PropertyName != nameof(MainViewModel.IsCommandPanelFloating))
        {
            return;
        }

        if (_viewModel.IsCommandPanelFloating)
        {
            ShowFloatingCommandWindow();
            return;
        }

        CloseFloatingCommandWindow();
    }

    private void QueuePageTransition()
    {
        var currentPageIndex = _viewModel.CurrentPageIndex;
        var direction = currentPageIndex >= _lastPageIndex ? 1 : -1;
        _lastPageIndex = currentPageIndex;

        if (!IsLoaded)
        {
            return;
        }

        // Hide the old page before its item source is replaced so each log viewer restores
        // its saved offsets before the transition becomes visible.
        PageTransitionHost.BeginAnimation(OpacityProperty, null);
        PageTransitionTransform.BeginAnimation(TranslateTransform.XProperty, null);
        PageTransitionHost.Opacity = 0;
        PageTransitionTransform.X = direction >= 0 ? 18 : -18;

        var transitionVersion = ++_pageTransitionVersion;
        Dispatcher.BeginInvoke(() =>
        {
            if (transitionVersion != _pageTransitionVersion || !IsLoaded)
            {
                return;
            }

            BeginPageTransition();
        }, DispatcherPriority.Render);
    }

    private void BeginPageTransition()
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        PageTransitionHost.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
        PageTransitionTransform.BeginAnimation(
            TranslateTransform.XProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)) { EasingFunction = easing });
    }

    private void ApplyThemeResources()
    {
        if (ColorConverter.ConvertFromString(_viewModel.ThemeColor) is not Color accentColor ||
            ColorConverter.ConvertFromString(_viewModel.ThemeSoftBrush) is not Color softColor)
        {
            return;
        }

        Application.Current.Resources["AccentBrush"] = new SolidColorBrush(accentColor);
        Application.Current.Resources["AccentSoftBrush"] = new SolidColorBrush(softColor);
    }

    private void ShowFloatingCommandWindow()
    {
        if (_floatingCommandWindow is not null)
        {
            return;
        }

        var (width, height) = GetFloatingCommandWindowSize();
        _floatingCommandWindow = new FloatingCommandWindow
        {
            Owner = this,
            DataContext = _viewModel,
            Left = Left + 80,
            Top = Top + 80,
            Width = width,
            Height = height,
            MinWidth = Math.Min(_viewModel.FloatingCommandPanelMinWidth, width),
            MinHeight = Math.Min(_viewModel.FloatingCommandPanelMinHeight, height)
        };
        _floatingCommandWindow.Closed += (_, _) => _floatingCommandWindow = null;
        _floatingCommandWindow.Show();
    }

    private (double Width, double Height) GetFloatingCommandWindowSize()
    {
        var width = CommandPanelHost.ActualWidth;
        var height = CommandPanelHost.ActualHeight;
        if (!IsUsableSize(width, height))
        {
            width = _viewModel.FloatingCommandPanelWidth;
            height = _viewModel.FloatingCommandPanelHeight;
        }

        var workArea = SystemParameters.WorkArea;
        var maxWidth = Math.Max(_viewModel.FloatingCommandPanelMinWidth, workArea.Width - 80);
        var maxHeight = Math.Max(_viewModel.FloatingCommandPanelMinHeight, workArea.Height - 80);
        return (
            Math.Clamp(width, _viewModel.FloatingCommandPanelMinWidth, maxWidth),
            Math.Clamp(height, _viewModel.FloatingCommandPanelMinHeight, maxHeight));
    }

    private static bool IsUsableSize(double width, double height)
    {
        return !double.IsNaN(width) &&
            !double.IsNaN(height) &&
            !double.IsInfinity(width) &&
            !double.IsInfinity(height) &&
            width > 0 &&
            height > 0;
    }

    private void CloseFloatingCommandWindow()
    {
        if (_floatingCommandWindow is null)
        {
            return;
        }

        var window = _floatingCommandWindow;
        _floatingCommandWindow = null;
        window.CloseFromMainWindow();
    }

    private static bool TryGetDraggedSerialWindowId(DragEventArgs e, out string windowId)
    {
        if (e.Data.GetDataPresent(SerialWindowDragDataFormat) &&
            e.Data.GetData(SerialWindowDragDataFormat) is string draggedId &&
            !string.IsNullOrWhiteSpace(draggedId))
        {
            windowId = draggedId;
            return true;
        }

        windowId = string.Empty;
        return false;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T typed)
            {
                return typed;
            }

            current = GetParent(current);
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject current)
    {
        return current switch
        {
            Visual => VisualTreeHelper.GetParent(current),
            FrameworkContentElement contentElement => contentElement.Parent,
            _ => LogicalTreeHelper.GetParent(current)
        };
    }

    private static bool IsInteractiveElement(DependencyObject? source)
    {
        return FindAncestor<TextBox>(source) is not null ||
            FindAncestor<ComboBox>(source) is not null ||
            FindAncestor<Button>(source) is not null ||
            FindAncestor<CheckBox>(source) is not null ||
            FindAncestor<ListBox>(source) is not null ||
            FindAncestor<RichTextBox>(source) is not null;
    }
}
