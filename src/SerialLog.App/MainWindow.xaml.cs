using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SerialLog.App.ViewModels;

namespace SerialLog.App;

public partial class MainWindow : Window
{
    private const string SerialWindowDragDataFormat = "SerialLog.SerialWindowId";
    private readonly MainViewModel _viewModel;
    private Point _serialDragStartPoint;
    private string? _serialDragWindowId;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
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

        var targetIndex = slot.Window is null
            ? _viewModel.SerialWindows.Count - 1
            : _viewModel.SerialWindows.ToList().FindIndex(window => window.Id == slot.Window.Id);
        if (targetIndex < 0)
        {
            return;
        }

        _viewModel.MoveSerialWindow(windowId, targetIndex);
        e.Handled = true;
    }

    private void PreviousPageButton_Drop(object sender, DragEventArgs e)
    {
        if (!TryGetDraggedSerialWindowId(e, out var windowId) || _viewModel.CurrentPageIndex <= 0)
        {
            return;
        }

        var targetPage = _viewModel.CurrentPageIndex - 1;
        _viewModel.MoveSerialWindow(windowId, targetPage * 6);
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
        var targetIndex = Math.Min(targetPage * 6, _viewModel.SerialWindows.Count - 1);
        _viewModel.MoveSerialWindow(windowId, targetIndex);
        _viewModel.CurrentPageIndex = targetPage;
        e.Handled = true;
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
