using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SerialLog.App.ViewModels;

namespace SerialLog.App.Views;

public partial class CommandPanelView : UserControl
{
    private const string CommandDragDataFormat = "SerialLog.CommandGroupCommandIndex";
    private Point _commandDragStartPoint;
    private int _commandDragStartIndex = -1;

    public CommandPanelView()
    {
        InitializeComponent();
        PublicPanel.Items.Remove(ImportTab);
        PublicPanel.Items.Remove(TargetWindowsTab);
        PublicPanel.Items.Remove(HistoryTab);
        PublicPanel.Items.Add(ImportTab);
        PublicPanel.Items.Add(TargetWindowsTab);
        PublicPanel.Items.Add(HistoryTab);
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void CommandGroupCommandsListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox { SelectedIndex: >= 0 } list || ViewModel is not { } vm)
        {
            return;
        }

        var command = e.Key switch
        {
            Key.Delete when Keyboard.Modifiers == ModifierKeys.None => vm.RemoveCommandFromGroupCommand,
            Key.C when Keyboard.Modifiers == ModifierKeys.Control => vm.CopyCommandFromGroupCommand,
            _ => null
        };
        if (command?.CanExecute(list.SelectedIndex) == true)
        {
            command.Execute(list.SelectedIndex);
            e.Handled = true;
        }
    }

    private void CommandGroupCommandsListBox_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list) return;
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        list.SelectedIndex = item is null ? -1 : list.ItemContainerGenerator.IndexFromContainer(item);
    }

    private void CommandGroupCommandsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not ListBox list ||
            FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) is not { } item) return;
        var index = list.ItemContainerGenerator.IndexFromContainer(item);
        if (ViewModel?.EditCommandInGroupCommand.CanExecute(index) != true) return;
        _commandDragStartIndex = -1;
        ViewModel.EditCommandInGroupCommand.Execute(index);
        e.Handled = true;
    }

    private void CommandGroupCommandsListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _commandDragStartPoint = e.GetPosition(null);
        _commandDragStartIndex = -1;

        if (sender is not ListBox listBox)
        {
            return;
        }

        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item is null)
        {
            return;
        }

        item.IsSelected = true;
        _commandDragStartIndex = listBox.ItemContainerGenerator.IndexFromContainer(item);
    }

    private void CommandGroupCommandsListBox_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            sender is not ListBox listBox ||
            _commandDragStartIndex < 0)
        {
            return;
        }

        var current = e.GetPosition(null);
        if (Math.Abs(current.X - _commandDragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _commandDragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var data = new DataObject(CommandDragDataFormat, _commandDragStartIndex);
        DragDrop.DoDragDrop(listBox, data, DragDropEffects.Move);
    }

    private void CommandGroupCommandsListBox_Drop(object sender, DragEventArgs e)
    {
        if (sender is not ListBox listBox || !e.Data.GetDataPresent(CommandDragDataFormat))
        {
            return;
        }

        var sourceIndex = (int)e.Data.GetData(CommandDragDataFormat)!;
        var targetItem = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        var targetIndex = targetItem is null
            ? listBox.Items.Count - 1
            : listBox.ItemContainerGenerator.IndexFromContainer(targetItem);

        ViewModel?.MoveSelectedCommandInGroup(sourceIndex, targetIndex);
        e.Handled = true;
    }

    private void ImportedAtCommandsListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || sender is not ListBox { SelectedItem: string command })
        {
            return;
        }

        if (ViewModel?.RemoveImportedAtCommandCommand.CanExecute(command) == true)
        {
            ViewModel.RemoveImportedAtCommandCommand.Execute(command);
            e.Handled = true;
        }
    }

    private void ImportedAtCommandsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: string command })
        {
            ViewModel?.ApplyCommandToActiveEditor(command);
        }
    }

    private void CommandHistoryListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || sender is not ListBox { SelectedItem: string command })
        {
            return;
        }

        if (ViewModel?.RemoveCommandHistoryItemCommand.CanExecute(command) == true)
        {
            ViewModel.RemoveCommandHistoryItemCommand.Execute(command);
            e.Handled = true;
        }
    }

    private void ListBoxItem_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem item)
        {
            return;
        }

        item.IsSelected = true;
        item.Focus();
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T typed)
            {
                return typed;
            }

            current = current switch
            {
                Visual => VisualTreeHelper.GetParent(current),
                FrameworkContentElement contentElement => contentElement.Parent,
                _ => LogicalTreeHelper.GetParent(current)
            };
        }

        return null;
    }
}
