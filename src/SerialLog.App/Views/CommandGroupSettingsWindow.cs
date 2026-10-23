using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.IO;
using Microsoft.Win32;
using SerialLog.App.ViewModels;
using SerialLog.Core.Configuration;

namespace SerialLog.App.Views;

public sealed record CommandGroupImportSource(string WorkspaceId, string Name, IReadOnlyList<CommandGroupConfig> Groups);

public sealed class CommandGroupSettingsWindow : Window
{
    private readonly CommandPanelViewModel _ownerPanel;
    private readonly HashSet<string> _importedInboxIds = [];
    private readonly ComboBox _packageScope = new() { Width = 150, Margin = new(0, 0, 8, 4),
        ItemsSource = new[] { "当前分类列表", "全部命令组", "当前选中命令组" }, SelectedIndex = 0 };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 6) };
    public CommandPanelViewModel Editor { get; }
    public IReadOnlyList<CommandGroupConfig> Result { get; private set; } = [];
    public string? SelectedGroupId { get; private set; }
    public IReadOnlyList<CommandGroupListConfig> ListsResult { get; private set; } = [];
    public string? SelectedListId { get; private set; }

    public CommandGroupSettingsWindow(CommandPanelViewModel panel)
    {
        _ownerPanel = panel;
        _error.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        var initialSize = CalculateInitialSize(SystemParameters.WorkArea.Size);
        Title = "命令组设置"; Width = initialSize.Width; Height = initialSize.Height;
        MinWidth = Math.Min(900, Width); MinHeight = Math.Min(520, Height);
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        InputMethod.SetIsInputMethodEnabled(this, true);
        SetResourceReference(BackgroundProperty, "BackgroundBrush");
        Editor = new(panel.SerialWindows, text => _error.Text = text)
        { ImportSources = panel.ImportSources, GroupDeletionGuard = panel.CanDeleteCommandGroup };
        Editor.LoadCommandGroupLists(panel.CommandGroupLists.Select(list => list.ToConfig()), panel.SelectedCommandGroupList?.Id);
        foreach (var config in panel.CommandGroups.Select(g => g.ToConfig())) Editor.CommandGroups.Add(new(config));
        Editor.SyncCommandGroupTargets(); Editor.SelectedCommandGroup = Editor.VisibleCommandGroups.FirstOrDefault(g => g.Id == panel.SelectedCommandGroup?.Id) ?? Editor.VisibleCommandGroups.FirstOrDefault();
        foreach (var command in panel.CommandHistory) Editor.CommandHistory.Add(command);
        var root = new DockPanel { Margin = new(16) };
        var footer = new StackPanel(); footer.Children.Add(_error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Button("取消", () => DialogResult = false); cancel.IsCancel = true; actions.Children.Add(cancel);
        actions.Children.Add(Button("保存设置", Save)); footer.Children.Add(actions); DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var header = new StackPanel { Margin = new(0, 0, 0, 12) };
        header.Children.Add(new TextBlock { Text = "命令组设置", FontSize = 18, FontWeight = FontWeights.SemiBold });
        var toolbar = new WrapPanel { Margin = new(0, 10, 0, 6) };
        toolbar.Children.Add(Button("从其他工作区导入", Import));
        var imported = new ComboBox { ItemsSource = panel.ImportedAtCommands.ToArray(), MinWidth = 160, MaxWidth = 260, Margin = new(8, 0, 8, 0), ToolTip = "当前导入命令集" };
        toolbar.Children.Add(imported);
        toolbar.Children.Add(Button("导入命令入组", () => AddCommand(imported.SelectedItem as string)));
        var history = new ComboBox { ItemsSource = panel.CommandHistory.ToArray(), MinWidth = 160, MaxWidth = 260, Margin = new(8, 0, 8, 0), ToolTip = "发送历史" };
        toolbar.Children.Add(history); toolbar.Children.Add(Button("历史命令入组", () => AddCommand(history.SelectedItem as string)));
        header.Children.Add(toolbar);
        var exchange = new WrapPanel { Margin = new(0, 0, 0, 4) };
        exchange.Children.Add(_packageScope);
        exchange.Children.Add(Button("导出配置", ExportFile));
        exchange.Children.Add(Button("导入配置", ImportFile));
        exchange.Children.Add(Button("协作发送", SendPackage));
        var receive = Button("接收配置", ReceivePackages);
        var receiveLabel = new TextBlock();
        receiveLabel.SetBinding(TextBlock.TextProperty, new Binding("ReceivedCommandPackages.Count")
        { Source = panel, StringFormat = "接收配置（{0}）" });
        receive.Content = receiveLabel;
        exchange.Children.Add(receive);
        header.Children.Add(exchange);
        header.Children.Add(new TextBlock { Text = "修改保存后生效；正在运行的任务保持原配置。0 次表示不循环，只执行一轮；无限循环需勾选，不能入队。", TextWrapping = TextWrapping.Wrap });
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        root.Children.Add(new CommandGroupEditorView { DataContext = Editor }); Content = root;
        panel.GroupScheduler.PropertyChanged += OwnerSchedulerChanged;
        Closed += (_, _) => { panel.GroupScheduler.PropertyChanged -= OwnerSchedulerChanged; Editor.Dispose(); };
    }
    private void OwnerSchedulerChanged(object? sender, PropertyChangedEventArgs e)
    {
        Editor.ClearCommandGroupsCommand.RaiseCanExecuteChanged();
        Editor.DeleteCommandGroupCommand.RaiseCanExecuteChanged();
    }
    internal static Size CalculateInitialSize(Size workArea) => new(
        Math.Min(1320, Math.Max(1, workArea.Width - 40)),
        Math.Min(920, Math.Max(1, workArea.Height - 40)));

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource { CompositionTarget: not null } source) source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
    }
    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text, MinWidth = 58, Height = 28, Padding = new(10, 4, 10, 4), Margin = new(0, 0, 8, 4) };
        if (text == "保存设置") { button.SetResourceReference(StyleProperty, "PrimaryButton"); button.FontWeight = FontWeights.Normal; }
        else if (text.StartsWith("删除", StringComparison.Ordinal)) button.SetResourceReference(StyleProperty, "DangerButton");
        button.Click += (_, _) => action(); return button;
    }
    private void AddCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command) || Editor.SelectedCommandGroup is null) return;
        Editor.SelectedCommandGroup.NewCommand = command; Editor.AddCommandToGroupCommand.Execute(null);
    }
    private void Import()
    {
        var sources = Editor.ImportSources();
        if (sources.Count == 0) { _error.Text = "没有其他工作区可导入。"; return; }
        var picker = new CommandGroupImportWindow(sources) { Owner = this };
        if (picker.ShowDialog() == true) Editor.ImportCommandGroups(picker.SelectedGroups);
    }
    private void Save()
    {
        if (Descendants(Content as DependencyObject).OfType<TextBox>().Any(Validation.GetHasError))
        { _error.Text = "请修正标红的参数后再保存。"; return; }
        var configs = Editor.CommandGroups.Select(g => g.ToConfig()).ToArray();
        var lists = Editor.CommandGroupLists.Select(list => list.ToConfig()).ToArray();
        if (lists.Any(list => string.IsNullOrWhiteSpace(list.Name)) || lists.Select(list => list.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != lists.Length)
        { _error.Text = "列表名称不能为空或重复，请修改后保存。"; return; }
        if (configs.Any(g => string.IsNullOrWhiteSpace(g.Name))) { _error.Text = "命令组名称不能为空。"; return; }
        if (!_ownerPanel.CanApplyCommandGroupSettings(configs, out var reason, lists, Editor.SelectedCommandGroupList?.Id)) { _error.Text = reason; return; }
        Result = configs; ListsResult = lists; SelectedGroupId = Editor.SelectedCommandGroup?.Id;
        foreach (var item in _ownerPanel.ReceivedCommandPackages.Where(item => _importedInboxIds.Contains(item.Id)).ToArray())
            _ownerPanel.ReceivedCommandPackages.Remove(item);
        SelectedListId = Editor.SelectedCommandGroupList?.Id; DialogResult = true;
    }

    private string CreatePackage()
    {
        if (Descendants(Content as DependencyObject).OfType<TextBox>().Any(Validation.GetHasError))
            throw new InvalidDataException("请先修正标红的参数。");
        var groups = Editor.CommandGroups.Where(group => _packageScope.SelectedIndex switch
        {
            1 => true,
            2 => group == Editor.SelectedCommandGroup,
            _ => Editor.SelectedCommandGroupList is not { Id.Length: > 0 } list || group.ListIds.Contains(list.Id)
        }).Select(group => group.ToConfig()).ToArray();
        if (groups.Length == 0) throw new InvalidDataException("所选范围没有命令组。");
        var used = groups.SelectMany(g => g.ListIds).ToHashSet();
        return CommandGroupPackageCodec.Export(groups, Editor.CommandGroupLists.Where(l => used.Contains(l.Id)).Select(l => l.ToConfig()));
    }

    private void ExportFile()
    {
        try
        {
            var json = CreatePackage();
            var picker = new SaveFileDialog { Title = "导出独立命令组配置", Filter = "命令组配置 (*.json)|*.json", FileName = "command-groups.json" };
            if (picker.ShowDialog(this) != true) return;
            var temporary = picker.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temporary, json); File.Move(temporary, picker.FileName, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            _error.Text = "已导出独立配置，包含命令组及分类关系，不包含本机串口目标。";
        }
        catch (Exception ex) { _error.Text = "导出失败：" + ex.Message; }
    }

    private void ImportFile()
    {
        var picker = new OpenFileDialog { Title = "导入独立命令组配置", Filter = "命令组配置 (*.json)|*.json" };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(picker.FileName).Length > CommandGroupPackageCodec.MaxBytes) throw new InvalidDataException("配置超过 1 MiB。");
            Editor.ImportCommandPackage(File.ReadAllText(picker.FileName));
            _error.Text = "已追加到编辑副本，同名项自动重命名；请重新选择目标，保存设置后生效。";
        }
        catch (Exception ex) { _error.Text = "导入失败：" + ex.Message; }
    }

    private void SendPackage()
    {
        try
        {
            var json = CreatePackage();
            var peers = _ownerPanel.CommandPackagePeers();
            if (peers.Count == 0) throw new InvalidOperationException("没有支持传输的协作工作区；请连接协作并确认主机和接收方均为新版程序。");
            var dialog = new CommandPackageSendWindow(peers, json, _ownerPanel.SendCommandPackage) { Owner = this };
            if (dialog.ShowDialog() == true) _error.Text = dialog.ResultText;
        }
        catch (Exception ex) { _error.Text = "发送失败：" + ex.Message; }
    }

    private void ReceivePackages()
    {
        var dialog = new CommandPackageInboxWindow(_ownerPanel.ReceivedCommandPackages, _importedInboxIds) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            foreach (var item in dialog.SelectedPackages)
            {
                Editor.ImportCommandPackage(item.Json); _importedInboxIds.Add(item.Id);
            }
            _error.Text = "已导入到编辑副本，请重新选择目标后保存；取消设置时接收箱中的配置仍保留。";
        }
        catch (Exception ex) { _error.Text = "导入失败：" + ex.Message; }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject? node)
    {
        if (node is null) yield break;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i); yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
}

