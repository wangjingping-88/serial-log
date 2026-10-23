using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using SerialLog.App.ViewModels;
using SerialLog.App.Views;
using SerialLog.Core.Configuration;

namespace SerialLog.Tests;

public class CommandGroupLayoutTests
{
    private static readonly XNamespace Ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static string Repo => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    [Fact]
    public void Repeated_tab_switches_keep_all_local_target_rows_at_exactly_the_same_positions()
    {
        InSta(() =>
        {
            var source = XElement.Load(Path.Combine(Repo, "src/SerialLog.App/Views/CommandPanelView.xaml"));
            var tab = source.Descendants(Ui + "TabItem").Single(t => (string?)t.Attribute("Header") == "目标窗口");
            var view = ParseStyled(new XElement(Ui + "TabControl", new XElement(tab)));
            using var vm = new MainViewModel(Path.Combine(Repo, "artifacts/not-saved-groups-layout.json"), false, configuration: new WorkspaceConfig
            {
                SerialWindows = Enumerable.Range(0, 5).Select(i => new SerialWindowConfig { Id = "node" + i, Title = i == 0 ? "AP" : "iote" + i }).ToList()
            });
            vm.AddCommandGroupCommand.Execute(null);
            view.DataContext = vm;
            foreach (var width in new[] { 600, 285 })
            {
                (double X, double Y, double Height, string Font)[]? baseline = null;
                for (var iteration = 0; iteration < 12; iteration++)
                {
                    vm.SelectedCommandPanelTabIndex = iteration % 3;
                    Layout(view, width, 200);
                    var visibleScroll = Descendants<ScrollViewer>(view).Single(s => s.Content is ItemsControl && s.Visibility == Visibility.Visible);
                    var boxes = Descendants<CheckBox>(visibleScroll).ToArray(); Assert.Equal(5, boxes.Length);
                    var positions = boxes.Select(c => (c.TranslatePoint(new Point(), view).X, c.TranslatePoint(new Point(), view).Y, c.ActualHeight, c.FontFamily.Source)).ToArray();
                    baseline ??= positions;
                    Assert.Equal(baseline, positions);
                    var selectAll = Descendants<CheckBox>(view).Where(c => Equals(c.Content, "全选") && c.Visibility == Visibility.Visible).Single();
                    Assert.Equal(boxes[0].FontFamily, selectAll.FontFamily);
                    if (iteration < 3) Render(view, width, 200, $"targets-{width}-{iteration}");
                }
            }
        });
    }

    [Fact]
    public void Outer_group_list_and_queue_render_with_real_bindings_and_scrollable_rows()
    {
        InSta(() =>
        {
            var source = XElement.Load(Path.Combine(Repo, "src/SerialLog.App/Views/CommandPanelView.xaml"));
            var tab = source.Descendants(Ui + "TabItem").Single(t => (string?)t.Attribute("Header") == "命令组");
            var view = ParseStyled(new XElement(Ui + "TabControl", new XElement(tab)));
            using var vm = new MainViewModel(Path.Combine(Repo, "artifacts/not-saved-groups-ui.json"), false, configuration: new WorkspaceConfig());
            for (var i = 0; i < 4; i++)
            {
                vm.AddCommandGroupCommand.Execute(null); var group = vm.SelectedCommandGroup!;
                group.Name = "中文命令组 " + i; group.LoopCount = 2; group.Commands.Add("AT");
                vm.CommandPanel.GroupScheduler.AddToQueue(group);
            }
            view.DataContext = vm;
            vm.CommandGroups[0].Name = "用于验证超长名称不换行的中文命令组";
            vm.CommandGroups[0].ExecutionStatus = "执行失败：用于验证很长的错误原因可以通过提示查看，但不会让列表条目增高";
            foreach (var width in new[] { 1100, 640 })
            {
                Layout(view, width, 360);
                var lists = Descendants<ListBox>(view).ToArray(); Assert.Equal(2, lists.Length);
                Assert.Contains(lists, list => ReferenceEquals(list.ItemsSource, vm.CommandPanel.VisibleCommandGroups));
                Assert.Contains(lists, list => ReferenceEquals(list.ItemsSource, vm.CommandPanel.GroupScheduler.Queue));
                var run = Descendants<Button>(view).First(b => Equals(b.Content, "运行"));
                Assert.Same(vm.CommandPanel.GroupScheduler.RunGroupCommand, run.Command);
                Assert.IsType<CommandGroupEditorViewModel>(run.CommandParameter);
                Assert.Contains(Descendants<Button>(view), b => Equals(b.Content, "命令组设置") && ReferenceEquals(b.Command, vm.CommandPanel.CommandGroupSettingsCommand));
                Assert.DoesNotContain(Descendants<Button>(view), b => b.Content is "上移" or "下移" or "一次" or "停止" or "删除");
                AssertGroupHeader(view, "命令组列表 · 全部（4）");
                AssertGroupHeader(view, "执行队列（4）");
                Assert.Contains(Descendants<TextBlock>(view), text => text.Text == "命令组（4）");
                Assert.All(lists, list => Assert.True(list.ActualHeight > 20));
                var groupList = lists.Single(list => ReferenceEquals(list.ItemsSource, vm.CommandPanel.VisibleCommandGroups));
                foreach (var row in Descendants<ListBoxItem>(groupList))
                {
                    Assert.InRange(row.ActualHeight, 24, 26);
                    var buttons = Descendants<Button>(row).ToArray();
                    Assert.Equal(new[] { "运行", "入队" }, buttons.Select(b => b.Content));
                    var name = Descendants<TextBlock>(row).Single(t => BindingOperations.GetBinding(t, TextBlock.TextProperty)?.Path.Path == "Name");
                    var summary = Descendants<TextBlock>(row).Single(t => BindingOperations.GetBinding(t, TextBlock.TextProperty)?.Path.Path == "Summary");
                    var status = Descendants<TextBlock>(row).Single(t => BindingOperations.GetBinding(t, TextBlock.TextProperty)?.Path.Path == "ExecutionStatus");
                    var center = name.TranslatePoint(new Point(0, name.ActualHeight / 2), row).Y;
                    foreach (var element in new FrameworkElement[] { summary, status, buttons[0], buttons[1] })
                        Assert.InRange(Math.Abs(element.TranslatePoint(new Point(0, element.ActualHeight / 2), row).Y - center), 0, 0.5);
                    Assert.True(buttons[0].TranslatePoint(new Point(), row).X > status.TranslatePoint(new Point(), row).X);
                    Assert.True(buttons[1].TranslatePoint(new Point(buttons[1].ActualWidth, 0), groupList).X <= groupList.ActualWidth);
                    Assert.Equal(((CommandGroupEditorViewModel)row.DataContext).Name, Descendants<CheckBox>(row).Single().ToolTip);
                    Assert.Equal(status.Text, status.ToolTip);
                    Assert.True(status.TranslatePoint(new Point(), row).X < row.ActualWidth * 0.5,
                        "状态列应左移，减少名称和参数摘要占用，给结果留出更宽空间。");
                }
                var queueList = lists.Single(list => ReferenceEquals(list.ItemsSource, vm.CommandPanel.GroupScheduler.Queue));
                Assert.All(Descendants<ListBoxItem>(queueList), row => Assert.InRange(row.ActualHeight, 24, 26));
                Render(view, width, 360, "groups-" + width);
            }
        });
    }

