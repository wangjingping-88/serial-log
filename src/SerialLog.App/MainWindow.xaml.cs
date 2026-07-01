using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SerialLog.App.ViewModels;

namespace SerialLog.App;

public partial class MainWindow : Window
{
    private const string SerialWindowDragDataFormat = "SerialLog.SerialWindowId";
    private const string CommandPanelDragDataFormat = "SerialLog.CommandPanel";
    private readonly MainViewModel _viewModel;
    private Point _serialDragStartPoint;
    private string? _serialDragWindowId;
    private Point _commandPanelDragStartPoint;
    private bool _isCommandPanelHeaderPressed;
    private FloatingCommandWindow? _floatingCommandWindow;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        DataContext = _viewModel;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
        _floatingCommandWindow?.CloseFromMainWindow();
        _viewModel.SaveWorkspace();
        _viewModel.Dispose();
        base.OnClosing(e);
    }

    private void SerialWindowCard_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _serialDragStartPoint = e.GetPosition(null);
        _serialDragWindowId = null;

        if (IsInteractiveElement(e.OriginalSource as DependencyObject) ||
            sender is not FrameworkElement { DataContext: SerialWindowSlotViewModel { IsAddSlot: false, Window: { } window } })
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

    private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
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

    private void ShowFloatingCommandWindow()
    {
        if (_floatingCommandWindow is not null)
        {
            return;
        }

        _floatingCommandWindow = new FloatingCommandWindow
        {
            Owner = this,
            DataContext = _viewModel,
            Left = Left + 80,
            Top = Top + 80
        };
        _floatingCommandWindow.Closed += (_, _) => _floatingCommandWindow = null;
        _floatingCommandWindow.Show();
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

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static bool IsInteractiveElement(DependencyObject? source)
    {
        return FindAncestor<TextBox>(source) is not null ||
            FindAncestor<ComboBox>(source) is not null ||
            FindAncestor<Button>(source) is not null ||
            FindAncestor<CheckBox>(source) is not null ||
            FindAncestor<ListBox>(source) is not null;
    }
}
