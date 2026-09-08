using System.Windows;
using System.Windows.Controls;
using SerialLog.Core.Collaboration;
using SerialLog.Core.Configuration;

namespace SerialLog.App.Views;

/// <summary>固定绑定打开时的工作区；搜索不改变已勾选状态。</summary>
public sealed class WorkspaceSubscriptionsWindow : Window
{
    private readonly List<(RemoteWindowSubscription Subscription, bool Available)> _entries;
    private readonly HashSet<string> _selected;
    private readonly TreeView _tree = new();
    public IReadOnlyList<RemoteWindowSubscription> Selection => _entries.Where(e => _selected.Contains(e.Subscription.Id)).Select(e => e.Subscription).ToArray();

    public WorkspaceSubscriptionsWindow(string workspaceName, IReadOnlyList<CollaborationClientSnapshot> directory,
        IReadOnlyList<RemoteWindowSubscription> subscriptions)
    {
        Title = $"订阅窗口 — {workspaceName}";
        Width = 580; Height = 540; MinWidth = 400; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _selected = subscriptions.Select(s => s.Id).ToHashSet();
        _entries = directory.SelectMany(source => source.Windows.Where(w => w.IsShared).Select(window =>
            (subscriptions.FirstOrDefault(s => s.Source.ConnectionId == source.ConnectionId && s.Window.Id == window.Id)
                ?? new RemoteWindowSubscription { Source = source with { Windows = [] }, Window = window }, true))).ToList();
        _entries.AddRange(subscriptions.Where(s => !_entries.Any(e => e.Subscription.Id == s.Id)).Select(s => (s, false)));
        var panel = new DockPanel { Margin = new Thickness(12) };
        var search = new TextBox { Margin = new Thickness(0, 0, 0, 10), ToolTip = "搜索电脑、工作区、窗口或串口" };
        DockPanel.SetDock(search, Dock.Top); panel.Children.Add(search);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(6), Padding = new Thickness(14, 5, 14, 5) };
        var save = new Button { Content = "保存订阅", IsDefault = true, Margin = new Thickness(6), Padding = new Thickness(14, 5, 14, 5) };
        save.Click += (_, _) => DialogResult = true;
        footer.Children.Add(cancel); footer.Children.Add(save);
        DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer); panel.Children.Add(_tree);
        Content = panel;
        search.TextChanged += (_, _) => Rebuild(search.Text);
        Rebuild("");
    }

    private void Rebuild(string search)
    {
        _tree.Items.Clear();
        var matches = _entries.Where(e => $"{e.Subscription.Source.PcName} {e.Subscription.Source.WorkspaceName} {e.Subscription.Window.Title} {e.Subscription.Window.PortName}"
            .Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));
        foreach (var pc in matches.GroupBy(e => e.Subscription.Source.PcId))
        {
            var computer = new TreeViewItem { Header = pc.First().Subscription.Source.PcName, IsExpanded = true };
            foreach (var workspace in pc.GroupBy(e => e.Subscription.Source.WorkspaceId))
            {
                var branch = new TreeViewItem { Header = workspace.First().Subscription.Source.WorkspaceName, IsExpanded = true };
                foreach (var entry in workspace)
                {
                    var id = entry.Subscription.Id;
                    var check = new CheckBox { Content = $"{entry.Subscription.Window.Title} ({entry.Subscription.Window.PortName})" + (entry.Available ? "" : " — 离线/停止共享"),
                        IsChecked = _selected.Contains(id), Margin = new Thickness(3) };
                    check.Checked += (_, _) => _selected.Add(id);
                    check.Unchecked += (_, _) => _selected.Remove(id);
                    branch.Items.Add(check);
                }
                computer.Items.Add(branch);
            }
            _tree.Items.Add(computer);
        }
    }
}
