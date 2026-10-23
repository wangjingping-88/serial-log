using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using SerialLog.App.ViewModels;
using SerialLog.Core.Configuration;

namespace SerialLog.Tests;

public class AppearanceBaselineTests
{
    private static readonly XNamespace Ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static string Repo => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));

    [Fact]
    public void Render_and_compare_control_and_page_layout_baselines()
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { Run(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(50)), "界面基线渲染超时");
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static void Run()
    {
        var before = Environment.GetEnvironmentVariable("SERIALLOG_APPEARANCE_BASELINE") == "before";
        var directory = Path.Combine(Repo, "artifacts/appearance-20261021"); Directory.CreateDirectory(directory);
        using var vm = new MainViewModel(Path.Combine(directory, "not-saved.json"), false, configuration: new WorkspaceConfig
        {
            SerialWindows = [new() { Id = "ap", Title = "AP 网关", PortName = "COM7" }, new() { Id = "iot", Title = "IOT 终端", PortName = "COM8" }],
            CommandGroups = Enumerable.Range(1, 8).Select(index => new CommandGroupConfig { Name = "设备命令 " + index, Commands = ["AT+STATUS?"], TargetIds = ["ap"] }).ToList()
        });
        var controls = XElement.Parse("""
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Margin="16">
              <StackPanel>
                <TextBlock Text="Serial Log · 控件状态" FontSize="18" Margin="0,0,0,12" />
                <StackPanel Orientation="Horizontal" Margin="0,0,0,12"><Button x:Name="plain" Content="普通按钮"/><Button x:Name="primary" Content="开始烧录" Style="{StaticResource PrimaryButton}"/><Button x:Name="danger" Content="删除" Style="{StaticResource DangerButton}"/><Button x:Name="disabled" Content="不可用" IsEnabled="False"/></StackPanel>
                <StackPanel Orientation="Horizontal" Margin="0,0,0,12"><TextBox x:Name="input" Text="中文工作区" Width="170" InputMethod.IsInputMethodEnabled="True"/><ComboBox x:Name="combo" Width="110"><ComboBoxItem Content="COM7" IsSelected="True"/><ComboBoxItem Content="COM8"/></ComboBox><ComboBox x:Name="editable" IsEditable="True" Text="460800" Width="110"><ComboBoxItem Content="115200"/><ComboBoxItem Content="460800"/></ComboBox></StackPanel>
                <StackPanel Orientation="Horizontal" Margin="0,0,0,12"><CheckBox x:Name="check" Content="AP 网关" IsChecked="True"/><CheckBox x:Name="partial" Content="全选" IsThreeState="True" IsChecked="{x:Null}"/><CheckBox Content="共享"/></StackPanel>
                <TabControl x:Name="tabs" Height="140"><TabItem Header="单条命令"><TextBlock Text="AT+STATUS?" Margin="10"/></TabItem><TabItem Header="命令组（8）"/><TabItem Header="Ymodem"/></TabControl>
                <ProgressBar x:Name="progress" Height="8" Value="65" Margin="4,12,4,0"/>
              </StackPanel>
            </Grid>
            """);
        Capture("controls", controls, vm, 720, 340, before, directory);
        var panel = XElement.Load(Path.Combine(Repo, "src/SerialLog.App/Views/CommandPanelView.xaml"));
        panel.Attribute(X + "Class")!.Remove();
        Capture("command-panel", panel, vm, 1200, 300, before, directory);
        var editor = XElement.Load(Path.Combine(Repo, "src/SerialLog.App/Views/CommandGroupEditorView.xaml"));
        editor.Attribute(X + "Class")!.Remove();
        Capture("settings", editor, vm.CommandPanel, 1288, 720, before, directory);
        var main = XElement.Load(Path.Combine(Repo, "src/SerialLog.App/MainWindow.xaml"));
        main.Attribute(X + "Class")!.Remove();
        main.Attributes().Where(attribute => attribute.Name.LocalName is "Icon" or "Loaded" or "PreviewKeyDown" or "PreviewMouseDown" or "Deactivated").Remove();
        main.Elements().Where(element => element.Name.LocalName.Contains("WindowChrome")).Remove();
        var canvas = new XElement(Ui + "Grid", main.Attributes().Where(attribute => attribute.IsNamespaceDeclaration),
            new XElement(Ui + "Grid.Resources", main.Element(Ui + "Window.Resources")!.Elements()),
            main.Elements().Where(element => element.Name.LocalName != "Window.Resources"));
        Capture("main", canvas, vm, 1500, 950, before, directory);
        var menu = main.Descendants(Ui + "Popup").Single(element => (string?)element.Attribute(X + "Name") == "WorkspaceMenuPopup");
        Capture("menu", new XElement(menu.Elements().Single()), vm, 300, 265, before, directory);
        var dialog = XElement.Load(Path.Combine(Repo, "src/SerialLog.App/Views/DeleteConfirmationWindow.xaml"));
        Capture("confirmation", new XElement(dialog.Elements().Single()), vm, 460, 220, before, directory);
    }

    private static void Capture(string name, XElement source, object model, int width, int height, bool before, string directory)
    {
        source = new XElement(source);
        foreach (var child in source.Descendants().Where(element => element.Name.LocalName == "CommandPanelView").ToArray())
        {
            var panel = XElement.Load(Path.Combine(Repo, "src/SerialLog.App/Views/CommandPanelView.xaml"));
            panel.Attribute(X + "Class")!.Remove(); panel.Name = Ui + "Grid";
            child.ReplaceWith(new XElement(Ui + "Grid", child.Attributes(), panel));
        }
        var events = new HashSet<string> { "Click", "Loaded", "SelectionChanged", "Checked", "Unchecked", "Opened", "Closed", "MouseEnter", "MouseLeave", "PreviewKeyDown", "PreviewMouseDown", "PreviewMouseLeftButtonDown", "PreviewMouseRightButtonDown", "PreviewMouseMove", "MouseLeftButtonDown", "MouseDoubleClick", "Drop", "DragOver", "KeyDown", "SizeChanged", "ScrollChanged", "LostFocus", "PreviewTextInput", "TextChanged", "MouseUp", "PreviewMouseWheel" };
        foreach (var type in new[] { typeof(Control), typeof(TextBox), typeof(ListBox), typeof(ComboBox), typeof(Button), typeof(System.Windows.Controls.Primitives.Popup) })
            events.UnionWith(type.GetEvents().Select(eventInfo => eventInfo.Name));
        source.DescendantsAndSelf().Attributes().Where(attribute => events.Contains(attribute.Name.LocalName)).Remove();
        source.Descendants(Ui + "EventSetter").Remove();
        foreach (var attribute in source.DescendantsAndSelf().Attributes().Where(attribute => attribute.Name.LocalName == "Icon" || attribute.Name.LocalName == "Class").ToArray()) attribute.Remove();
        var app = XElement.Load(Path.Combine(Repo, "src/SerialLog.App/App.xaml"));
        var root = new XElement(Ui + "Grid", new XAttribute(XNamespace.Xmlns + "x", X),
            new XAttribute(XNamespace.Xmlns + "sys", "clr-namespace:System;assembly=mscorlib"),
            new XAttribute(XNamespace.Xmlns + "local", "clr-namespace:SerialLog.App;assembly=SerialLog.App"),
            new XElement(Ui + "Grid.Resources", app.Element(Ui + "Application.Resources")!.Elements()), source);
        // Loose XAML needs assembly-qualified local namespaces, unlike compiled BAML.
        foreach (var element in root.DescendantsAndSelf().Where(element => element.Name.NamespaceName.StartsWith("clr-namespace:SerialLog.App") && !element.Name.NamespaceName.Contains(";assembly=")))
            element.Name = XNamespace.Get(element.Name.NamespaceName + ";assembly=SerialLog.App") + element.Name.LocalName;
        foreach (var attribute in root.DescendantsAndSelf().Attributes().Where(attribute => attribute.IsNamespaceDeclaration && attribute.Value.StartsWith("clr-namespace:SerialLog.App") && !attribute.Value.Contains(";assembly="))) attribute.Value += ";assembly=SerialLog.App";
        foreach (var attribute in root.DescendantsAndSelf().Attributes().Where(attribute => attribute.Name.NamespaceName.StartsWith("clr-namespace:SerialLog.App") && !attribute.Name.NamespaceName.Contains(";assembly=")).ToArray())
        {
            attribute.Parent!.Add(new XAttribute(XNamespace.Get(attribute.Name.NamespaceName + ";assembly=SerialLog.App") + attribute.Name.LocalName, attribute.Value)); attribute.Remove();
        }
        var view = (Grid)XamlReader.Parse(root.ToString()); view.DataContext = model;
        using var host = new HwndSource(new HwndSourceParameters("AppearanceBaseline") { Width = width, Height = height, WindowStyle = unchecked((int)0x80000000) });
        host.CompositionTarget.RenderMode = RenderMode.SoftwareOnly; host.RootVisual = view;
        view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        view.Measure(new Size(width, height)); view.Arrange(new Rect(0, 0, width, height)); view.UpdateLayout();
        var geometry = Descendants(view).OfType<FrameworkElement>().Where(element => !string.IsNullOrEmpty(element.Name) && !element.Name.StartsWith("PART_") && element.IsVisible)
            .Where(element => element is Control || element.Name is "CommandPanelViewport" or "WorkspaceViewport")
            .GroupBy(element => element.Name).Where(group => group.Count() == 1).ToDictionary(group => group.Key, group =>
            { var element = group.Single(); var origin = element.TranslatePoint(new Point(), view); return new[] { origin.X, origin.Y, element.ActualWidth, element.ActualHeight }; });
        var baseline = Path.Combine(directory, name + "-geometry.json");
        var differences = new List<string>();
        if (before) File.WriteAllText(baseline, JsonSerializer.Serialize(geometry));
        else
        {
            File.WriteAllText(Path.Combine(directory, name + "-after-geometry.json"), JsonSerializer.Serialize(geometry));
            var expected = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, double[]>>>(File.ReadAllText(
                Path.Combine(Repo, "tests/SerialLog.Tests/Baselines/appearance-geometry.json")))![name];
            foreach (var (key, value) in expected)
            {
                Assert.True(geometry.ContainsKey(key), $"{name}/{key} 不再可见");
                for (var index = 0; index < 4; index++) if (Math.Abs(value[index] - geometry[key][index]) > 1) differences.Add($"{name}/{key}/{index}: {value[index]} → {geometry[key][index]}");
            }
        }
        foreach (var scale in new[] { 1d, 1.25, 1.5, 2 })
        {
            var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            var backdrop = new DrawingVisual(); using (var drawing = backdrop.RenderOpen()) drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            bitmap.Render(backdrop); bitmap.Render(view);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(directory, $"{name}-{(before ? "before" : "after")}-{scale * 100:0}.png")); encoder.Save(stream);
        }
        Assert.True(differences.Count == 0, string.Join(Environment.NewLine, differences));
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        { var child = VisualTreeHelper.GetChild(root, index); yield return child; foreach (var nested in Descendants(child)) yield return nested; }
    }
}
