using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using SerialLog.Core.Firmware;

namespace SerialLog.App.Views;

/// <summary>仅编辑所属工作区的配置副本；关闭取消不会改动原配置或运行中的任务。</summary>
public sealed class YmodemRulesWindow : Window
{
    private readonly ComboBox _profiles = new() { MinWidth = 220, MaxWidth = 360, Margin = new Thickness(0, 0, 8, 0) };
    private readonly StackPanel _profileSettings = new();
    private readonly TabControl _tabs = new();
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
    private readonly Dictionary<(object Owner, string Field), string> _invalid = [];
    private YmodemRuleProfile? Current => _profiles.SelectedItem as YmodemRuleProfile;
    public ObservableCollection<YmodemRuleProfile> Profiles { get; }
    public string? SelectedProfileId => Current?.Id;

    public YmodemRulesWindow(IEnumerable<YmodemRuleProfile> profiles, string? selectedId)
    {
        Profiles = new(profiles.Select(YmodemRuleProfiles.Clone));
        Title = "Ymodem 规则设置"; Width = 1050; Height = 740; MinWidth = 720; MinHeight = 460;
        MaxHeight = SystemParameters.WorkArea.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false; AllowsTransparency = false;
        Background = (Brush?)Application.Current?.TryFindResource("BackgroundBrush") ?? new SolidColorBrush(Color.FromRgb(238, 242, 246));
        var root = new DockPanel { Margin = new Thickness(16), Background = Background };
        var footer = new StackPanel();
        footer.Children.Add(_error);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Button("取消", () => DialogResult = false); cancel.IsCancel = true; actions.Children.Add(cancel);
        actions.Children.Add(Button("保存规则", Save)); footer.Children.Add(actions);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "Ymodem 前置／后置规则", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) });
        var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        _profiles.ItemsSource = Profiles; _profiles.ItemTemplate = NameTemplate(); toolbar.Children.Add(_profiles);
        toolbar.Children.Add(Button("新建", () => Add(new YmodemRuleProfile())));
        toolbar.Children.Add(Button("复制", () => { if (Current is { } p) { var copy = YmodemRuleProfiles.Clone(p); copy.Id = Guid.NewGuid().ToString("N"); copy.Name += " 副本"; Add(copy); } }));
        toolbar.Children.Add(Button("删除", DeleteProfile)); toolbar.Children.Add(Button("导入 JSON", Import)); toolbar.Children.Add(Button("导出 JSON", Export));
        header.Children.Add(toolbar); header.Children.Add(_profileSettings);
        header.Children.Add(new TextBlock { Text = "前置和后置都可留空。步骤按顺序执行；变量仅在单个目标任务内共享。首次 C 握手由 Ymodem 引擎处理，无需添加等待 C 的步骤。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10) });
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header); root.Children.Add(_tabs); Content = root;
        _profiles.SelectionChanged += (_, _) => BuildProfile();
        _profiles.SelectedItem = Profiles.FirstOrDefault(p => p.Id == selectedId) ?? Profiles.FirstOrDefault();
        BuildProfile();
    }
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource { CompositionTarget: not null } source) source.CompositionTarget.RenderMode = RenderMode.SoftwareOnly;
    }
    private static DataTemplate NameTemplate()
    {
        var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetBinding(TextBlock.TextProperty, new Binding("Name"));
        return new DataTemplate { VisualTree = text };
    }
    private static Button Button(string text, Action action)
    {
        var button = new Button
        {
            Content = text, MinWidth = 58, Height = 28, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 4, 10, 4)
        };
        button.Click += (_, _) => action(); return button;
    }
    private static Grid StepActionRow(params Button[] buttons)
    {
        var row = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        for (var i = 0; i < buttons.Length; i++)
        {
            if (i > 0) row.ColumnDefinitions.Add(new() { Width = new GridLength(8) });
            row.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            var button = buttons[i];
            button.MinWidth = 0; button.Margin = new Thickness(0); button.Padding = new Thickness(6, 4, 6, 4);
            Grid.SetColumn(button, i * 2); row.Children.Add(button);
        }
        return row;
    }
    private void Add(YmodemRuleProfile profile) { Profiles.Add(profile); _profiles.SelectedItem = profile; }
    private void DeleteProfile()
    {
        if (Current is not { } profile || !DeleteConfirmationWindow.Confirm("删除规则方案", $"删除“{profile.Name}”？运行中的任务使用原快照，不受影响。", this)) return;
        RemoveInvalid(profile); Profiles.Remove(profile); _profiles.SelectedItem = Profiles.FirstOrDefault(); BuildProfile();
    }
    private void BuildProfile()
    {
        _profileSettings.Children.Clear(); _tabs.Items.Clear();
        if (Current is not { } profile) return;
        var settings = new WrapPanel();
        Text(settings, "方案名称（可直接重命名）", profile.Name, value => { profile.Name = value; _profiles.Items.Refresh(); });
        Number(settings, "要求波特率（0 表示不限）", profile, nameof(profile.RequiredBaudRate), profile.RequiredBaudRate, v => profile.RequiredBaudRate = v);
        _profileSettings.Children.Add(settings);
        _tabs.Items.Add(new TabItem { Header = "前置规则", Content = StepsEditor(profile, profile.Before) });
        _tabs.Items.Add(new TabItem { Header = "后置规则", Content = StepsEditor(profile, profile.After) });
        _tabs.Items.Add(new TabItem { Header = "帧模板", Content = TemplatesEditor(profile) });
    }
    private UIElement StepsEditor(YmodemRuleProfile profile, List<YmodemRuleStep> steps)
    {
        var list = new ListBox { ItemsSource = steps, ItemTemplate = NameTemplate() };
        var detail = new StackPanel { Margin = new Thickness(12) };
        void Rebuild()
        {
            detail.Children.Clear();
            if (list.SelectedItem is not YmodemRuleStep step) return;
            Text(detail, "步骤名称", step.Name, v => step.Name = v);
            var enabled = new CheckBox { Content = "启用此步骤", IsChecked = step.Enabled, Margin = new Thickness(0, 6, 0, 8) };
            enabled.Checked += (_, _) => step.Enabled = true; enabled.Unchecked += (_, _) => step.Enabled = false; detail.Children.Add(enabled);
            Choice(detail, "操作类型", step.Kind, v => { RemoveInvalid(step); step.Kind = v; Rebuild(); });
            if (step.Kind is YmodemRuleKind.Send or YmodemRuleKind.RequestResponse) SendEditor(detail, profile, step.Send);
            if (step.Kind is YmodemRuleKind.Wait or YmodemRuleKind.RequestResponse)
            {
                ReceiveEditor(detail, profile, step.Receive);
                Number(detail, "响应超时（ms）", step, nameof(step.TimeoutMilliseconds), step.TimeoutMilliseconds, v => step.TimeoutMilliseconds = v);
            }
            if (step.Kind is YmodemRuleKind.ModifyBytes or YmodemRuleKind.Assert)
            {
                Text(detail, "输入变量", step.InputVariable, v => step.InputVariable = v);
                if (step.Kind == YmodemRuleKind.ModifyBytes)
                {
                    Text(detail, "输出变量（复制后修改）", step.OutputVariable, v => step.OutputVariable = v);
                    Number(detail, "零基偏移", step, nameof(step.Offset), step.Offset, v => step.Offset = v);
                    Text(detail, "替换 Hex", step.ReplacementHex, v => step.ReplacementHex = v);
                }
                else
                {
                    Choice(detail, "断言类型", step.Assertion, v => { step.Assertion = v; Rebuild(); });
                    if (step.Assertion == RuleAssertionKind.Length) Number(detail, "期望长度（字节）", step, nameof(step.ExpectedLength), step.ExpectedLength, v => step.ExpectedLength = v);
                    else Text(detail, "期望内容", step.ExpectedValue, v => step.ExpectedValue = v);
                    if (step.Assertion == RuleAssertionKind.Text)
                    {
                        var trim = new CheckBox { Content = "去除文本末尾的零填充", IsChecked = step.TrimTrailingZeros, Margin = new Thickness(0, 8, 0, 8) };
                        trim.Checked += (_, _) => step.TrimTrailingZeros = true; trim.Unchecked += (_, _) => step.TrimTrailingZeros = false; detail.Children.Add(trim);
                    }
                }
            }
            if (step.Kind == YmodemRuleKind.Delay) Number(detail, "延时（ms，可取消）", step, nameof(step.DelayMilliseconds), step.DelayMilliseconds, v => step.DelayMilliseconds = v);
        }
        list.SelectionChanged += (_, _) => Rebuild();
        var buttons = StepActionRow(
            Button("新增", () => { var item = new YmodemRuleStep(); steps.Add(item); list.Items.Refresh(); list.SelectedItem = item; }),
            Button("删除", () => { if (list.SelectedItem is YmodemRuleStep item) { RemoveInvalid(item); steps.Remove(item); list.Items.Refresh(); Rebuild(); } }),
            Button("上移", () => Move(steps, list, -1)), Button("下移", () => Move(steps, list, 1)));
        if (steps.Count > 0) list.SelectedIndex = 0;
        return SplitEditor(list, buttons, detail);
    }
    private void SendEditor(Panel panel, YmodemRuleProfile profile, RuleSendData send)
    {
        var options = new StackPanel();
        void Build()
        {
            options.Children.Clear();
            if (send.Kind == RuleDataKind.Frame)
            {
                TemplateChoice(options, profile, send.TemplateId, v => send.TemplateId = v);
                Choice(options, "载荷来源", send.PayloadKind, v => { send.PayloadKind = v; Build(); }, [RuleDataKind.Text, RuleDataKind.Hex, RuleDataKind.Variable]);
                Text(options, "载荷内容／变量名", send.PayloadValue, v => send.PayloadValue = v);
                IntegersEditor(options, send.Integers, "发送整数字段值（留空使用模板默认值）");
            }
            else
            {
                Text(options, "发送内容／变量名", send.Value, v => send.Value = v, true);
                if (send.Kind == RuleDataKind.Text) StringChoice(options, "换行符", send.LineEnding, ["", "CR", "LF", "CRLF"], v => send.LineEnding = v);
            }
        }
        Choice(panel, "发送方式", send.Kind, v => { send.Kind = v; Build(); }); panel.Children.Add(options); Build();
    }
    private void ReceiveEditor(Panel panel, YmodemRuleProfile profile, RuleReceiveData receive)
    {
        var options = new StackPanel();
        void Build()
        {
            options.Children.Clear();
            if (receive.Kind == RuleDataKind.Frame)
            {
                TemplateChoice(options, profile, receive.TemplateId, v => receive.TemplateId = v);
                IntegersEditor(options, receive.Integers, "响应整数字段匹配（十进制）");
            }
            else Text(options, "等待匹配内容", receive.Value, v => receive.Value = v, true);
            Text(options, "保存响应为变量（可缺省）", receive.SaveVariable, v => receive.SaveVariable = v);
            options.Children.Add(new TextBlock { Text = "帧响应保存载荷；字段可用 变量名.字段名 引用。文本／Hex 保存命中的序列。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) });
        }
        Choice(panel, "响应类型", receive.Kind, v => { receive.Kind = v; Build(); }, [RuleDataKind.Text, RuleDataKind.Hex, RuleDataKind.Frame]);
        panel.Children.Add(options); Build();
    }
    private UIElement TemplatesEditor(YmodemRuleProfile profile)
    {
        var list = new ListBox { ItemsSource = profile.Templates, ItemTemplate = NameTemplate() };
        var detail = new StackPanel { Margin = new Thickness(12) };
        void Build()
        {
            detail.Children.Clear();
            if (list.SelectedItem is not SerialFrameTemplate template) return;
            Text(detail, "模板名称", template.Name, v => template.Name = v);
            var fields = new ListBox { ItemsSource = template.Fields, ItemTemplate = NameTemplate(), Height = 130, Margin = new Thickness(0, 8, 0, 8) };
            detail.Children.Add(fields);
            var toolbar = new WrapPanel(); var parameters = new StackPanel();
            void Parameters()
            {
                parameters.Children.Clear(); if (fields.SelectedItem is not SerialFrameField field) return;
                Text(parameters, "字段名（唯一）", field.Name, v => field.Name = v);
                Choice(parameters, "字段类型", field.Kind, v => { field.Kind = v; Parameters(); });
                if (field.Kind == FrameFieldKind.Fixed) Text(parameters, "固定 Hex", field.Hex, v => field.Hex = v);
                if (field.Kind is FrameFieldKind.Integer or FrameFieldKind.PayloadLength)
                {
                    Choice(parameters, "宽度（字节）", field.Width, v => field.Width = v, [1, 2, 4]);
                    Choice(parameters, "端序", field.Endian, v => field.Endian = v);
                    if (field.Kind == FrameFieldKind.Integer) Unsigned(parameters, "默认整数（十进制）", field, nameof(field.Value), field.Value, v => field.Value = v);
                }
                if (field.Kind == FrameFieldKind.Payload)
                {
                    Text(parameters, "引用长度字段（空则使用定长）", field.LengthField, v => field.LengthField = v);
                    Number(parameters, "固定长度（字节）", field, nameof(field.FixedLength), field.FixedLength, v => field.FixedLength = v);
                }
                if (field.Kind == FrameFieldKind.Checksum)
                {
                    Choice(parameters, "校验算法", field.Checksum, v => field.Checksum = v);
                    Text(parameters, "覆盖起始字段名", field.ChecksumFrom, v => field.ChecksumFrom = v);
                    Text(parameters, "覆盖结束字段名（含此字段）", field.ChecksumTo, v => field.ChecksumTo = v);
                    Choice(parameters, "校验输出端序（CRC16）", field.Endian, v => field.Endian = v);
                }
            }
            fields.SelectionChanged += (_, _) => Parameters();
            toolbar.Children.Add(Button("新增字段", () => { var field = new SerialFrameField { Name = "field" + (template.Fields.Count + 1) }; template.Fields.Add(field); fields.Items.Refresh(); fields.SelectedItem = field; }));
            toolbar.Children.Add(Button("删除字段", () => { if (fields.SelectedItem is SerialFrameField field) { RemoveInvalid(field); template.Fields.Remove(field); fields.Items.Refresh(); Parameters(); } }));
            toolbar.Children.Add(Button("上移", () => Move(template.Fields, fields, -1))); toolbar.Children.Add(Button("下移", () => Move(template.Fields, fields, 1)));
            detail.Children.Add(toolbar); detail.Children.Add(parameters); if (template.Fields.Count > 0) fields.SelectedIndex = 0;
        }
        list.SelectionChanged += (_, _) => Build();
        var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        actions.Children.Add(Button("新增模板", () => { var t = new SerialFrameTemplate(); profile.Templates.Add(t); list.Items.Refresh(); list.SelectedItem = t; }));
        actions.Children.Add(Button("删除模板", () => { if (list.SelectedItem is SerialFrameTemplate t) { RemoveInvalid(t); profile.Templates.Remove(t); list.Items.Refresh(); Build(); } }));
        if (profile.Templates.Count > 0) list.SelectedIndex = 0;
        return SplitEditor(list, actions, detail);
    }
    private static UIElement SplitEditor(ListBox list, Panel buttons, Panel detail)
    {
        var grid = new Grid(); grid.ColumnDefinitions.Add(new() { Width = new GridLength(230) }); grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var left = new DockPanel { Margin = new Thickness(8) }; DockPanel.SetDock(buttons, Dock.Bottom); left.Children.Add(buttons); left.Children.Add(list);
        grid.Children.Add(left); var scroll = new ScrollViewer { Content = detail, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        Grid.SetColumn(scroll, 1); grid.Children.Add(scroll); return grid;
    }
    private static void Move<T>(List<T> items, ListBox list, int delta)
    {
        if (list.SelectedItem is not T item) return;
        var from = items.IndexOf(item); var to = from + delta; if (to < 0 || to >= items.Count) return;
        items.RemoveAt(from); items.Insert(to, item); list.Items.Refresh(); list.SelectedItem = item;
    }
    private void IntegersEditor(Panel panel, List<FrameIntegerValue> values, string label)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 4) });
        var rows = new StackPanel(); panel.Children.Add(rows);
        void Build()
        {
            rows.Children.Clear();
            foreach (var item in values.ToArray())
            {
                var row = new WrapPanel(); Text(row, "字段名", item.Field, v => item.Field = v);
                Unsigned(row, "整数值", item, nameof(item.Value), item.Value, v => item.Value = v);
                // 参数行输入框统一高度，避免文本／整数字段的不同最小高度使底边错位。
                foreach (var input in row.Children.OfType<Panel>().SelectMany(field => field.Children.OfType<TextBox>()))
                    input.MinHeight = 28;
                var remove = Button("删除", () => { values.Remove(item); _invalid.Remove((item, nameof(item.Value))); Build(); });
                remove.VerticalAlignment = VerticalAlignment.Bottom;
                remove.Margin = new Thickness(0, 0, 8, 6);
                row.Children.Add(remove); rows.Children.Add(row);
            }
        }
        panel.Children.Add(Button("增加整数字段", () => { values.Add(new()); Build(); })); Build();
    }
    private static StackPanel Field(Panel panel, string label)
    {
        var field = new StackPanel { Margin = new Thickness(0, 4, 12, 6), MinWidth = 150 };
        field.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap }); panel.Children.Add(field); return field;
    }
    private void RemoveInvalid(object owner)
    {
        var owners = new HashSet<object>(ReferenceEqualityComparer.Instance) { owner };
        if (owner is YmodemRuleProfile profile)
        {
            foreach (var step in profile.Before.Concat(profile.After)) { RemoveInvalid(step); }
            foreach (var template in profile.Templates) RemoveInvalid(template);
        }
        if (owner is YmodemRuleStep rule)
            foreach (var value in rule.Send.Integers.Concat(rule.Receive.Integers)) owners.Add(value);
        if (owner is SerialFrameTemplate frame) foreach (var field in frame.Fields) owners.Add(field);
        foreach (var key in _invalid.Keys.Where(key => owners.Contains(key.Owner)).ToArray()) _invalid.Remove(key);
    }
    private static void Text(Panel panel, string label, string value, Action<string> set, bool multiline = false)
    {
        var input = new TextBox { Text = value, AcceptsReturn = multiline, MinHeight = multiline ? 50 : 26, TextWrapping = TextWrapping.Wrap };
        System.Windows.Automation.AutomationProperties.SetName(input, label);
        System.Windows.Input.InputMethod.SetIsInputMethodEnabled(input, true);
        input.TextChanged += (_, _) => set(input.Text); Field(panel, label).Children.Add(input);
    }
    private void Number(Panel panel, string label, object owner, string name, int value, Action<int> set)
    {
        var input = new TextBox { Text = _invalid.GetValueOrDefault((owner, name), value.ToString(CultureInfo.InvariantCulture)) };
        System.Windows.Automation.AutomationProperties.SetName(input, label);
        input.TextChanged += (_, _) =>
        {
            if (int.TryParse(input.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) { _invalid.Remove((owner, name)); set(number); input.ToolTip = null; }
            else { _invalid[(owner, name)] = input.Text; input.ToolTip = "请输入有效整数"; }
        };
        Field(panel, label).Children.Add(input);
    }
    private void Unsigned(Panel panel, string label, object owner, string name, uint value, Action<uint> set)
    {
        var input = new TextBox { Text = _invalid.GetValueOrDefault((owner, name), value.ToString(CultureInfo.InvariantCulture)) };
        System.Windows.Automation.AutomationProperties.SetName(input, label);
        input.TextChanged += (_, _) =>
        {
            if (uint.TryParse(input.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) { _invalid.Remove((owner, name)); set(number); input.ToolTip = null; }
            else { _invalid[(owner, name)] = input.Text; input.ToolTip = "请输入 0～4294967295 的十进制整数"; }
        };
        Field(panel, label).Children.Add(input);
    }
    private static string Label(object value) => value switch
    {
        YmodemRuleKind.Send => "发送", YmodemRuleKind.Wait => "等待响应", YmodemRuleKind.RequestResponse => "请求／响应",
        YmodemRuleKind.ModifyBytes => "修改字节", YmodemRuleKind.Assert => "断言", YmodemRuleKind.Delay => "延时",
        RuleDataKind.Text => "UTF-8 文本", RuleDataKind.Hex => "Hex 字节", RuleDataKind.Variable => "字节变量", RuleDataKind.Frame => "协议帧模板",
        RuleAssertionKind.Length => "长度", RuleAssertionKind.Hex => "Hex 内容", RuleAssertionKind.Text => "文本内容",
        FrameFieldKind.Fixed => "固定 Hex", FrameFieldKind.Integer => "整数", FrameFieldKind.PayloadLength => "载荷长度", FrameFieldKind.Payload => "载荷", FrameFieldKind.Checksum => "校验",
        FrameEndian.Little => "小端", FrameEndian.Big => "大端", FrameChecksum.Sum8 => "累加和 Sum8", FrameChecksum.Xor8 => "异或 Xor8", FrameChecksum.Crc16Xmodem => "CRC16/XMODEM",
        _ => value.ToString() ?? ""
    };
    private static void Choice<T>(Panel panel, string label, T selected, Action<T> set, T[]? values = null) where T : struct
    {
        var combo = new ComboBox(); var source = values ?? Enum.GetValues(typeof(T)).Cast<T>().ToArray();
        foreach (var value in source) combo.Items.Add(new ComboBoxItem { Content = Label(value), Tag = value });
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, selected));
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ComboBoxItem { Tag: T value }) set(value); };
        Field(panel, label).Children.Add(combo);
    }
    private static void StringChoice(Panel panel, string label, string selected, string[] values, Action<string> set)
    {
        var combo = new ComboBox(); foreach (var value in values) combo.Items.Add(new ComboBoxItem { Content = value.Length == 0 ? "无" : value, Tag = value });
        combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, selected));
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ComboBoxItem { Tag: string value }) set(value); }; Field(panel, label).Children.Add(combo);
    }
    private static void TemplateChoice(Panel panel, YmodemRuleProfile profile, string id, Action<string> set)
    {
        var combo = new ComboBox { ItemsSource = profile.Templates, ItemTemplate = NameTemplate(), SelectedItem = profile.Templates.FirstOrDefault(t => t.Id == id) };
        combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is SerialFrameTemplate template) set(template.Id); }; Field(panel, "帧模板").Children.Add(combo);
    }
    private void Save()
    {
        try
        {
            if (_invalid.Count > 0) throw new InvalidDataException("存在无效数字，请检查步骤及模板中的整数输入。");
            foreach (var profile in Profiles) YmodemRuleValidator.Validate(profile);
            DialogResult = true;
        }
        catch (Exception e) { _error.Text = e.Message; }
    }
    private void Import()
    {
        var dialog = new OpenFileDialog { Title = "导入 Ymodem 规则方案", Filter = "规则方案 (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 4 * YmodemRuleProfiles.MaxVariableBytes) throw new InvalidDataException("规则文件超过 4 MiB。");
            Add(YmodemRuleProfiles.Import(File.ReadAllText(dialog.FileName))); _error.Text = "导入成功；保存后应用到当前工作区。";
        }
        catch (Exception e) { _error.Text = $"导入失败：{e.Message}"; }
    }
    private void Export()
    {
        if (Current is not { } profile) return;
        try
        {
            if (_invalid.Count > 0) throw new InvalidDataException("请先修正无效数字。");
            var json = YmodemRuleProfiles.Export(profile);
            var dialog = new SaveFileDialog { Title = "导出 Ymodem 规则方案", Filter = "规则方案 (*.json)|*.json", FileName = "ymodem-rules.json" };
            if (dialog.ShowDialog(this) == true) { File.WriteAllText(dialog.FileName, json); _error.Text = "已导出方案及引用的帧模板。"; }
        }
        catch (Exception e) { _error.Text = $"导出失败：{e.Message}"; }
    }
}
