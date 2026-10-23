using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SerialLog.App.ViewModels;
using SerialLog.Core.Configuration;

namespace SerialLog.App.Views;

internal sealed class CommandPackageSendWindow : Window
{
    internal string ResultText { get; private set; } = "";
    internal CommandPackageSendWindow(IReadOnlyList<CommandPackagePeer> peers, string json, Func<string, string, Task<string>> send, bool workspace = false)
    {
        Title = workspace ? "协作发送工作区快照" : "协作发送命令组"; Width = 540; Height = 280; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "BackgroundBrush");
        var package = workspace ? null : CommandGroupPackageCodec.Parse(json);
        var root = new DockPanel { Margin = new(20) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var submit = new Button { Content = "发送", IsDefault = true };
        submit.SetResourceReference(StyleProperty, "PrimaryButton");
        actions.Children.Add(cancel); actions.Children.Add(submit); DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions);
        var body = new StackPanel(); root.Children.Add(body);
        body.Children.Add(new TextBlock { Text = workspace ? $"发送工作区：{WorkspacePackageCodec.Parse(json).Name}"
            : $"发送 {package!.Groups.Count} 个命令组、{package.Lists.Count} 个分类列表", FontSize = 18, TextTrimming = TextTrimming.CharacterEllipsis });
        var label = new FrameworkElementFactory(typeof(TextBlock)); label.SetBinding(TextBlock.TextProperty, new Binding(nameof(CommandPackagePeer.Name)));
        var target = new ComboBox { ItemsSource = peers, ItemTemplate = new DataTemplate { VisualTree = label }, SelectedIndex = 0, Margin = new(0, 14, 0, 10) };
        body.Children.Add(target);
        body.Children.Add(new TextBlock { Text = workspace ? "接收方确认后新建独立工作区，重新选择 COM 口；不自动连接、共享或执行。"
            : "接收方确认导入后生效，不携带串口目标，不自动执行。", TextWrapping = TextWrapping.Wrap });
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 10, 0, 0) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); body.Children.Add(status);
        var sending = false;
        Closing += (_, e) => { if (sending) e.Cancel = true; };
        submit.Click += async (_, _) =>
        {
            if (sending || target.SelectedItem is not CommandPackagePeer peer) return;
            sending = true; submit.IsEnabled = false; cancel.IsEnabled = false; target.IsEnabled = false;
            status.Text = "正在传输，等待接收回执…";
            try { ResultText = await send(peer.ConnectionId, json); sending = false; DialogResult = true; }
            catch (Exception ex) { status.Text = "发送失败：" + ex.Message; }
            finally { sending = false; submit.IsEnabled = true; cancel.IsEnabled = true; target.IsEnabled = true; }
        };
        Content = root;
    }
}

internal sealed class CommandPackageInboxWindow : Window
{
    internal IReadOnlyList<ReceivedCommandPackage> SelectedPackages { get; private set; } = [];
    internal CommandPackageInboxWindow(ObservableCollection<ReceivedCommandPackage> inbox, HashSet<string> imported, bool workspace = false)
    {
        Title = workspace ? "接收工作区快照" : "接收命令组配置"; Width = 620; Height = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false; SetResourceReference(BackgroundProperty, "BackgroundBrush");
        var root = new DockPanel { Margin = new(20) };
        var hint = new TextBlock { Text = workspace ? "选择一份快照，确认后创建独立工作区，不覆盖已有工作区。待确认快照仅保留至本次退出。"
            : "选择配置后确认导入；只追加到当前工作区。待确认配置仅保留至本次应用退出。", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) };
        DockPanel.SetDock(hint, Dock.Top); root.Children.Add(hint);
        var list = new ListBox { SelectionMode = workspace ? SelectionMode.Single : SelectionMode.Extended, DisplayMemberPath = nameof(ReceivedCommandPackage.DisplayName) };
        void Refresh() => list.ItemsSource = inbox.Where(item => !imported.Contains(item.Id)).ToArray();
        Refresh();
        System.Collections.Specialized.NotifyCollectionChangedEventHandler changed = (_, _) => Refresh();
        inbox.CollectionChanged += changed; Closed += (_, _) => inbox.CollectionChanged -= changed;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        var discard = new Button { Content = "丢弃所选", IsEnabled = false };
        var cancel = new Button { Content = "取消", IsCancel = true };
        var accept = new Button { Content = workspace ? "确认新建工作区" : "导入所选", IsEnabled = false };
        accept.SetResourceReference(StyleProperty, "PrimaryButton");
        discard.Click += (_, _) => { foreach (var item in list.SelectedItems.Cast<ReceivedCommandPackage>().ToArray()) inbox.Remove(item); };
        accept.Click += (_, _) => { SelectedPackages = list.SelectedItems.Cast<ReceivedCommandPackage>().ToArray(); DialogResult = true; };
        list.SelectionChanged += (_, _) => { accept.IsEnabled = discard.IsEnabled = list.SelectedItems.Count > 0; };
        actions.Children.Add(discard); actions.Children.Add(cancel); actions.Children.Add(accept);
        DockPanel.SetDock(actions, Dock.Bottom); root.Children.Add(actions); root.Children.Add(list); Content = root;
    }
}
