using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;

namespace SerialLog.Tests;

public sealed class WorkspaceHeaderXamlTests
{
    private static readonly XNamespace Ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static XElement ReadWindow() => XElement.Load(Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "../../../../../src/SerialLog.App/MainWindow.xaml")));
    private static XElement Named(XElement root, string name) => root.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == name);

    [Fact]
    public void Workspace_template_displays_live_name_instead_of_object_type()
    {
        var selector = Named(ReadWindow(), "WorkspaceSelector");
        Assert.Null(selector.Attribute("DisplayMemberPath"));
        var templateXaml = selector.Element(Ui + "ComboBox.ItemTemplate")!.Element(Ui + "DataTemplate")!.ToString();
        InSta(() =>
        {
            var template = (DataTemplate)XamlReader.Parse(templateXaml);
            var label = (TextBlock)template.LoadContent();
            var item = new HeaderItem { DisplayName = "默认测试 · 连接 0" };
            label.DataContext = item;
            label.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.Equal(item.DisplayName, label.Text);
            item.DisplayName = "网关测试 · 连接 2 · 循环中";
            label.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.Equal(item.DisplayName, label.Text);
            Assert.Equal(TextTrimming.CharacterEllipsis, label.TextTrimming);
        });
    }

    [Fact]
    public void Workspace_menu_has_same_spacing_as_adjacent_menus()
    {
        var root = ReadWindow();
        var workspace = Named(root, "WorkspaceMenuToggle");
        var page = Named(root, "PageMenuToggle");
        Assert.Equal((string?)page.Attribute("Style"), (string?)workspace.Attribute("Style"));
        Assert.Null(workspace.Attribute("Margin"));
        var margin = (Thickness)new ThicknessConverter().ConvertFromInvariantString((string)workspace.Parent!.Attribute("Margin")!)!;
        Assert.Equal(0, margin.Right);
    }

    [Fact]
    public void Empty_workspace_background_is_hit_testable_for_menu_dismissal()
    {
        var viewport = Named(ReadWindow(), "WorkspaceViewport");
        Assert.Equal("WorkspaceViewport_PreviewMouseDown", (string?)viewport.Attribute("PreviewMouseDown"));
        var background = (string?)viewport.Attribute("Background");
        Assert.Equal("Transparent", background);
        InSta(() =>
        {
            var grid = new Grid { Background = (Brush)new BrushConverter().ConvertFromInvariantString(background!)! };
            var root = new Border { Background = Brushes.LightGray, Child = grid };
            root.Measure(new Size(400, 300));
            root.Arrange(new Rect(0, 0, 400, 300));
            root.UpdateLayout();
            Assert.Same(grid, VisualTreeHelper.HitTest(root, new Point(200, 150))?.VisualHit);
        });
    }

    [Fact]
    public void Collaboration_actions_have_consistent_vertical_spacing()
    {
        var popup = Named(ReadWindow(), "CollaborationMenuPopup");
        var panel = popup.Descendants(Ui + "StackPanel").Single(e => (string?)e.Attribute("Grid.Row") == "6");
        var panelMargin = (Thickness)new ThicknessConverter().ConvertFromInvariantString((string)panel.Attribute("Margin")!)!;
        Assert.Equal(6, panelMargin.Top);
        var buttons = panel.Elements(Ui + "Button").ToArray();
        Assert.Equal(3, buttons.Length);
        foreach (var button in buttons)
        {
            var margin = (Thickness)new ThicknessConverter().ConvertFromInvariantString((string)button.Attribute("Margin")!)!;
            Assert.Equal(new Thickness(4, 3, 4, 3), margin);
        }
    }

    [Fact]
    public void Workspace_actions_use_page_menu_spacing_with_separated_groups()
    {
        var root = ReadWindow();
        var panel = Named(root, "WorkspaceMenuPopup").Descendants(Ui + "StackPanel").Single();
        var buttons = panel.Elements(Ui + "Button").ToArray();
        Assert.Equal(5, buttons.Length);
        Assert.Equal(new[] { "new", "copy", "rename", "stop", "delete" }, buttons.Select(b => (string?)b.Attribute("Tag")));
        var page = Named(root, "PageMenuPopup");
        var pageSpacing = (string?)page.Descendants(Ui + "Button").Single(b => (string?)b.Attribute("Content") == "删除当前页").Attribute("Margin");
        foreach (var index in new[] { 1, 2, 4 })
            Assert.Equal(pageSpacing, (string?)buttons[index].Attribute("Margin"));
        Assert.Equal((string?)page.Descendants(Ui + "Separator").First().Attribute("Margin"),
            (string?)panel.Element(Ui + "Separator")!.Attribute("Margin"));
    }

    [Fact]
    public void Workspace_name_enables_ime_without_changing_default_textbox_protection()
    {
        InSta(() =>
        {
            var appXaml = XElement.Load(Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory, "../../../../../src/SerialLog.App/App.xaml")));
            var setter = appXaml.Descendants(Ui + "Style")
                .Single(e => (string?)e.Attribute("TargetType") == "TextBox" && e.Attribute(Xaml + "Key") is null)
                .Elements(Ui + "Setter").Single(e => (string?)e.Attribute("Property") == "InputMethod.IsInputMethodEnabled");
            var style = new Style(typeof(TextBox));
            style.Setters.Add(new Setter(System.Windows.Input.InputMethod.IsInputMethodEnabledProperty,
                bool.Parse((string)setter.Attribute("Value")!)));
            var ordinary = new TextBox { Style = style };
            var name = SerialLog.App.MainWindow.CreateWorkspaceNameInput("中文测试 副本");
            name.Style = style;
            Assert.False(System.Windows.Input.InputMethod.GetIsInputMethodEnabled(ordinary));
            Assert.True(System.Windows.Input.InputMethod.GetIsInputMethodEnabled(name));
            Assert.Equal("中文测试 副本", name.Text);
            Assert.Equal(80, name.MaxLength);
            Assert.Equal("False", (string?)ReadWindow().Attribute("InputMethod.IsInputMethodEnabled"));
        });
    }

    [Theory]
    [InlineData("CommandText")]
    [InlineData("SelectedCommandGroup.NewCommand")]
    [InlineData("SelectedAtCommandSet.Name")]
    [InlineData("SelectedCommandGroup.Name")]
    public void Command_editors_override_disabled_ime_in_docked_and_floating_panels(string bindingPath)
    {
        var panel = XElement.Load(Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "../../../../../src/SerialLog.App/Views/CommandPanelView.xaml")));
        var editor = panel.Descendants(Ui + "TextBox")
            .Single(e => ((string?)e.Attribute("Text"))?.StartsWith("{Binding " + bindingPath + ",") == true);
        InSta(() =>
        {
            foreach (var inheritedEnabled in new[] { false, true })
            {
                var parent = new Grid();
                System.Windows.Input.InputMethod.SetIsInputMethodEnabled(parent, inheritedEnabled);
                var style = new Style(typeof(TextBox));
                style.Setters.Add(new Setter(System.Windows.Input.InputMethod.IsInputMethodEnabledProperty, false));
                var input = (TextBox)XamlReader.Parse(editor.ToString());
                input.Style = style;
                parent.Children.Add(input);
                Assert.True(System.Windows.Input.InputMethod.GetIsInputMethodEnabled(input));
                Assert.True(SerialLog.App.MainWindow.ShouldEnableInputMethod(input));
                input.IsReadOnly = true;
                Assert.False(SerialLog.App.MainWindow.ShouldEnableInputMethod(input));
            }
            Assert.False(SerialLog.App.MainWindow.ShouldEnableInputMethod(new ListBox()));
            Assert.False(SerialLog.App.MainWindow.ShouldEnableInputMethod(null));
        });
    }

    [Fact]
    public void Serial_window_name_enables_ime_and_updates_chinese_title()
    {
        var editor = ReadWindow().Descendants(Ui + "TextBox")
            .Single(e => (string?)e.Attribute("Text") == "{Binding Title, UpdateSourceTrigger=PropertyChanged}");
        editor.Attribute("Style")!.Remove();
        InSta(() =>
        {
            using var window = new SerialLog.App.ViewModels.SerialWindowViewModel("ime-test", "串口 1");
            var parent = new Grid();
            System.Windows.Input.InputMethod.SetIsInputMethodEnabled(parent, false);
            var style = new Style(typeof(TextBox));
            style.Setters.Add(new Setter(System.Windows.Input.InputMethod.IsInputMethodEnabledProperty, false));
            var input = (TextBox)XamlReader.Parse(editor.ToString());
            input.Style = style;
            input.DataContext = window;
            parent.Children.Add(input);
            input.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.True(System.Windows.Input.InputMethod.GetIsInputMethodEnabled(input));
            Assert.True(SerialLog.App.MainWindow.ShouldEnableInputMethod(input));
            Assert.Equal(window.Title, input.Text);
            input.Text = "网关中文测试";
            input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            Assert.Equal("网关中文测试", window.Title);
            Assert.False(System.Windows.Input.InputMethod.GetIsInputMethodEnabled(parent));
            Assert.False(SerialLog.App.MainWindow.ShouldEnableInputMethod(new ListBox()));
        });
    }

    [Fact]
    public void Delete_confirmation_matches_update_check_style_and_defaults_to_cancel()
    {
        var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/SerialLog.App/Views"));
        var delete = XElement.Load(Path.Combine(directory, "DeleteConfirmationWindow.xaml"));
        var update = XElement.Load(Path.Combine(directory, "UpdateCheckWindow.xaml"));
        foreach (var property in new[] { "Width", "Background", "WindowStyle", "AllowsTransparency", "ShowInTaskbar", "ResizeMode", "WindowStartupLocation" })
            Assert.Equal((string?)update.Attribute(property), (string?)delete.Attribute(property));
        var cancel = Named(delete, "CancelButton");
        Assert.Equal("True", (string?)cancel.Attribute("IsCancel"));
        Assert.Equal("True", (string?)cancel.Attribute("IsDefault"));
        var confirm = Named(delete, "DeleteButton");
        Assert.Null(confirm.Attribute("IsDefault"));
        Assert.Equal("{StaticResource CompactDangerButton}", (string?)confirm.Attribute("Style"));
    }

    public sealed class HeaderItem : INotifyPropertyChanged
    {
        private string _name = "";
        public string DisplayName
        {
            get => _name;
            set { _name = value; PropertyChanged?.Invoke(this, new(nameof(DisplayName))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private static void InSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception exception) { failure = exception; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
