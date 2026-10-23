using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SerialLog.App.ViewModels;

namespace SerialLog.App.Views;

public partial class CommandPanelView : UserControl
{
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

    private void CommandList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox list) SelectCommandListRow(list, e.OriginalSource as DependencyObject);
    }

    private void CommandList_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list) return;
        SelectCommandListRow(list, e.OriginalSource as DependencyObject);
        list.Focus();
    }

    private void CommandList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete || Keyboard.Modifiers != ModifierKeys.None || sender is not ListBox list) return;
        if (ItemsControl.ContainerFromElement(list, e.OriginalSource as DependencyObject) is ListBoxItem item)
            list.SelectedIndex = list.ItemContainerGenerator.IndexFromContainer(item);
        e.Handled = DeleteCommandListSelection(list);
    }

    private void CommandList_EditGroupOnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || sender is not ListBox list ||
            !SelectCommandGroupForEditing(list, e.OriginalSource as DependencyObject)) return;
        var panel = (CommandPanelViewModel)list.DataContext;
        if (!panel.CommandGroupSettingsCommand.CanExecute(null)) return;
        e.Handled = true;
        panel.CommandGroupSettingsCommand.Execute(null);
    }

    internal static bool SelectCommandGroupForEditing(ListBox list, DependencyObject? source)
    {
        if (source is null || list.DataContext is not CommandPanelViewModel panel ||
            !ReferenceEquals(list.ItemsSource, panel.VisibleCommandGroups) ||
            ItemsControl.ContainerFromElement(list, source) is not ListBoxItem item ||
            item.DataContext is not CommandGroupEditorViewModel group || !panel.CommandGroups.Contains(group)) return false;

        // 名称文字独立于勾选框；按钮及其模板子元素保留自身操作，不触发编辑或改变勾选。
        for (var current = source; current is not null && !ReferenceEquals(current, item); current = current switch
        {
            Visual => VisualTreeHelper.GetParent(current),
            FrameworkContentElement content => content.Parent,
            _ => LogicalTreeHelper.GetParent(current)
        })
            if (current is ButtonBase or TextBoxBase or ScrollBar) return false;

        list.SelectedItem = group;
        panel.SelectedCommandGroup = group;
        return true;
    }

    internal static void SelectCommandListRow(ListBox list, DependencyObject? source)
    {
        var item = source is null ? null : ItemsControl.ContainerFromElement(list, source) as ListBoxItem;
        list.SelectedIndex = item is null ? -1 : list.ItemContainerGenerator.IndexFromContainer(item);
    }

    internal static bool DeleteCommandListSelection(ListBox list)
    {
        if (list.DataContext is not CommandPanelViewModel panel) return false;
        if (list.SelectedItem is CommandGroupEditorViewModel group && ReferenceEquals(list.ItemsSource, panel.VisibleCommandGroups))
        {
            panel.SelectedCommandGroup = group;
            if (!panel.RemoveCurrentListMemberCommand.CanExecute(null)) return false;
            panel.RemoveCurrentListMemberCommand.Execute(null);
            return true;
        }
        if (list.SelectedItem is CommandGroupQueueItemViewModel entry && ReferenceEquals(list.ItemsSource, panel.GroupScheduler.Queue) &&
            panel.GroupScheduler.RemoveQueueItemCommand.CanExecute(entry))
        {
            panel.GroupScheduler.RemoveQueueItemCommand.Execute(entry);
            return true;
        }
        return false;
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

}
