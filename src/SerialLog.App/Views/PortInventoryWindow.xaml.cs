using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using SerialLog.App.Infrastructure;
using SerialLog.App.ViewModels;
using SerialLog.Core.Configuration;
using SerialLog.Core.Serial;

namespace SerialLog.App.Views;

public partial class PortInventoryWindow : Window
{
    private readonly LocalPortMonitor _monitor;
    private readonly TestWorkspaceManager _workspaces;
    private readonly Action<PortUse> _locate;
    private readonly DispatcherTimer _refreshUsage = new() { Interval = TimeSpan.FromSeconds(1) };
    private PortInventoryRow[] _rows = [];
    private LocalPortEvent[] _history = [];
    private GroupOption[] _groups = [];
    private string? _feedback;
    private bool _painting;
    private string SelectedGroup => (GroupFilter.SelectedItem as GroupOption)?.Id ?? PortInventorySettings.AllOrder;
    private PortInventoryRow? Selection => Ports.SelectedItem as PortInventoryRow;

    internal PortInventoryWindow(LocalPortMonitor monitor, TestWorkspaceManager workspaces, Action<PortUse> locate)
    {
        _monitor = monitor;
        _workspaces = workspaces;
        _locate = locate;
        _painting = true;
        InitializeComponent();
        _painting = false;
        _monitor.Changed += Render;
        _refreshUsage.Tick += (_, _) => Render();
        _refreshUsage.Start();
        Render();
    }

    protected override void OnClosed(EventArgs e)
    {
        _refreshUsage.Stop();
        _monitor.Changed -= Render;
        base.OnClosed(e);
    }

    private void Render()
    {
        if (_painting) return;
        _painting = true;
        try
        {
            var settings = _monitor.Settings;
            var group = SelectedGroup;
            var choices = new[] { new GroupOption(PortInventorySettings.AllOrder, "全部"),
                    new GroupOption(PortInventorySettings.UngroupedOrder, "未分组") }
                .Concat(settings.Groups.Select(g => new GroupOption(g.Id, g.Name))).ToArray();
            if (!choices.SequenceEqual(_groups))
            {
                _groups = choices;
                GroupFilter.ItemsSource = choices;
                GroupFilter.SelectedItem = choices.FirstOrDefault(g => g.Id == group) ?? choices[0];
                group = SelectedGroup;
            }
            NotificationToggle.IsChecked = settings.ShowNotifications;
            NotificationToggle.IsEnabled = ImportButton.IsEnabled = NewGroup.IsEnabled = _monitor.CanSaveSettings;

            var selected = Selection;
            var present = _monitor.Inventory.Ports.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
            var names = PortInventory.SortNames(present.Keys.Concat(_workspaces.Workspaces.SelectMany(w => w.ViewModel.SerialWindows)
                .Where(w => !w.IsRemote).SelectMany(w => new[] { w.PortName, w.ActiveLocalPortName }).OfType<string>()));
            var manualOrder = settings.Orders.GetValueOrDefault(group) ?? [];
            var indexes = manualOrder.Select((key, index) => (key, index))
                .ToDictionary(pair => pair.key, pair => pair.index, StringComparer.OrdinalIgnoreCase);
            var rows = names.Select(name => PortInventoryViewModel.CreateRow(
                    present.GetValueOrDefault(name) ?? new LocalPortInfo(name), present.ContainsKey(name),
                    settings, PortInventoryViewModel.FindUses(_workspaces.Workspaces, name)))
                .Where(row => group == PortInventorySettings.AllOrder ||
                    (group == PortInventorySettings.UngroupedOrder ? row.GroupId.Length == 0 : row.GroupId == group))
                .OrderBy(row => indexes.GetValueOrDefault(PortInventoryViewModel.SortKey(settings, group, row.Port), int.MaxValue));
            var filtered = PortInventoryViewModel.Filter(rows, Search.Text, out var queryError);
            if (!filtered.SequenceEqual(_rows))
            {
                _rows = filtered;
                Ports.ItemsSource = filtered;
                if (selected is not null)
                    Ports.SelectedItem = filtered.FirstOrDefault(row => row.PortName == selected.PortName);
            }
            var history = _monitor.Inventory.History.ToArray();
            if (!history.SequenceEqual(_history)) { _history = history; History.ItemsSource = history; }

            var health = _monitor.Inventory.ScanError is { } error ? $"枚举失败，保留上次清单：{error}"
                : !_monitor.Inventory.Initialized ? "正在读取系统端口…"
                : _monitor.DetailsError is not null ? "设备详情暂不可用，端口清单仍可用。" : null;
            Status.Text = string.Join("  ·  ", new[] { $"显示 {filtered.Length} 个端口，本机检测到 {present.Count} 个",
                queryError, _monitor.SettingsWarning, health, _feedback }.Where(s => !string.IsNullOrWhiteSpace(s)));
            UpdateActions();
        }
        finally { _painting = false; }
    }

