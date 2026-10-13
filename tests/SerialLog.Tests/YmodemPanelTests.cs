using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using SerialLog.App.ViewModels;
using SerialLog.App.Views;
using SerialLog.Core.Configuration;
using SerialLog.Core.Firmware;

namespace SerialLog.Tests;

public class YmodemPanelTests
{
    [Fact]
    public void Firmware_panel_renders_with_application_styles_and_resolves_target_and_mode_bindings()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
                var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
                var app = XElement.Load(Path.Combine(repo, "src/SerialLog.App/App.xaml"));
                var panel = XElement.Load(Path.Combine(repo, "src/SerialLog.App/Views/CommandPanelView.xaml"));
                var root = new XElement(ui + "Grid", new XAttribute(XNamespace.Xmlns + "x", x),
                    new XAttribute(XNamespace.Xmlns + "sys", "clr-namespace:System;assembly=mscorlib"),
                    new XAttribute(XNamespace.Xmlns + "local", "clr-namespace:SerialLog.App;assembly=SerialLog.App"),
                    new XElement(ui + "Grid.Resources", app.Element(ui + "Application.Resources")!.Elements()),
                    new XElement(ui + "TabControl", new XElement(panel.Descendants(ui + "TabItem").Single(t => (string?)t.Attribute("Header") == "Ymodem"))));
                var view = (Grid)XamlReader.Parse(root.ToString());
                using var vm = new MainViewModel(Path.Combine(repo, "artifacts/not-saved-ui.json"), false,
                    configuration: new WorkspaceConfig { SerialWindows = [new() { Id = "node", Title = "测试设备", PortName = "COM7", BaudRate = 460800 }] });
                vm.Ymodem.FilePath = "D:\\测试固件\\firmware.bin";
                var row = new YmodemTargetProgress(vm.SerialWindows[0], 1986392, default) { IsRunning = true };
                row.Report(new("传输固件", 1048576, 1986392, 24200, TimeSpan.FromSeconds(45)));
                vm.Ymodem.TargetProgress.Add(row);
                view.DataContext = vm;
                view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var selectedProfileId = vm.Ymodem.SelectedProfileId;
                foreach (var width in new[] { 950, 460 })
                {
                    foreach (var mode in new[] { YmodemMode.Standard, YmodemMode.Custom, YmodemMode.Standard })
                    {
                        vm.Ymodem.Mode = mode;
                        view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        view.Measure(new Size(width, 290)); view.Arrange(new Rect(0, 0, width, 290)); view.UpdateLayout();
                        var modeCombo = Descendants<ComboBox>(view).Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == "Ymodem 模式");
                        Assert.Equal((int)mode, modeCombo.SelectedIndex);
                        var ruleOptions = Descendants<WrapPanel>(view).Single(p => p.Name == "YmodemRuleOptions");
                        Assert.Equal(mode == YmodemMode.Custom ? Visibility.Visible : Visibility.Collapsed, ruleOptions.Visibility);
                        Assert.Equal("规则方案", Assert.Single(ruleOptions.Children.OfType<TextBlock>()).Text);
                        Assert.Equal("规则设置", Assert.Single(ruleOptions.Children.OfType<Button>()).Content);
                        var profileCombo = Assert.Single(ruleOptions.Children.OfType<ComboBox>());
                        Assert.Same(vm.Ymodem.SelectedProfile, profileCombo.SelectedItem);
                        Assert.NotNull(profileCombo.ItemTemplate);
                        Assert.Equal(selectedProfileId, vm.Ymodem.SelectedProfileId);
                        if (mode == YmodemMode.Custom)
                            Assert.True(profileCombo.ActualWidth > 100);
                        else
                            Assert.Equal(new Size(), ruleOptions.DesiredSize);
                        Assert.Single(Descendants<ProgressBar>(view));
                        var bitmap = new RenderTargetBitmap(width, 290, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(view);
                        var directory = Path.Combine(repo, "artifacts/ymodem-render"); Directory.CreateDirectory(directory);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var output = File.Create(Path.Combine(directory, $"ymodem-{width}-{mode}.png")); encoder.Save(output);
                    }
                }
            }
            catch (Exception e) { failure = e; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(25)), "Ymodem 界面渲染超时");
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    [Fact]
    public void Visual_rule_editor_renders_steps_and_frame_fields_with_application_resources()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
                XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
                XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
                var app = XElement.Load(Path.Combine(repo, "src/SerialLog.App/App.xaml"));
                var resourceRoot = new XElement(ui + "Grid", new XAttribute(XNamespace.Xmlns + "x", x),
                    new XAttribute(XNamespace.Xmlns + "sys", "clr-namespace:System;assembly=mscorlib"),
                    new XAttribute(XNamespace.Xmlns + "local", "clr-namespace:SerialLog.App;assembly=SerialLog.App"),
                    new XElement(ui + "Grid.Resources", app.Element(ui + "Application.Resources")!.Elements()));
                var resources = ((Grid)XamlReader.Parse(resourceRoot.ToString())).Resources;
                var profile = YmodemRuleProfiles.CreateUcchipExample();
                var window = new YmodemRulesWindow([profile], profile.Id) { Resources = resources };
                var content = (FrameworkElement)window.Content;
                foreach (var width in new[] { 1000, 720 })
                {
                    foreach (var tabIndex in new[] { 0, 1, 2 })
                    {
                        content.Measure(new Size(width, 650)); content.Arrange(new Rect(0, 0, width, 650)); content.UpdateLayout();
                        var tabs = Assert.Single(Descendants<TabControl>(content)); tabs.SelectedIndex = tabIndex;
                        content.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                        content.Measure(new Size(width, 650)); content.Arrange(new Rect(0, 0, width, 650)); content.UpdateLayout();
                        Assert.NotEmpty(Descendants<TextBox>(content));
                        Assert.Contains(Descendants<Button>(content), b => Equals(b.Content, "保存规则"));
                        var deletes = Descendants<Button>(content).Where(b => b.Content is string text && text.StartsWith("删除")).ToArray();
                        Assert.NotEmpty(deletes);
                        Assert.All(deletes, b => Assert.Equal(28, b.ActualHeight, 2));
                        foreach (var delete in deletes.Where(b => b.Parent is WrapPanel row && Descendants<TextBox>(row).Any()))
                        {
                            var row = (WrapPanel)delete.Parent;
                            var input = Descendants<TextBox>(row).First();
                            Assert.Equal(input.TranslatePoint(new Point(), content).Y + input.ActualHeight,
                                delete.TranslatePoint(new Point(), content).Y + delete.ActualHeight, 2);
                        }
                        if (tabIndex is 0 or 1)
                        {
                            var toolbar = Descendants<Grid>(content).Single(g => g.Children.OfType<Button>().Select(b => b.Content?.ToString()).SequenceEqual(new[] { "新增", "删除", "上移", "下移" }));
                            var toolbarButtons = toolbar.Children.OfType<Button>().ToArray();
                            Assert.Equal(7, toolbar.ColumnDefinitions.Count);
                            Assert.All(toolbarButtons, b => Assert.InRange(b.ActualWidth, 40, 60));
                            Assert.All(toolbarButtons, b => Assert.Equal(toolbarButtons[0].TranslatePoint(new Point(), content).Y, b.TranslatePoint(new Point(), content).Y, 2));
                        }
                        var bitmap = new RenderTargetBitmap(width, 650, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
                        var directory = Path.Combine(repo, "artifacts/ymodem-render"); Directory.CreateDirectory(directory);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using (var output = File.Create(Path.Combine(directory, $"rules-{width}-{tabIndex}.png"))) encoder.Save(output);
                        if (tabIndex == 0)
                        {
                            var editorScroll = Descendants<ScrollViewer>(content).Single(s => s.Content is StackPanel);
                            editorScroll.ScrollToVerticalOffset(300); content.UpdateLayout();
                            var detailBitmap = new RenderTargetBitmap(width, 650, 96, 96, PixelFormats.Pbgra32); detailBitmap.Render(content);
                            var detailEncoder = new PngBitmapEncoder(); detailEncoder.Frames.Add(BitmapFrame.Create(detailBitmap));
                            using (var output = File.Create(Path.Combine(directory, $"rules-{width}-integers.png"))) detailEncoder.Save(output);
                            editorScroll.ScrollToTop(); content.UpdateLayout();
                        }
                    }
                }
                window.Close();
                Assert.Equal("UCCHIP-小网关", profile.Name); // 设置窗口编辑副本。
            }
            catch (Exception e) { failure = e; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(25)), "规则编辑器渲染超时");
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