    [Fact]
    public void Command_editor_selects_all_from_the_start_and_does_not_scroll_long_commands_to_the_tail()
    {
        InSta(() =>
        {
            foreach (var command in new[] { "AT+中文=测试", "at+wiotasend=10000,12\\r\\n" + string.Concat(Enumerable.Repeat("0123456789", 200)), string.Empty })
            {
                var view = ParseStyled(new XElement(Ui + "Grid", new XElement(Ui + "TextBox",
                    new XAttribute("Text", command), new XAttribute("FontFamily", "Consolas"),
                    new XAttribute("VerticalAlignment", "Top"), new XAttribute("MinHeight", 30))));
                Layout(view, 520, 80);
                var input = Descendants<TextBox>(view).Single();
                input.CaretIndex = input.Text.Length; input.ScrollToEnd();
                Layout(view, 520, 80);
                CommandPanelViewModel.InitializeCommandEditorInput(input);
                for (var frame = 0; frame < 3; frame++) Layout(view, 520, 80);
                Assert.Equal(command, input.Text);
                Assert.Equal(command, input.SelectedText);
                Assert.Equal(0, input.SelectionStart); Assert.Equal(command.Length, input.SelectionLength);
                Assert.Equal(0, input.CaretIndex);
                Assert.InRange(input.HorizontalOffset, 0, 0.1);
                Assert.InRange(input.GetRectFromCharacterIndex(0).X, 0, 10);
                if (command.Length > 100) Render(view, 520, 80, "command-editor-selection-start");
            }
        });
    }

