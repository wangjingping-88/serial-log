using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SerialLog.App.Infrastructure;
using SerialLog.App.ViewModels;
using SerialLog.App.Views;
using SerialLog.Core.Serial;

namespace SerialLog.App;

public partial class MainWindow
{
    private readonly LocalPortMonitor _portMonitor;
    private PortInventoryWindow? _portInventoryWindow;
    private static readonly DependencyPropertyKey IsPortInventoryOpenPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsPortInventoryOpen), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
    public static readonly DependencyProperty IsPortInventoryOpenProperty = IsPortInventoryOpenPropertyKey.DependencyProperty;
    public bool IsPortInventoryOpen => (bool)GetValue(IsPortInventoryOpenProperty);
    private PortEventToast? _portEventToast;
    private readonly DispatcherTimer _portBubbleBatch = new() { Interval = TimeSpan.FromMilliseconds(380) };
    private readonly DispatcherTimer _portBubbleDismiss = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly List<LocalPortEvent> _pendingPortEvents = [];
    private const int MaxPendingPortEvents = 80;

    private void InitializePortInventory()
    {
        _portMonitor.Changed += PortInventoryChanged;
        _portMonitor.EventsDetected += PortEventsDetected;
        _portBubbleBatch.Tick += (_, _) => ShowPortBubble();
        _portBubbleDismiss.Tick += (_, _) => DismissPortBubble();
    }

    private void StopPortInventory()
    {
        _portMonitor.Changed -= PortInventoryChanged;
        _portMonitor.EventsDetected -= PortEventsDetected;
        _portMonitor.Dispose();
        DismissPortBubble();
    }

    private void PortInventoryChanged()
    {
        if (_shutdownInProgress) return;
        foreach (var window in _workspaces.Workspaces.SelectMany(w => w.ViewModel.SerialWindows).Where(w => !w.IsRemote))
            window.AutoRefreshPorts();
        if (!_portMonitor.Settings.ShowNotifications) DismissPortBubble();
    }

    private void ShowPortInventory_Click(object sender, RoutedEventArgs e)
    {
        CloseOpenTitleBarMenus();
        if (_portInventoryWindow is { } existing) { existing.Activate(); return; }
        var dialog = new PortInventoryWindow(_portMonitor, _workspaces, LocatePortWindow) { Owner = this };
        _portInventoryWindow = dialog;
        dialog.Closed += (_, _) =>
        {
            _portInventoryWindow = null;
            SetValue(IsPortInventoryOpenPropertyKey, false);
        };
        dialog.Show();
        SetValue(IsPortInventoryOpenPropertyKey, ReferenceEquals(_portInventoryWindow, dialog));
    }

    private void LocatePortWindow(PortUse target)
    {
        if (!_workspaces.Workspaces.Contains(target.Workspace) ||
            !target.Workspace.ViewModel.SerialWindows.Contains(target.Window)) return;
        WorkspaceSelector.SelectedItem = target.Workspace;
        target.Workspace.ViewModel.CurrentPageIndex = target.Window.PageIndex;
        target.Workspace.ViewModel.StatusText = $"已定位 {target.Window.Title}（{target.Window.PortName ?? "未选端口"}）";
        Activate();
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!ReferenceEquals(_viewModel, target.Workspace.ViewModel) || _viewModel.CurrentPageIndex != target.Window.PageIndex) return;
            FindLogList(WorkspaceViewport, target.Window)?.Focus();
        }, DispatcherPriority.Loaded);
    }

    private static ListBox? FindLogList(DependencyObject root, SerialWindowViewModel window)
    {
        if (root is ListBox list && ReferenceEquals(list.DataContext, window)) return list;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindLogList(VisualTreeHelper.GetChild(root, i), window) is { } match) return match;
        return null;
    }

    private void PortEventsDetected(IReadOnlyList<LocalPortEvent> events)
    {
        if (!_portMonitor.Settings.ShowNotifications || _shutdownInProgress) return;
        _pendingPortEvents.AddRange(events.Take(Math.Max(0, MaxPendingPortEvents - _pendingPortEvents.Count)));
        _portBubbleBatch.Start();
    }

    private void ShowPortBubble()
    {
        _portBubbleBatch.Stop();
        if (!_portMonitor.Settings.ShowNotifications || _pendingPortEvents.Count == 0 || _shutdownInProgress)
        { DismissPortBubble(); return; }
        var descriptions = _pendingPortEvents.Take(5).Select(entry =>
            (entry.Arrived ? "插入" : "拔出") + " · " +
            (entry.Arrived && _portMonitor.Inventory.Find(entry.Port.Name) is { } current &&
                (entry.Port.DeviceId.Length == 0 || entry.Port.DeviceId.Equals(current.DeviceId, StringComparison.OrdinalIgnoreCase))
                ? _portMonitor.Settings.Label(current) : entry.Label)).ToList();
        if (_pendingPortEvents.Count > descriptions.Count) descriptions.Add($"另有 {_pendingPortEvents.Count - descriptions.Count} 条，详见最近事件。");
        _pendingPortEvents.Clear();
        if (_portEventToast is null)
        {
            _portEventToast = new PortEventToast(this);
            _portEventToast.Closed += (_, _) => { _portEventToast = null; _portBubbleDismiss.Stop(); };
        }
        _portEventToast.Display(string.Join(Environment.NewLine, descriptions));
        _portBubbleDismiss.Stop();
        _portBubbleDismiss.Start();
    }

    private void DismissPortBubble()
    {
        _portBubbleBatch.Stop();
        _portBubbleDismiss.Stop();
        _pendingPortEvents.Clear();
        _portEventToast?.Close();
    }
}
