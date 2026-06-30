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
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void CommandGroupCommandsListBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || sender is not ListBox { SelectedItem: string command })
        {
            return;
        }

        if (ViewModel?.RemoveCommandFromGroupCommand.CanExecute(command) == true)
        {
            ViewModel.RemoveCommandFromGroupCommand.Execute(command);
            e.Handled = true;
        }
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

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