    [Fact]
    public void Double_click_edit_selects_the_clicked_group_but_excludes_checkboxes_buttons_blank_space_and_queue()
    {
        InSta(() =>
        {
            var source = XElement.Load(Path.Combine(Repo, "src/SerialLog.App/Views/CommandPanelView.xaml"));
            var tab = source.Descendants(Ui + "TabItem").Single(t => (string?)t.Attribute("Header") == "命令组");
            var listXml = tab.Descendants(Ui + "ListBox").Single(l => (string?)l.Attribute(X + "Name") == "CommandGroupsList");
            Assert.Equal("CommandList_EditGroupOnDoubleClick", (string?)listXml.Attribute("MouseDoubleClick"));
            var queueXml = tab.Descendants(Ui + "ListBox").Single(l => (string?)l.Attribute(X + "Name") == "CommandGroupQueueList");
            Assert.Null(queueXml.Attribute("MouseDoubleClick"));
            var view = ParseStyled(new XElement(Ui + "TabControl", new XElement(tab)));
            using var vm = new MainViewModel(Path.Combine(Repo, "artifacts/not-saved-group-double-click.json"), false,
                configuration: new WorkspaceConfig());
            using var background = new CommandPanelViewModel([], _ => { });
            background.AddCommandGroupCommand.Execute(null); var backgroundGroup = background.SelectedCommandGroup;
            vm.AddCommandGroupCommand.Execute(null); var first = vm.SelectedCommandGroup!;
            vm.AddCommandGroupCommand.Execute(null); var clicked = vm.SelectedCommandGroup!;
            first.IsSelectedForRun = false; clicked.IsSelectedForRun = true;
            vm.CommandPanel.GroupScheduler.AddToQueue(clicked);
            view.DataContext = vm; Layout(view, 1100, 240);
            var list = Descendants<ListBox>(view).Single(l => l.Name == "CommandGroupsList");
            var queue = Descendants<ListBox>(view).Single(l => l.Name == "CommandGroupQueueList");
            var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1);
            var textRegions = Descendants<TextBlock>(row).Where(t =>
                BindingOperations.GetBinding(t, TextBlock.TextProperty)?.Path.Path is "Name" or "Summary" or "ExecutionStatus").ToArray();
            Assert.Equal(3, textRegions.Length);
            var content = Descendants<Grid>(row).Single(g => g.Height == 24);
            foreach (var region in textRegions.Cast<DependencyObject>().Append(content))
            {
                vm.SelectedCommandGroup = first;
                Assert.True(CommandPanelView.SelectCommandGroupForEditing(list, region));
                Assert.Same(clicked, vm.SelectedCommandGroup); Assert.Same(clicked, list.SelectedItem);
                Assert.False(first.IsSelectedForRun); Assert.True(clicked.IsSelectedForRun);
            }
            var checkbox = Descendants<CheckBox>(row).Single(); Assert.Null(checkbox.Content);
            Assert.Equal(clicked.Name, System.Windows.Automation.AutomationProperties.GetName(checkbox));
            var excluded = Descendants<Button>(row).Cast<DependencyObject>().Append(checkbox)
                .Concat(Descendants<Border>(checkbox)).Concat(Descendants<Button>(row).SelectMany(b => Descendants<Border>(b)));
            foreach (var region in excluded)
            {
                vm.SelectedCommandGroup = first;
                Assert.False(CommandPanelView.SelectCommandGroupForEditing(list, region));
                Assert.Same(first, vm.SelectedCommandGroup);
            }
            Assert.False(CommandPanelView.SelectCommandGroupForEditing(list, list));
            Assert.False(CommandPanelView.SelectCommandGroupForEditing(list, null));
            var queueRow = (ListBoxItem)queue.ItemContainerGenerator.ContainerFromIndex(0);
            Assert.False(CommandPanelView.SelectCommandGroupForEditing(queue, Descendants<TextBlock>(queueRow).First()));
            Assert.Same(backgroundGroup, background.SelectedCommandGroup);
            Assert.False(first.IsSelectedForRun); Assert.True(clicked.IsSelectedForRun);
        });
    }

    [Fact]
    public void Infinite_group_enqueue_is_visibly_disabled_with_hover_reason_and_updates_immediately()
    {
        InSta(() =>
        {
            var source = XElement.Load(Path.Combine(Repo, "src/SerialLog.App/Views/CommandPanelView.xaml"));
            var tab = source.Descendants(Ui + "TabItem").Single(t => (string?)t.Attribute("Header") == "命令组");
            var view = ParseStyled(new XElement(Ui + "TabControl", new XElement(tab)));
            using var vm = new MainViewModel(Path.Combine(Repo, "artifacts/not-saved-queue-tooltip.json"), false, configuration: new WorkspaceConfig());
            vm.AddCommandGroupCommand.Execute(null); var group = vm.SelectedCommandGroup!;
            group.LoopCount = 0; group.LoopIntervalMilliseconds = 0;
            group.IsInfiniteLoop = true;
            view.DataContext = vm; Layout(view, 1100, 240);
            var enqueue = Descendants<Button>(view).Single(b => Equals(b.Content, "入队"));
            Assert.False(enqueue.IsEnabled); Assert.Contains("无限循环", enqueue.ToolTip?.ToString());
            Assert.Contains("0 次表示不循环，只执行一轮", enqueue.ToolTip?.ToString());
            Assert.True(ToolTipService.GetShowOnDisabled(enqueue));
            Assert.Equal(0.45, Descendants<Border>(enqueue).Single(b => b.Name == "Chrome").Opacity, 2);
            group.IsInfiniteLoop = false; Layout(view, 1100, 240); Assert.True(enqueue.IsEnabled);
            enqueue.Command.Execute(enqueue.CommandParameter); Layout(view, 1100, 240);
            AssertGroupHeader(view, "执行队列（1）");
            group.IsInfiniteLoop = true; Layout(view, 1100, 240); Assert.False(enqueue.IsEnabled);
            Render(view, 1100, 240, "groups-disabled-enqueue");
        });
    }

    [Fact]
    public void Clear_buttons_and_loop_checkbox_render_with_real_bindings_without_hiding_completed_status()
    {
        InSta(() =>
        {
            var source = XElement.Load(Path.Combine(Repo, "src/SerialLog.App/Views/CommandPanelView.xaml"));
            var tab = source.Descendants(Ui + "TabItem").Single(t => (string?)t.Attribute("Header") == "命令组");
            var view = ParseStyled(new XElement(Ui + "TabControl", new XElement(tab)));
            using var vm = new MainViewModel(Path.Combine(Repo, "artifacts/not-saved-clear-ui.json"), false, configuration: new WorkspaceConfig());
            vm.AddCommandGroupCommand.Execute(null); var group = vm.SelectedCommandGroup!;
            group.Commands.Add("AT"); group.ExecutionStatus = "完成：1 轮，成功 1，失败 0，跳过 0";
            view.DataContext = vm; Layout(view, 1100, 240);
            var clear = Assert.Single(Descendants<Button>(view).Where(b => Equals(b.Content, "清空")));
            Assert.DoesNotContain(Descendants<Button>(view), button => ReferenceEquals(button.Command, vm.CommandPanel.ClearCommandGroupsCommand));
            Assert.Same(vm.CommandPanel.GroupScheduler.ClearQueueCommand, clear.Command); Assert.False(clear.IsEnabled);
            vm.CommandPanel.GroupScheduler.AddToQueue(group); Layout(view, 1100, 240); Assert.True(clear.IsEnabled);
            clear.Command.Execute(null); Layout(view, 1100, 240); Assert.False(clear.IsEnabled);
            AssertGroupHeader(view, "执行队列（0）");
            var status = Descendants<TextBlock>(view).Single(t => BindingOperations.GetBinding(t, TextBlock.TextProperty)?.Path.Path == "ExecutionStatus");
            var measured = new FormattedText(status.Text, System.Globalization.CultureInfo.CurrentUICulture,
                status.FlowDirection, new Typeface(status.FontFamily, status.FontStyle, status.FontWeight, status.FontStretch),
                status.FontSize, status.Foreground, VisualTreeHelper.GetDpi(status).PixelsPerDip);
            Assert.True(status.ActualWidth >= measured.WidthIncludingTrailingWhitespace,
                $"常见完成状态不应省略：可用 {status.ActualWidth}，实际文字 {measured.WidthIncludingTrailingWhitespace}");
            Render(view, 1100, 240, "groups-clear-and-status");

            foreach (var isGroup in new[] { true, false })
            {
                XElement xml;
                object context;
                if (isGroup)
                {
                    xml = XElement.Load(Path.Combine(Repo, "src/SerialLog.App/Views/CommandGroupEditorView.xaml"));
                    xml.Attribute(X + "Class")!.Remove();
                    foreach (var setter in xml.Descendants(Ui + "EventSetter").ToArray()) setter.Remove();
                    foreach (var attribute in xml.DescendantsAndSelf().Attributes().Where(a => a.Name.LocalName.StartsWith("Preview", StringComparison.Ordinal) || a.Name.LocalName is "MouseDoubleClick" or "Drop").ToArray()) attribute.Remove();
                    context = vm.CommandPanel;
                }
                else
                {
                    xml = new XElement(Ui + "TabControl", new XElement(source.Descendants(Ui + "TabItem").Single(t => (string?)t.Attribute("Header") == "单条命令")));
                    context = vm;
                }
                var editor = ParseStyled(xml); editor.DataContext = context; Layout(editor, 1050, 410);
                var checkbox = Descendants<CheckBox>(editor).Single(c => Equals(c.Content, "无限循环"));
                var count = Descendants<TextBox>(editor).Single(t => BindingOperations.GetBinding(t, TextBox.TextProperty)?.Path.Path == (isGroup ? "SelectedCommandGroup.LoopCount" : "SingleCommandLoopCount"));
                Assert.False(checkbox.IsChecked); Assert.True(count.IsEnabled);
                checkbox.IsChecked = true; Layout(editor, 1050, 410);
                Assert.False(count.IsEnabled); Assert.True(isGroup ? group.IsInfiniteLoop : vm.SingleCommandInfiniteLoop);
                checkbox.IsChecked = false; Layout(editor, 1050, 410); Assert.True(count.IsEnabled);
                Render(editor, 1050, 410, isGroup ? "groups-infinite-checkbox" : "single-infinite-checkbox");
            }
        });
    }

    [Fact]
    public void Compact_layout_shows_six_full_groups_at_240_height_and_counts_stay_in_headers()
    {
        InSta(() =>
        {
            var source = XElement.Load(Path.Combine(Repo, "src/SerialLog.App/Views/CommandPanelView.xaml"));
            var tab = source.Descendants(Ui + "TabItem").Single(t => (string?)t.Attribute("Header") == "命令组");
            var view = ParseStyled(new XElement(Ui + "TabControl", new XElement(tab)));
            using var vm = new MainViewModel(Path.Combine(Repo, "artifacts/not-saved-compact-groups.json"), false, configuration: new WorkspaceConfig());
            for (var i = 0; i < 8; i++)
            {
                vm.AddCommandGroupCommand.Execute(null); vm.SelectedCommandGroup!.Name = "命令组 " + (i + 1);
                vm.SelectedCommandGroup.LoopCount = 1; vm.CommandPanel.GroupScheduler.AddToQueue(vm.SelectedCommandGroup);
            }
            view.DataContext = vm; Layout(view, 1100, 240);
            var list = Descendants<ListBox>(view).Single(l => ReferenceEquals(l.ItemsSource, vm.CommandPanel.VisibleCommandGroups));
            var fullRows = Descendants<ListBoxItem>(list).Count(row =>
            {
                var y = row.TranslatePoint(new Point(), list).Y;
                return y >= 1 && y + row.ActualHeight <= list.ActualHeight - 1;
            });
            Assert.True(fullRows >= 6, $"只有 {fullRows} 个完整条目，列表高度 {list.ActualHeight}");
            AssertGroupHeader(view, "命令组列表 · 全部（8）");
            AssertGroupHeader(view, "执行队列（8）");
            vm.CommandGroups.RemoveAt(0); Layout(view, 1100, 240);
            AssertGroupHeader(view, "命令组列表 · 全部（7）");
            AssertGroupHeader(view, "执行队列（7）");
            Assert.Contains(Descendants<TextBlock>(view), text => text.Text == "命令组（7）");
            Render(view, 1100, 240, "groups-compact-240");
        });
    }

    [Fact]
    public void Outer_context_menus_delete_selected_row_and_keyboard_helper_does_not_touch_another_workspace()
    {
        InSta(() =>
        {
            var source = XElement.Load(Path.Combine(Repo, "src/SerialLog.App/Views/CommandPanelView.xaml"));
            var tab = source.Descendants(Ui + "TabItem").Single(t => (string?)t.Attribute("Header") == "命令组");
            var view = ParseStyled(new XElement(Ui + "TabControl", new XElement(tab)));
            using var vm = new MainViewModel(Path.Combine(Repo, "artifacts/not-saved-delete-groups.json"), false, configuration: new WorkspaceConfig());
            // 命令组删除确认在生产窗口执行；本测试使用可注入确认的面板，避免显示模态弹框。
            using var panel = new CommandPanelViewModel([], _ => { }, confirmDelete: (_, _) => true);
            panel.AddCommandGroupCommand.Execute(null); var first = panel.SelectedCommandGroup!; first.LoopCount = 1;
            panel.AddCommandGroupCommand.Execute(null); var second = panel.SelectedCommandGroup!; second.LoopCount = 1;
            panel.GroupScheduler.AddToQueue(first); panel.GroupScheduler.AddToQueue(first); panel.GroupScheduler.AddToQueue(second);
            view.DataContext = vm; Layout(view, 1100, 240);
            var groupList = Descendants<ListBox>(view).Single(l => l.Name == "CommandGroupsList");
            var queueList = Descendants<ListBox>(view).Single(l => l.Name == "CommandGroupQueueList");
            // 设置与实际视图等价的面板绑定，验证上下文菜单经 PlacementTarget.Tag 解析所属工作区。
            groupList.DataContext = queueList.DataContext = panel; Layout(view, 1100, 240);
            groupList.SelectedIndex = 0; panel.SelectedCommandGroup = first;
            var row = (ListBoxItem)groupList.ItemContainerGenerator.ContainerFromIndex(1);
            CommandPanelView.SelectCommandListRow(groupList, Descendants<CheckBox>(row).Single());
            Assert.Same(second, groupList.SelectedItem);
            var groupMenu = groupList.ContextMenu!; groupMenu.PlacementTarget = groupList;
            Layout(groupMenu, 160, 60); var groupDelete = Assert.Single(groupMenu.Items.OfType<MenuItem>());
            Assert.Same(panel.RemoveCurrentListMemberCommand, groupDelete.Command); Assert.Equal("Delete", groupDelete.InputGestureText);
            Assert.True(CommandPanelView.DeleteCommandListSelection(groupList));
            Assert.Same(first, Assert.Single(panel.CommandGroups)); Assert.Equal(2, panel.GroupScheduler.Queue.Count);
            queueList.SelectedIndex = 1; var removed = (CommandGroupQueueItemViewModel)queueList.SelectedItem;
            var queueMenu = queueList.ContextMenu!; queueMenu.PlacementTarget = queueList; Layout(queueMenu, 160, 60);
            var queueDelete = Assert.Single(queueMenu.Items.OfType<MenuItem>());
            Assert.Same(panel.GroupScheduler.RemoveQueueItemCommand, queueDelete.Command); Assert.Same(removed, queueDelete.CommandParameter);
            Assert.True(CommandPanelView.DeleteCommandListSelection(queueList));
            Assert.Single(panel.GroupScheduler.Queue); Assert.DoesNotContain(removed, panel.GroupScheduler.Queue);
            CommandPanelView.SelectCommandListRow(groupList, groupList); Layout(view, 1100, 240);
            Assert.Equal(-1, groupList.SelectedIndex); Assert.False(CommandPanelView.DeleteCommandListSelection(groupList));
            Layout(groupMenu, 160, 60); Assert.False(groupDelete.IsEnabled);
            Assert.Empty(vm.CommandGroups); // 没有误删原视图绑定的另一工作区。
        });
    }

    [Fact]
    public void Workspace_import_shows_only_name_with_application_combo_template_and_switches_groups()
    {
        InSta(() =>
        {
            CommandGroupImportSource[] sources =
            [
                new(Guid.NewGuid().ToString("N"), "EcoLink测试", [new() { Name = "中心命令组" }]),
                new(Guid.NewGuid().ToString("N"), "另一个中文工作区", [new() { Name = "节点命令组" }])
            ];
            var window = new CommandGroupImportWindow(sources) { Resources = ParseStyled(new XElement(Ui + "Grid")).Resources };
            var content = (FrameworkElement)window.Content;
            try
            {
                Layout(content, 520, 360);
                var combo = Assert.Single(Descendants<ComboBox>(content));
                var list = Assert.Single(Descendants<ListBox>(content));
                foreach (var index in new[] { 0, 1, 0 })
                {
                    combo.SelectedIndex = index; Layout(content, 520, 360);
                    Assert.Same(sources[index], combo.SelectedItem); Assert.Same(sources[index].Groups, list.ItemsSource);
                    var label = Descendants<TextBlock>(combo).Single(t => BindingOperations.GetBinding(t, TextBlock.TextProperty)?.Path.Path == "Name");
                    Assert.Equal(sources[index].Name, label.Text);
                    Assert.DoesNotContain(sources[index].WorkspaceId, label.Text);
                    Assert.DoesNotContain("Groups", label.Text);
                    list.SelectedIndex = 0;
                    Assert.True(Descendants<Button>(content).Single(b => Equals(b.Content, "导入所选")).IsEnabled);
                }
                Render(content, 520, 360, "workspace-import");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void Group_order_can_only_be_changed_in_settings_and_applies_on_save()
    {
        InSta(() =>
        {
            using var original = new CommandPanelViewModel([], _ => { });
            original.AddCommandGroupCommand.Execute(null); original.SelectedCommandGroup!.Name = "第一组";
            original.AddCommandGroupCommand.Execute(null); original.SelectedCommandGroup!.Name = "第二组";
            foreach (var group in original.CommandGroups) { group.LoopCount = 1; original.GroupScheduler.AddToQueue(group); }
            using var draft = new CommandPanelViewModel([], _ => { });
            foreach (var group in original.CommandGroups) draft.CommandGroups.Add(new(group.ToConfig()));
            draft.SelectedCommandGroup = draft.CommandGroups[1];
            var xml = XElement.Load(Path.Combine(Repo, "src/SerialLog.App/Views/CommandGroupEditorView.xaml"));
            xml.Attribute(X + "Class")!.Remove();
            foreach (var element in xml.Descendants(Ui + "EventSetter").ToArray()) element.Remove();
            foreach (var attr in xml.DescendantsAndSelf().Attributes().Where(a => a.Name.LocalName is "PreviewMouseLeftButtonDown" or "PreviewMouseRightButtonDown" or "MouseDoubleClick" or "PreviewMouseMove" or "Drop" or "PreviewKeyDown").ToArray()) attr.Remove();
            var view = ParseStyled(xml); view.DataContext = draft; Layout(view, 1050, 410);
            var up = Descendants<Button>(view).Single(b => Equals(b.Content, "上移"));
            var down = Descendants<Button>(view).Single(b => Equals(b.Content, "下移"));
            Assert.Same(draft.GroupScheduler.MoveGroupUpCommand, up.Command);
            Assert.Same(draft.GroupScheduler.MoveGroupDownCommand, down.Command);
            Assert.True(up.IsEnabled); Assert.False(down.IsEnabled);
            up.Command.Execute(up.CommandParameter); Layout(view, 1050, 410);
            Assert.Equal(new[] { "第二组", "第一组" }, draft.CommandGroups.Select(g => g.Name));
            Assert.Equal("第二组", draft.SelectedCommandGroup!.Name);
            Assert.False(up.IsEnabled); Assert.True(down.IsEnabled);
            Assert.Equal(new[] { "第一组", "第二组" }, original.CommandGroups.Select(g => g.Name));
            original.ApplyCommandGroupSettings(draft.CommandGroups.Select(g => g.ToConfig()).ToArray(), draft.SelectedCommandGroup.Id);
            Assert.Equal(new[] { "第二组", "第一组" }, original.CommandGroups.Select(g => g.Name));
            Assert.Equal("第二组", original.SelectedCommandGroup!.Name);
            Assert.Equal(new[] { "第一组", "第二组" }, original.GroupScheduler.Queue.Select(q => q.Name));
        });
    }

    [Fact]
    public void Settings_order_list_is_always_visible_and_tracks_selected_position_after_repeated_moves()
    {
        InSta(() =>
        {
            using var draft = new CommandPanelViewModel([], _ => { });
            for (var index = 0; index < 30; index++)
            {
                draft.AddCommandGroupCommand.Execute(null); draft.SelectedCommandGroup!.Name = "测试命令组 " + (index + 1);
            }
            var selected = draft.SelectedCommandGroup!;
            var xml = XElement.Load(Path.Combine(Repo, "src/SerialLog.App/Views/CommandGroupEditorView.xaml"));
            xml.Attribute(X + "Class")!.Remove();
            foreach (var element in xml.Descendants(Ui + "EventSetter").ToArray()) element.Remove();
            foreach (var attr in xml.DescendantsAndSelf().Attributes().Where(a => a.Name.LocalName is "PreviewMouseLeftButtonDown" or "PreviewMouseRightButtonDown" or "MouseDoubleClick" or "PreviewMouseMove" or "Drop" or "PreviewKeyDown").ToArray()) attr.Remove();
            var view = ParseStyled(xml); view.DataContext = draft;
            // BringIntoView 需要真实呈现源；使用不显示、不抢焦点的 HWND 验证虚拟化滚动。
            using var presentation = new HwndSource(new HwndSourceParameters("CommandGroupOrderTest")
            { Width = 1050, Height = 410, WindowStyle = unchecked((int)0x80000000) });
            presentation.CompositionTarget.RenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            presentation.RootVisual = view; Layout(view, 1050, 410);
            var list = Descendants<ListBox>(view).Single(l => l.Name == "GroupOrderList");
            Assert.Same(draft.VisibleCommandGroups, list.ItemsSource);
            Assert.DoesNotContain(Descendants<ComboBox>(view), combo => ReferenceEquals(combo.ItemsSource, draft.CommandGroups));
            var smart = Descendants<Button>(view).Single(b => Equals(b.Content, "智能分组"));
            Assert.Same(draft.GroupScheduler.SmartGroupCommand, smart.Command);
            var position = Descendants<TextBlock>(view).Single(t => BindingOperations.GetBinding(t, TextBlock.TextProperty)?.Path.Path == "GroupScheduler.SelectedGroupPositionText");
            for (var index = 29; index >= 0; index--)
            {
                if (index < 29) draft.GroupScheduler.MoveGroupUpCommand.Execute(null);
                CommandGroupEditorView.RevealSelectedCommandGroup(list);
                for (var frame = 0; frame < 3; frame++) Layout(view, 1050, 410);
                Assert.Same(selected, list.SelectedItem); Assert.Same(selected, draft.SelectedCommandGroup);
                Assert.Equal($"第 {index + 1} / 30 组", position.Text);
                var row = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(selected));
                var top = row.TranslatePoint(new Point(), list).Y;
                Assert.True(top >= 0 && top + row.ActualHeight <= list.ActualHeight,
                    $"序号 {index + 1}，条目 top={top}, height={row.ActualHeight}, list={list.ActualHeight}; " +
                    string.Join(";", Descendants<ScrollViewer>(list).Select(s => $"offset={s.VerticalOffset}, viewport={s.ViewportHeight}, extent={s.ExtentHeight}, logical={s.CanContentScroll}")));
                if (index is 29 or 0) Render(view, 1050, 410, "group-order-visible-" + index);
            }
            Assert.False(draft.GroupScheduler.MoveGroupUpCommand.CanExecute(null));
            draft.GroupScheduler.MoveGroupDownCommand.Execute(null);
            CommandGroupEditorView.RevealSelectedCommandGroup(list); Layout(view, 1050, 410);
            Assert.Equal("第 2 / 30 组", position.Text);
            smart.Command.Execute(null); CommandGroupEditorView.RevealSelectedCommandGroup(list); Layout(view, 1050, 410);
            Assert.Same(selected, list.SelectedItem);
        });
    }

    [Fact]
    public void Settings_editor_uses_a_separate_draft_and_keeps_edit_copy_and_chinese_input_controls()
    {
        InSta(() =>
        {
            using var original = new CommandPanelViewModel([new("node", "原名字", refreshPortsOnCreate: false)], _ => { });
            original.AddCommandGroupCommand.Execute(null); original.SelectedCommandGroup!.Commands.Add("AT");
            using var draft = new CommandPanelViewModel(original.SerialWindows, _ => { });
            draft.CommandGroups.Add(new(original.SelectedCommandGroup.ToConfig()));
            draft.SyncCommandGroupTargets(); draft.SelectedCommandGroup = draft.CommandGroups.Single();
            var xml = XElement.Load(Path.Combine(Repo, "src/SerialLog.App/Views/CommandGroupEditorView.xaml"));
            xml.Attribute(X + "Class")!.Remove();
            foreach (var element in xml.Descendants(Ui + "EventSetter").ToArray()) element.Remove();
            foreach (var attr in xml.DescendantsAndSelf().Attributes().Where(a => a.Name.LocalName is "PreviewMouseLeftButtonDown" or "PreviewMouseRightButtonDown" or "MouseDoubleClick" or "PreviewMouseMove" or "Drop" or "PreviewKeyDown").ToArray()) attr.Remove();
            var view = ParseStyled(xml); view.DataContext = draft;
            foreach (var width in new[] { 1050, 860 })
            {
                Layout(view, width, 410);
                var commands = Descendants<ListBox>(view).Single(list => ReferenceEquals(list.ItemsSource, draft.SelectedCommandGroup.Commands));
                Assert.Same(draft.SelectedCommandGroup.Commands, commands.ItemsSource);
                Assert.Equal(new[] { "编辑", "复制命令", "删除" }, commands.ContextMenu!.Items.OfType<MenuItem>().Select(item => item.Header));
                var inputs = Descendants<TextBox>(view).Where(t => BindingOperations.GetBinding(t, TextBox.TextProperty)?.Path.Path is "SelectedCommandGroup.Name" or "SelectedCommandGroup.NewCommand").ToArray();
                Assert.Equal(2, inputs.Length); Assert.All(inputs, t => Assert.True(System.Windows.Input.InputMethod.GetIsInputMethodEnabled(t)));
                Assert.All(inputs, t => Assert.True(t.ActualWidth >= 90, $"输入框宽度 {t.ActualWidth}，窗口宽度 {width}"));
                var settings = Descendants<GroupBox>(view).Single(box => Equals(box.Header, "命令组设置"));
                var list = Descendants<GroupBox>(view).Single(box => Equals(box.Header, "命令列表"));
                Assert.InRange(Math.Abs(settings.ActualWidth - list.ActualWidth), 0, 0.5);
                Render(view, width, 410, "group-settings-" + width);
            }
            draft.SelectedCommandGroup.Name = "修改中文名称"; draft.SelectedCommandGroup.Commands[0] = "EDIT";
            original.SerialWindows.Single().Title = "中文节点";
            Assert.Equal("中文节点", draft.SelectedCommandGroup.Targets.Single().Title);
            Assert.NotEqual("修改中文名称", original.SelectedCommandGroup.Name);
            Assert.Equal("AT", original.SelectedCommandGroup.Commands.Single()); // 取消只丢弃编辑副本。
            original.SerialWindows.Single().Dispose();
        });
    }

    private static void AssertGroupHeader(DependencyObject view, string header)
    {
        Assert.Contains(Descendants<GroupBox>(view), box => Descendants<TextBlock>(box).Any(text => text.Text == header));
    }

    [Fact]
    public void Named_list_tabs_filter_shared_groups_inside_outer_group_tab_and_keep_queue_visible()
    {
        InSta(() =>
        {
            var source = XElement.Load(Path.Combine(Repo, "src/SerialLog.App/Views/CommandPanelView.xaml"));
            var tab = source.Descendants(Ui + "TabItem").Single(t => (string?)t.Attribute("Header") == "命令组");
            var view = ParseStyled(new XElement(Ui + "TabControl", new XElement(tab)));
            var ap = CommandGroupListConfig.CreateDefault(); ap.Name = "AP设备";
            var iot = new CommandGroupListConfig { Name = "IOT设备" };
            var shared = new CommandGroupConfig { Name = "共同命令", Commands = ["AT"], ListIds = [ap.Id, iot.Id] };
            using var vm = new MainViewModel(Path.Combine(Repo, "artifacts/not-saved-list-tabs.json"), false, configuration: new()
            {
                CommandGroupLists = [ap, iot], SelectedCommandGroupListId = ap.Id,
                CommandGroups = [shared, new() { Name = "AP专用", ListIds = [ap.Id] }, new() { Name = "IOT专用", ListIds = [iot.Id] }],
                CommandGroupQueue = [new() { GroupId = shared.Id }]
            });
            view.DataContext = vm;
            foreach (var width in new[] { 1100, 640 })
            {
                Layout(view, width, 260);
                var tabs = Descendants<TabControl>(view).Single(control => control.Name == "GroupListTabs");
                var panel = vm.CommandPanel; var queue = panel.GroupScheduler.Queue.Single();
                Assert.Equal(new[] { "全部", "AP设备", "IOT设备" }, tabs.Items.Cast<CommandGroupListViewModel>().Select(list => list.Name));
                var list = Descendants<ListBox>(view).Single(control => control.Name == "CommandGroupsList");
                for (var iteration = 0; iteration < 8; iteration++)
                {
                    tabs.SelectedIndex = iteration % 2 + 1; Layout(view, width, 260);
                    Assert.Equal(iteration % 2 == 0 ? "AP设备" : "IOT设备", panel.SelectedCommandGroupList!.Name);
                    Assert.Equal(2, list.Items.Count);
                    Assert.Same(panel.CommandGroups[0], list.Items[0]);
                    Assert.Same(queue, panel.GroupScheduler.Queue.Single());
                    Assert.All(Descendants<TabItem>(tabs), item => Assert.True(item.ActualWidth > 20));
                }
                Render(view, width, 260, "classified-groups-" + width);
            }
        });
    }

    [Fact]
    public void List_management_and_member_picker_render_names_and_keep_chinese_input_enabled()
    {
        InSta(() =>
        {
            using var panel = new CommandPanelViewModel([], _ => { });
            panel.AddCommandGroupListCommand.Execute(null); panel.SelectedCommandGroupList!.Name = "AP设备命令";
            panel.AddCommandGroupCommand.Execute(null); var group = panel.SelectedCommandGroup!; group.Name = "AP运行";
            panel.AddCommandGroupListCommand.Execute(null); panel.SelectedCommandGroupList!.Name = "IOT设备命令";
            panel.SelectedCommandGroupList = panel.AllCommandGroupsList;
            var xml = XElement.Load(Path.Combine(Repo, "src/SerialLog.App/Views/CommandGroupEditorView.xaml")); xml.Attribute(X + "Class")!.Remove();
            foreach (var setter in xml.Descendants(Ui + "EventSetter").ToArray()) setter.Remove();
            foreach (var attribute in xml.DescendantsAndSelf().Attributes().Where(attribute => attribute.Value.StartsWith("CommandGroupCommandsListBox_", StringComparison.Ordinal)).ToArray()) attribute.Remove();
            var view = ParseStyled(xml); view.DataContext = panel; Layout(view, 1050, 540);
            var name = Descendants<TextBox>(view).Single(input => BindingOperations.GetBinding(input, TextBox.TextProperty)?.Path.Path == "SelectedCommandGroupList.Name");
            var clear = Assert.Single(Descendants<Button>(view).Where(button => Equals(button.Content, "清空列表")));
            Assert.Same(panel.ClearCommandGroupsCommand, clear.Command); Assert.True(clear.IsEnabled);
            Assert.False(name.IsEnabled);
            panel.SelectedCommandGroupList = panel.CommandGroupLists[0]; Layout(view, 1050, 540);
            Assert.True(name.IsEnabled); Assert.True(System.Windows.Input.InputMethod.GetIsInputMethodEnabled(name));
            Assert.Equal(2, panel.CommandGroupMemberships.Count);
            Render(view, 1050, 540, "group-lists-settings");
            var picker = new CommandGroupListMembersWindow(panel.CommandGroupLists[0], panel.CommandGroups);
            var content = (FrameworkElement)picker.Content;
            content.Resources = ParseStyled(new XElement(Ui + "Grid")).Resources;
            Layout(content, 500, 360);
            Assert.Contains(Descendants<CheckBox>(content), box => Equals(box.Content, "AP运行"));
            Render(content, 500, 360, "group-list-members");
            picker.Close();
        });
    }

    private static Grid ParseStyled(XElement content)
    {
        foreach (var attribute in content.DescendantsAndSelf().Attributes().Where(a => a.Value.StartsWith("CommandList_", StringComparison.Ordinal)).ToArray()) attribute.Remove();
        foreach (var attribute in content.DescendantsAndSelf().Attributes().Where(a =>
            (a.Name.LocalName is "Click" or "Loaded" or "SelectionChanged") && a.Value.StartsWith("GroupOrder", StringComparison.Ordinal)).ToArray()) attribute.Remove();
        var app = XElement.Load(Path.Combine(Repo, "src/SerialLog.App/App.xaml"));
        var root = new XElement(Ui + "Grid", new XAttribute(XNamespace.Xmlns + "x", X),
            new XAttribute(XNamespace.Xmlns + "sys", "clr-namespace:System;assembly=mscorlib"),
            new XAttribute(XNamespace.Xmlns + "local", "clr-namespace:SerialLog.App;assembly=SerialLog.App"),
            new XElement(Ui + "Grid.Resources", app.Element(Ui + "Application.Resources")!.Elements()), content);
        return (Grid)XamlReader.Parse(root.ToString());
    }

    [Fact]
    public void Switching_between_overflowing_short_and_empty_categories_keeps_every_column_and_queue_in_place()
    {
        InSta(() =>
        {
            foreach (var slim in new[] { false, true })
            foreach (var width in new[] { 1100, 640 })
            {
                var source = XElement.Load(Path.Combine(Repo, "src/SerialLog.App/Views/CommandPanelView.xaml"));
                var tab = source.Descendants(Ui + "TabItem").Single(item => (string?)item.Attribute("Header") == "命令组");
                var view = ParseStyled(new XElement(Ui + "TabControl", new XElement(tab)));
                if (slim) view.Resources[typeof(ScrollViewer)] = new Style(typeof(ScrollViewer), (Style)view.FindResource("SlimScrollViewerStyle"));
                var ap = new CommandGroupListConfig { Name = "AP命令" }; var iot = new CommandGroupListConfig { Name = "IOT命令" };
                var common = new CommandGroupConfig { Name = "公共命令", ListIds = [ap.Id, iot.Id] };
                using var vm = new MainViewModel(Path.Combine(Repo, "artifacts/not-saved-stable-scrollbar.json"), false, configuration: new()
                {
                    CommandGroupLists = [ap, iot], SelectedCommandGroupListId = ap.Id,
                    CommandGroups = new[] { common }.Concat(Enumerable.Range(0, 20).Select(index => new CommandGroupConfig { Name = "AP" + index, ListIds = [ap.Id] }))
                        .Concat(Enumerable.Range(0, 2).Select(index => new CommandGroupConfig { Name = "IOT" + index, ListIds = [iot.Id] })).ToList()
                });
                view.DataContext = vm;
                using var presentation = new HwndSource(new HwndSourceParameters("StableCommandListTest")
                { Width = width, Height = 240, WindowStyle = unchecked((int)0x80000000) });
                presentation.CompositionTarget.RenderMode = RenderMode.SoftwareOnly; presentation.RootVisual = view;
                Layout(view, width, 240);
                var list = Descendants<ListBox>(view).Single(control => control.Name == "CommandGroupsList");
                var queue = Descendants<ListBox>(view).Single(control => control.Name == "CommandGroupQueueList");
                var scroll = Descendants<ScrollViewer>(list).Single();
                var bar = Descendants<ScrollBar>(list).Single(control => control.Orientation == Orientation.Vertical);
                double[]? baseline = null;
                for (var iteration = 0; iteration < 12; iteration++)
                {
                    vm.CommandPanel.SelectedCommandGroupList = vm.CommandPanel.CommandGroupLists[iteration % 2];
                    Layout(view, width, 240);
                    var first = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                    var elements = Descendants<TextBlock>(first).Where(text => BindingOperations.GetBinding(text, TextBlock.TextProperty)?.Path.Path is "Name" or "Summary" or "ExecutionStatus")
                        .Cast<FrameworkElement>().Concat(Descendants<Button>(first)).ToArray();
                    var geometry = elements.SelectMany(element => new[] { element.TranslatePoint(new Point(), view).X, element.ActualWidth })
                        .Concat(new[] { first.ActualWidth, list.ActualWidth, queue.TranslatePoint(new Point(), view).X, queue.ActualWidth }).ToArray();
                    baseline ??= geometry;
                    for (var index = 0; index < geometry.Length; index++)
                        Assert.True(Math.Abs(baseline[index] - geometry[index]) < 0.1, $"列 {index} 随滚动条变化发生位移：{baseline[index]} → {geometry[index]}，宽度 {width}，Slim {slim}");
                    Assert.Equal(Visibility.Visible, scroll.ComputedVerticalScrollBarVisibility);
                    if (iteration % 2 == 0) { Assert.True(scroll.ScrollableHeight > 0); Assert.Equal(1, bar.Opacity); }
                    else { Assert.Equal(0, scroll.ScrollableHeight); Assert.Equal(0, bar.Opacity); Assert.False(bar.IsHitTestVisible); }
                    if (iteration < 2 && slim) Render(view, width, 240, $"stable-scroll-{width}-{iteration}");
                }
                var contentWidth = scroll.ViewportWidth;
                foreach (var group in vm.CommandPanel.VisibleCommandGroups.ToArray()) group.ListIds.Remove(iot.Id);
                Layout(view, width, 240); Assert.Empty(list.Items); Assert.Equal(contentWidth, scroll.ViewportWidth); Assert.Equal(0, bar.Opacity);
                vm.CommandPanel.AddCommandGroupCommand.Execute(null); Layout(view, width, 240);
                Assert.Single(list.Items); Assert.Equal(contentWidth, scroll.ViewportWidth);
                var queueScroll = Descendants<ScrollViewer>(queue).Single(); var emptyQueueWidth = queueScroll.ViewportWidth;
                for (var index = 0; index < 20; index++) vm.CommandPanel.GroupScheduler.AddToQueue(vm.CommandPanel.CommandGroups[0]);
                Layout(view, width, 240); Assert.True(queueScroll.ScrollableHeight > 0); Assert.Equal(emptyQueueWidth, queueScroll.ViewportWidth);
                vm.CommandPanel.GroupScheduler.ClearQueueCommand.Execute(null); Layout(view, width, 240);
                Assert.Equal(emptyQueueWidth, queueScroll.ViewportWidth);
                Assert.Equal(0, Descendants<ScrollBar>(queue).Single(control => control.Orientation == Orientation.Vertical).Opacity);
            }
        });
    }

    [Fact]
    public void Larger_editor_fits_regular_settings_without_page_scrollbar_and_stays_within_small_screen()
    {
        InSta(() =>
        {
            Assert.Equal(new Size(1320, 920), CommandGroupSettingsWindow.CalculateInitialSize(new Size(1920, 1040)));
            Assert.Equal(new Size(1240, 640), CommandGroupSettingsWindow.CalculateInitialSize(new Size(1280, 680)));
            var windows = Enumerable.Range(0, 6).Select(index => new SerialWindowViewModel("node" + index, "设备" + index, refreshPortsOnCreate: false)).ToArray();
            try
            {
                using var panel = new CommandPanelViewModel(new(windows), _ => { });
                for (var index = 0; index < 3; index++) { panel.AddCommandGroupListCommand.Execute(null); panel.SelectedCommandGroupList!.Name = "设备命令" + index; }
                for (var index = 0; index < 17; index++) panel.AddCommandGroupCommand.Execute(null);
                var xml = XElement.Load(Path.Combine(Repo, "src/SerialLog.App/Views/CommandGroupEditorView.xaml")); xml.Attribute(X + "Class")!.Remove();
                foreach (var setter in xml.Descendants(Ui + "EventSetter").ToArray()) setter.Remove();
                foreach (var attribute in xml.DescendantsAndSelf().Attributes().Where(attribute => attribute.Value.StartsWith("CommandGroupCommandsListBox_", StringComparison.Ordinal)).ToArray()) attribute.Remove();
                var view = ParseStyled(xml); view.DataContext = panel;
                // 预留标题栏、说明、导入栏、页脚和窗口外边距，模拟 1320×920 对话框内的编辑区域。
                Layout(view, 1288, 720);
                var page = Descendants<ScrollViewer>(view).Single(scroll => scroll.Content is StackPanel);
                Assert.Equal(0, page.ScrollableHeight); Assert.Equal(Visibility.Collapsed, page.ComputedVerticalScrollBarVisibility);
                Assert.Equal(6, Descendants<CheckBox>(page).Count(box => windows.Any(window => Equals(box.Content, window.Title))));
                Render(view, 1288, 720, "group-lists-settings-enlarged");
                Layout(view, 868, 350);
                Assert.True(page.ScrollableHeight > 0); // 小屏保留兜底，不靠隐藏滚动条裁剪控件。
            }
            finally { foreach (var window in windows) window.Dispose(); }
        });
    }
    private static void Layout(FrameworkElement view, int width, int height)
    {
        view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
    }
    private static void Render(FrameworkElement view, int width, int height, string name)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var backdrop = new DrawingVisual();
        using (var drawing = backdrop.RenderOpen()) drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
        bitmap.Render(backdrop); bitmap.Render(view);
        var directory = Path.Combine(Repo, "artifacts/command-groups-render"); Directory.CreateDirectory(directory);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name + ".png")); encoder.Save(stream);
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T item) yield return item;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static void InSta(Action action)
    {
        Exception? failure = null; var thread = new Thread(() => { try { action(); } catch (Exception e) { failure = e; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(25)), "命令组界面渲染超时");
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
