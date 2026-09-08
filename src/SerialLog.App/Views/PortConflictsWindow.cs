using System.Windows;
using System.Windows.Controls;

namespace SerialLog.App.Views;

public sealed class PortConflictsWindow : Window
{
    public HashSet<string> Approved { get; } = [];
    public PortConflictsWindow(IReadOnlyList<(string Id, string Port, string Description)> conflicts)
    {
        Title = "批量连接：选择要转移的串口";
        Width = 600; Height = 380; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(12) };
        var hint = new TextBlock { Text = "仅连接勾选项，未勾选项保留原连接；每个端口最多选一个目标，无冲突端口正常连接。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,12) };
        DockPanel.SetDock(hint, Dock.Top); panel.Children.Add(hint);
        var button = new Button { Content = "继续连接", IsDefault = true, Padding = new Thickness(12,5,12,5), Margin = new Thickness(0,12,0,0) };
        button.Click += (_, _) => DialogResult = true;
        DockPanel.SetDock(button, Dock.Bottom); panel.Children.Add(button);
        var list = new StackPanel();
        var selections = new List<(string Port, CheckBox Check)>();
        foreach (var conflict in conflicts)
        {
            var check = new CheckBox { Content = conflict.Description, Margin = new Thickness(3,8,3,8) };
            check.Checked += (_, _) =>
            {
                foreach (var other in selections.Where(s => s.Port.Equals(conflict.Port, StringComparison.OrdinalIgnoreCase) && s.Check != check)) other.Check.IsChecked = false;
                Approved.Add(conflict.Id);
            };
            check.Unchecked += (_, _) => Approved.Remove(conflict.Id);
            list.Children.Add(check);
            selections.Add((conflict.Port, check));
        }
        panel.Children.Add(new ScrollViewer { Content = list }); Content = panel;
    }
}
