using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using SerialLog.App.Infrastructure;
using SerialLog.App.ViewModels;

namespace SerialLog.App.Views;

/// <summary>只编辑成员选择草稿；确认后由所属工作区设置窗口应用。</summary>
public sealed class CommandGroupListMembersWindow : Window
{
    public sealed class Member(string id, string name, bool selected) : ObservableObject
    {
        private bool _isSelected = selected;
        public string Id { get; } = id;
        public string Name { get; } = name;
        public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
    }
    public IReadOnlySet<string> SelectedGroupIds { get; private set; } = new HashSet<string>();
    public CommandGroupListMembersWindow(CommandGroupListViewModel list, IEnumerable<CommandGroupEditorViewModel> groups)
    {
        Title = $"列表成员 · {list.Name}"; Width = 540; Height = 480; MinWidth = 400; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        InputMethod.SetIsInputMethodEnabled(this, true);
        SetResourceReference(BackgroundProperty, "BackgroundBrush");
        var members = new ObservableCollection<Member>(groups.Select(group => new Member(group.Id, group.Name, group.ListIds.Contains(list.Id))));
        var view = new ListCollectionView(members);
        var root = new DockPanel { Margin = new(16) };
        var header = new StackPanel { Margin = new(0, 0, 0, 10) };
        header.Children.Add(new TextBlock { Text = "搜索并勾选列表成员", FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 6) });
        var search = new TextBox { ToolTip = "按命令组名称搜索；筛选不丢失已勾选项。" };
        System.Windows.Automation.AutomationProperties.SetName(search, "搜索命令组名称");
        search.TextChanged += (_, _) => { view.Filter = item => item is Member member && member.Name.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase); };
        header.Children.Add(search);
        header.Children.Add(new TextBlock { Text = "同一命令组可出现在多个列表。这里只修改分类关系，不复制、删除或执行命令。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 0) });
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 10, 0, 0) };
        Button AddButton(string text, Action action)
        {
            var button = new Button { Content = text, MinWidth = 66, Height = 28, Margin = new(0, 0, 8, 4) };
            if (text == "确定") { button.SetResourceReference(StyleProperty, "PrimaryButton"); button.FontWeight = FontWeights.Normal; }
            button.Click += (_, _) => action(); footer.Children.Add(button); return button;
        }
        AddButton("全选筛选项", () => { foreach (var member in view.Cast<Member>()) member.IsSelected = true; });
        AddButton("取消筛选项", () => { foreach (var member in view.Cast<Member>()) member.IsSelected = false; });
        var cancel = AddButton("取消", () => DialogResult = false); cancel.IsCancel = true;
        AddButton("确定", () => { SelectedGroupIds = members.Where(member => member.IsSelected).Select(member => member.Id).ToHashSet(); DialogResult = true; });
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var checkbox = new FrameworkElementFactory(typeof(CheckBox));
        checkbox.SetBinding(ContentControl.ContentProperty, new Binding(nameof(Member.Name)));
        checkbox.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(Member.IsSelected)) { Mode = BindingMode.TwoWay });
        checkbox.SetValue(MarginProperty, new Thickness(2, 3, 2, 3));
        root.Children.Add(new ListBox { ItemsSource = view, ItemTemplate = new DataTemplate { VisualTree = checkbox } });
        Content = root;
    }
}