public sealed class CommandGroupImportWindow : Window
{
    private readonly ListBox _groups = new() { SelectionMode = SelectionMode.Extended };
    public IReadOnlyList<CommandGroupConfig> SelectedGroups { get; private set; } = [];
    public CommandGroupImportWindow(IReadOnlyList<CommandGroupImportSource> sources)
    {
        Title = "从其他工作区导入命令组"; Width = 560; Height = 440; MinWidth = 400; MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "BackgroundBrush");
        var root = new DockPanel { Margin = new(16) };
        var header = new StackPanel { Margin = new(0, 0, 0, 10) };
        header.Children.Add(new TextBlock { Text = "选择来源工作区和命令组（Ctrl／Shift 多选）", Margin = new(0, 0, 0, 6) });
        // 应用默认 ComboBox.ItemTemplate 绑定整个对象，会覆盖 DisplayMemberPath 的显示效果。
        var workspaceName = new FrameworkElementFactory(typeof(TextBlock));
        workspaceName.SetBinding(TextBlock.TextProperty, new Binding(nameof(CommandGroupImportSource.Name)));
        workspaceName.SetBinding(TextBlock.ToolTipProperty, new Binding(nameof(CommandGroupImportSource.Name)));
        workspaceName.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        var workspaces = new ComboBox
        {
            ItemsSource = sources,
            ItemTemplate = new DataTemplate { VisualTree = workspaceName },
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new(0),
            Padding = new(7, 2, 7, 2)
        };
        System.Windows.Automation.AutomationProperties.SetName(workspaces, "来源工作区");
        header.Children.Add(workspaces);
        header.Children.Add(new TextBlock { Text = "复制命令和参数，来源保持不变；目标不直接沿用，请在当前工作区重新选择。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) });
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 10, 0, 0) };
        var selectAll = new Button { Content = "全选", Margin = new(0, 0, 8, 0) }; selectAll.Click += (_, _) => _groups.SelectAll(); footer.Children.Add(selectAll);
        var cancel = new Button { Content = "取消", IsCancel = true, Margin = new(0, 0, 8, 0) }; cancel.Click += (_, _) => DialogResult = false; footer.Children.Add(cancel);
        var import = new Button { Content = "导入所选", IsEnabled = false }; footer.Children.Add(import);
        import.SetResourceReference(StyleProperty, "PrimaryButton"); import.FontWeight = FontWeights.Normal;
        import.Click += (_, _) => { SelectedGroups = _groups.SelectedItems.Cast<CommandGroupConfig>().ToArray(); DialogResult = true; };
        _groups.SelectionChanged += (_, _) => import.IsEnabled = _groups.SelectedItems.Count > 0;
        workspaces.SelectionChanged += (_, _) => _groups.ItemsSource = (workspaces.SelectedItem as CommandGroupImportSource)?.Groups;
        _groups.DisplayMemberPath = "Name"; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer); root.Children.Add(_groups); Content = root;
        workspaces.SelectedIndex = 0;
    }
}