    private void UpdateActions()
    {
        var canSave = _monitor.CanSaveSettings;
        EditGroup.IsEnabled = DeleteGroup.IsEnabled = canSave && _monitor.Settings.Groups.Any(g => g.Id == SelectedGroup);
        var row = Selection;
        AliasButton.IsEnabled = AssignGroup.IsEnabled = canSave && row is not null;
        var index = row is null ? -1 : Array.IndexOf(_rows, row);
        var canSort = canSave && string.IsNullOrWhiteSpace(Search.Text) && index >= 0;
        Earlier.IsEnabled = canSort && index > 0;
        Later.IsEnabled = canSort && index + 1 < _rows.Length;
        Locate.IsEnabled = row is not null && PortInventoryViewModel.FindUses(_workspaces.Workspaces, row.PortName).Length > 0;
        Add.IsEnabled = row is { Present: true };
    }

    private void Filter_Changed(object sender, TextChangedEventArgs e) => ChangedByUser();
    private void GroupFilter_Changed(object sender, SelectionChangedEventArgs e) => ChangedByUser();
    private void ChangedByUser() { if (!_painting) { _feedback = null; Render(); } }
    private void Ports_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (!_painting) UpdateActions(); }
    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _feedback = "已请求刷新。";
        _monitor.Refresh(includeDetails: true);
        Render();
    }
    private void Clear_Click(object sender, RoutedEventArgs e) => _monitor.ClearHistory();

    private void Notification_Changed(object sender, RoutedEventArgs e)
    {
        if (_painting) return;
        var enabled = NotificationToggle.IsChecked == true;
        EditSettings(s => s.ShowNotifications = enabled, enabled ? "已开启插拔弹框。" : "已关闭弹框，仍记录插拔事件。");
    }

    private void EditAlias_Click(object sender, RoutedEventArgs e)
    {
        if (Selection is not { } row) return;
        var fields = PromptText("编辑串口别名", [("别名（留空清除）", row.Alias, 80)]);
        if (fields is null) return;
        EditSettings(s => s.Aliases[row.Port.Key] = fields[0], "别名已保存。");
    }

    private void NewGroup_Click(object sender, RoutedEventArgs e) => EditGroupDetails(null);
    private void EditGroup_Click(object sender, RoutedEventArgs e) => EditGroupDetails(_monitor.Settings.Groups.FirstOrDefault(g => g.Id == SelectedGroup));
    private void EditGroupDetails(LocalPortGroup? current)
    {
        var fields = PromptText(current is null ? "新建串口分组" : "编辑串口分组",
            [("分组名称", current?.Name ?? "", 24), ("颜色（#RRGGBB）", current?.Color ?? PortInventorySettingsStore.DefaultGroupColor, 7)],
            values => values[0].Length == 0 ? "请输入分组名称。"
                : _monitor.Settings.Groups.Any(g => g.Id != current?.Id && string.Equals(g.Name, values[0], StringComparison.OrdinalIgnoreCase))
                    || values[0] is "全部" or "未分组" ? "该分组名称已被使用。"
                : !System.Text.RegularExpressions.Regex.IsMatch(values[1], @"\A#[0-9A-Fa-f]{6}\z") ? "请输入有效的 #RRGGBB 颜色。" : null);
        if (fields is null) return;
        var group = new LocalPortGroup(current?.Id ?? Guid.NewGuid().ToString("N"), fields[0], fields[1]);
        EditSettings(s =>
        {
            var index = s.Groups.FindIndex(g => g.Id == group.Id);
            if (index < 0) s.Groups.Add(group); else s.Groups[index] = group;
        }, "分组已保存。");
    }

    private void DeleteGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_monitor.Settings.Groups.FirstOrDefault(g => g.Id == SelectedGroup) is not { } group) return;
        if (!DeleteConfirmationWindow.Confirm("删除串口分组", $"删除“{group.Name}”？组内设备会移到未分组，现有连接和窗口不受影响。", this)) return;
        EditSettings(s =>
        {
            s.Groups.RemoveAll(g => g.Id == group.Id);
            s.Orders.Remove(group.Id);
            foreach (var key in s.Assignments.Where(p => p.Value.Equals(group.Id, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToArray())
                s.Assignments[key] = "";
        }, "分组已删除。");
    }

    private void AssignGroup_Click(object sender, RoutedEventArgs e)
    {
        if (Selection is not { } row) return;
        var options = new[] { new GroupOption("", "未分组") }
            .Concat(_monitor.Settings.Groups.Select(g => new GroupOption(g.Id, g.Name))).ToArray();
        if (Pick("移至分组", "选择分组", options, option => option.Name, Array.FindIndex(options, g => g.Id == row.GroupId)) is { } selected)
            EditSettings(s => s.Assignments[row.Port.Key] = selected.Id, "设备分组已保存。");
    }

    private void Move_Click(object sender, RoutedEventArgs e)
    {
        if (Selection is not { } row || !string.IsNullOrWhiteSpace(Search.Text)) return;
        var index = Array.IndexOf(_rows, row);
        var target = index + ((sender as Button)?.Tag as string == "-1" ? -1 : 1);
        if (index < 0 || target < 0 || target >= _rows.Length) return;
        var group = SelectedGroup;
        EditSettings(s =>
        {
            var keys = _rows.Select(r => PortInventoryViewModel.SortKey(s, group, r.Port)).ToArray();
            var old = s.Orders.GetValueOrDefault(group) ?? [];
            (keys[index], keys[target]) = (keys[target], keys[index]);
            var visible = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            using var iterator = ((IEnumerable<string>)keys).GetEnumerator();
            var order = new List<string>();
            foreach (var key in old)
            {
                if (!visible.Contains(key)) order.Add(key);
                else if (iterator.MoveNext()) order.Add(iterator.Current);
            }
            while (iterator.MoveNext()) order.Add(iterator.Current);
            s.Orders[group] = order;
        }, "端口顺序已保存。");
    }

    private void Locate_Click(object sender, RoutedEventArgs e)
    {
        if (Selection is not { } row) return;
        var uses = PortInventoryViewModel.FindUses(_workspaces.Workspaces, row.PortName);
        var use = uses.Length == 1 ? uses[0] : Pick("定位串口窗口", "选择要定位的窗口", uses, u => $"{u.Label}（{u.State}）", 0);
        if (use is null) return;
        Close();
        _locate(use);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (Selection is not { Present: true } row) return;
        var current = _monitor.Inventory.Find(row.PortName);
        if (current is null || (row.Port.DeviceId.Length > 0 && !string.Equals(current.DeviceId, row.Port.DeviceId, StringComparison.OrdinalIgnoreCase)))
        { _feedback = "端口或设备已发生变化，请刷新后重选。"; Render(); return; }
        var workspace = _workspaces.Active;
        var existing = PortInventoryViewModel.FindUses([workspace], row.PortName).FirstOrDefault();
        if (existing is not null)
        {
            if (DeleteConfirmationWindow.Confirm("当前工作区已使用此端口", $"“{existing.Window.Title}”已引用 {row.PortName}。是否定位该窗口？",
                    this, "定位"))
            { Close(); _locate(existing); }
            return;
        }
        var alias = _monitor.Settings.Alias(current);
        var added = workspace.ViewModel.AddInventoryWindow(row.PortName, alias.Length == 0 ? row.PortName : alias);
        Close();
        _locate(new PortUse(workspace, added, 0));
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "选择旧串口管理别名或分组文件（可多选）",
            Filter = "旧串口管理配置 (*.json;*.bak)|*.json;*.bak|全部文件|*.*", Multiselect = true };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            var legacy = PortInventorySettingsStore.ReadLegacy(picker.FileNames);
            var (merged, conflicts) = PortInventorySettingsStore.Merge(_monitor.Settings, legacy, preferImported: false);
            var comOnly = legacy.Aliases.Keys.Concat(legacy.Assignments.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(key => key.StartsWith("port:", StringComparison.OrdinalIgnoreCase));
            var preview = $"别名 {legacy.Aliases.Count} 项、分组 {legacy.Groups.Count} 个、分组关系 {legacy.Assignments.Count} 项、排序 {legacy.Orders.Count} 组。\n" +
                $"其中 {comOnly} 个条目只按 COM 匹配，换设备后请核对。原文件不会改动。\n\n冲突 {conflicts} 项。";
            if (conflicts == 0)
            {
                if (!DeleteConfirmationWindow.Confirm("导入旧串口设置", preview, this, "导入")) return;
            }
            else
            {
                var decision = Pick("导入旧串口设置", preview, new[] { "保留现有冲突值", "用导入值覆盖冲突" }, text => text, 0);
                if (decision is null) return;
                if (decision == "用导入值覆盖冲突")
                    merged = PortInventorySettingsStore.Merge(_monitor.Settings, legacy, preferImported: true).Settings;
            }
            SaveSettings(merged, "旧串口配置已导入。");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException
            or NotSupportedException or ArgumentException)
        { _feedback = $"导入失败：{ex.Message}"; Render(); }
    }

    private void EditSettings(Action<PortInventorySettings> edit, string result)
    {
        var settings = PortInventorySettingsStore.Clone(_monitor.Settings);
        edit(settings);
        SaveSettings(settings, result);
    }

    private void SaveSettings(PortInventorySettings settings, string result)
    {
        try { _feedback = result; _monitor.Save(settings); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or System.Text.Json.JsonException)
        { _feedback = $"保存失败：{ex.Message}"; Render(); }
    }

    private string[]? PromptText(string title, (string Label, string Text, int Max)[] fields, Func<string[], string?>? validate = null)
    {
        var panel = new StackPanel { Margin = new Thickness(16) };
        var inputs = new List<TextBox>();
        foreach (var field in fields)
        {
            var input = new TextBox { Text = field.Text, MaxLength = field.Max, Margin = new Thickness(0, 0, 0, 8) };
            InputMethod.SetIsInputMethodEnabled(input, true);
            panel.Children.Add(new Label { Content = field.Label, Target = input });
            panel.Children.Add(input);
            inputs.Add(input);
        }
        var dialog = CreateDialog(title, panel, out var ok);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
        error.SetResourceReference(ForegroundProperty, "DangerBrush");
        panel.Children.Insert(panel.Children.Count - 1, error);
        string[]? result = null;
        ok.Click += (_, _) =>
        {
            var values = inputs.Select(t => t.Text.Trim()).ToArray();
            if (validate?.Invoke(values) is { } message) { error.Text = message; return; }
            result = values;
            dialog.DialogResult = true;
        };
        dialog.Loaded += (_, _) => { inputs[0].Focus(); inputs[0].SelectAll(); };
        return dialog.ShowDialog() == true ? result : null;
    }

    private T? Pick<T>(string title, string description, IReadOnlyList<T> options, Func<T, string> label, int initial) where T : class
    {
        if (options.Count == 0) return null;
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var choices = new ComboBox { ItemsSource = options.Select(label).ToArray(), SelectedIndex = Math.Max(0, initial) };
        System.Windows.Automation.AutomationProperties.SetName(choices, title);
        panel.Children.Add(choices);
        var dialog = CreateDialog(title, panel, out var ok);
        ok.Click += (_, _) => { if (choices.SelectedIndex >= 0) dialog.DialogResult = true; };
        dialog.Loaded += (_, _) => choices.Focus();
        return dialog.ShowDialog() == true ? options[choices.SelectedIndex] : null;
    }

    private Window CreateDialog(string title, StackPanel panel, out Button ok)
    {
        var dialog = new Window { Owner = this, Title = title, Width = 470, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false, ResizeMode = ResizeMode.NoResize, Content = panel };
        dialog.SetResourceReference(BackgroundProperty, "BackgroundBrush");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        ok = new Button { Content = "确定", IsDefault = true, MinWidth = 72, Margin = new Thickness(0, 0, 8, 0) };
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "取消", IsCancel = true, MinWidth = 72 });
        panel.Children.Add(buttons);
        return dialog;
    }

    private sealed record GroupOption(string Id, string Name);
}
