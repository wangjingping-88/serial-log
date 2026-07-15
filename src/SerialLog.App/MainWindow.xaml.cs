using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SerialLog.App.Behaviors;
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

    private void BrowseLogRootDirectoryButton_Click(object sender, RoutedEventArgs e)
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
