using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using SerialLog.App.Infrastructure;

namespace SerialLog.Tests;

public class AppearanceTests
{
    private static string Repo => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static Grid View(string markup)
    {
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var app = XElement.Load(Path.Combine(Repo, "src/SerialLog.App/App.xaml"));
        var root = new XElement(ui + "Grid", new XAttribute(XNamespace.Xmlns + "x", x),
            new XAttribute(XNamespace.Xmlns + "sys", "clr-namespace:System;assembly=mscorlib"),
            new XAttribute(XNamespace.Xmlns + "local", "clr-namespace:SerialLog.App;assembly=SerialLog.App"),
            new XElement(ui + "Grid.Resources", app.Element(ui + "Application.Resources")!.Elements()), XElement.Parse(markup));
        return (Grid)XamlReader.Parse(root.ToString());
    }
    private static HwndSource Host(Grid view)
    {
        var host = new HwndSource(new HwndSourceParameters("AppearanceTest") { Width = 600, Height = 350, PositionX = -20000, PositionY = -20000, WindowStyle = unchecked((int)0x80000000) });
        host.CompositionTarget.RenderMode = RenderMode.SoftwareOnly; host.RootVisual = view;
        view.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        view.Measure(new Size(600, 350)); view.Arrange(new Rect(0, 0, 600, 350)); view.UpdateLayout(); return host;
    }

    [Theory]
    [InlineData("#0B75B7")]
    [InlineData("#FFFF00")]
    [InlineData("#FFFFFF")]
    [InlineData("#000000")]
    [InlineData("#777777")]
    [InlineData("#00FFCC")]
    [InlineData("#0EAD4F")]
    public void Custom_accents_keep_normal_hover_pressed_and_emphasized_text_readable(string value)
    {
        var accent = (Color)ColorConverter.ConvertFromString(value);
        var palette = AppearanceResources.CreatePalette(accent, false);
        Color ColorOf(string key) => ((SolidColorBrush)palette[key]).Color;
        Assert.Equal(accent, ColorOf("AccentBrush"));
        Assert.Equal(Colors.White, ColorOf("OnAccentBrush"));
        foreach (var fill in new[] { "PrimaryBackgroundBrush", "PrimaryButtonHoverBrush", "PrimaryButtonPressedBrush" })
            Assert.True(AppearanceResources.Contrast(ColorOf(fill), ColorOf("PrimaryTextBrush")) >= 4.5);
        foreach (var (fill, amount) in new[] { ("PrimaryBackgroundBrush", .18), ("PrimaryButtonHoverBrush", .26), ("PrimaryButtonPressedBrush", .34) })
            Assert.Equal(Color.FromRgb((byte)Math.Round(255 + (accent.R - 255) * amount),
                (byte)Math.Round(255 + (accent.G - 255) * amount),
                (byte)Math.Round(255 + (accent.B - 255) * amount)), ColorOf(fill));
        Assert.True(AppearanceResources.Contrast(ColorOf("PrimaryBackgroundBrush"), Colors.White) < 1.6);
        foreach (var fill in new[] { "SolidAccentBrush", "PrimaryHoverBrush", "PrimaryPressedBrush" })
            Assert.True(AppearanceResources.Contrast(ColorOf(fill), ColorOf("OnAccentBrush")) >= 4.5);
        Assert.True(AppearanceResources.Contrast(ColorOf("AccentTextBrush"), Colors.White) >= 4.5);
        Assert.True(AppearanceResources.Contrast(ColorOf("MutedBrush"), Colors.White) >= 4.5);
        Assert.All(palette.Values, brush => Assert.True(brush.IsFrozen));
    }

    [Fact]
    public void Window_palette_and_captured_dialog_palette_are_isolated()
    {
        Sta(() =>
        {
            var a = new Grid(); var b = new Grid(); var dialog = new Grid();
            AppearanceResources.Apply(a, "#0B75B7"); AppearanceResources.Apply(b, "#CC5511");
            AppearanceResources.CaptureOwner(dialog, a);
            var captured = dialog.Resources["AccentBrush"];
            AppearanceResources.Apply(a, "#228833");
            Assert.Same(captured, dialog.Resources["AccentBrush"]);
            Assert.Equal((Color)ColorConverter.ConvertFromString("#CC5511"), ((SolidColorBrush)b.Resources["AccentBrush"]).Color);
            Assert.NotSame(a.Resources["AccentBrush"], b.Resources["AccentBrush"]);
            AppearanceResources.Apply(a, "not-a-color");
            Assert.Equal(AppearanceResources.DefaultAccent, ((SolidColorBrush)a.Resources["AccentBrush"]).Color);
        });
    }

    [Fact]
    public void High_contrast_palette_uses_system_surfaces_text_and_selection_colors()
    {
        Sta(() =>
        {
            var palette = AppearanceResources.CreatePalette(Colors.Yellow, true);
            Assert.Equal(SystemColors.WindowColor, ((SolidColorBrush)palette["PanelBrush"]).Color);
            Assert.Equal(SystemColors.WindowTextColor, ((SolidColorBrush)palette["TextBrush"]).Color);
            Assert.Equal(SystemColors.HighlightColor, ((SolidColorBrush)palette["AccentBrush"]).Color);
            Assert.Equal(SystemColors.HighlightColor, ((SolidColorBrush)palette["SolidAccentBrush"]).Color);
            Assert.Equal(SystemColors.HighlightTextColor, ((SolidColorBrush)palette["OnAccentBrush"]).Color);
        });
    }

    [Fact]
    public void Editable_combo_preserves_baud_selection_text_binding_and_distinct_keyboard_tab_stop()
    {
        Sta(() =>
        {
            var view = View("""<ComboBox xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" IsEditable="True" IsTextSearchEnabled="False" Width="100" VerticalAlignment="Top"/>""");
            var combo = (ComboBox)view.Children[0]; combo.ItemsSource = new[] { 115200, 460800, 921600 };
            var model = new TextModel { Text = "115200" };
            combo.SetBinding(ComboBox.TextProperty, new Binding(nameof(TextModel.Text)) { Source = model, Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
            using var host = Host(view);
            Assert.IsType<Popup>(combo.Template.FindName("PART_Popup", combo));
            var editor = Assert.IsType<TextBox>(combo.Template.FindName("PART_EditableTextBox", combo));
            Assert.Equal(Visibility.Visible, editor.Visibility); Assert.True(editor.IsTabStop); Assert.False(combo.IsTabStop);
            combo.SelectedItem = 460800; combo.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.Equal("460800", combo.Text); Assert.Equal("460800", model.Text);
            editor.Text = "250000"; combo.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.Equal("250000", combo.Text); Assert.Equal("250000", model.Text);
            combo.IsReadOnly = true; Assert.True(editor.IsReadOnly);
        });
    }

    [Fact]
    public void Text_input_retains_ime_readonly_validation_and_native_content_host()
    {
        Sta(() =>
        {
            var view = View("""<TextBox xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Text="中文命令" InputMethod.IsInputMethodEnabled="True" Width="200" VerticalAlignment="Top"/>""");
            var box = (TextBox)view.Children[0]; using var host = Host(view);
            Assert.IsType<ScrollViewer>(box.Template.FindName("PART_ContentHost", box));
            Assert.True(InputMethod.GetIsInputMethodEnabled(box)); Assert.Equal("中文命令", box.Text);
            box.IsReadOnly = true; view.UpdateLayout();
            var chrome = (Border)box.Template.FindName("Chrome", box);
            Assert.Same(view.FindResource("PanelAltBrush"), chrome.Background);
            box.IsReadOnly = false;
            box.SetBinding(TextBox.TextProperty, new Binding(nameof(TextModel.Text)) { Source = new TextModel { Text = "错误值" } });
            var binding = box.GetBindingExpression(TextBox.TextProperty)!;
            Validation.MarkInvalid(binding, new ValidationError(new ExceptionValidationRule(), binding, "输入错误", null));
            Assert.Same(view.FindResource("DangerBrush"), chrome.BorderBrush);
        });
    }

    [Fact]
    public void Checkbox_handles_three_states_without_changing_outer_geometry()
    {
        Sta(() =>
        {
            var view = View("""<CheckBox xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Content="全选" IsThreeState="True" HorizontalAlignment="Left" VerticalAlignment="Top"/>""");
            var box = (CheckBox)view.Children[0]; using var host = Host(view); var size = box.RenderSize;
            foreach (var state in new bool?[] { false, true, null, false })
            {
                box.IsChecked = state; view.UpdateLayout(); Assert.Equal(size, box.RenderSize);
                Assert.Equal(state == true ? Visibility.Visible : Visibility.Collapsed, ((FrameworkElement)box.Template.FindName("Check", box)).Visibility);
                Assert.Equal(state is null ? Visibility.Visible : Visibility.Collapsed, ((FrameworkElement)box.Template.FindName("Partial", box)).Visibility);
            }
        });
    }

    [Fact]
    public void State_feedback_never_animates_layout_or_shared_brushes_and_unloads_cleanly()
    {
        Sta(() =>
        {
            var view = View("""<Button xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Content="运行" Width="80" Height="28" HorizontalAlignment="Left" VerticalAlignment="Top"/>""");
            var button = (Button)view.Children[0]; using var host = Host(view); var size = button.RenderSize;
            var accent = (SolidColorBrush)view.FindResource("AccentBrush"); var originalAccent = accent.Color;
            ControlFeedback.Attach(button); ControlFeedback.Attach(button);
            var focus = (Border)button.Template.FindName("FocusLayer", button);
            focus.Opacity = 1; button.IsEnabled = false;
            Assert.Equal(0, focus.Opacity); Assert.False(focus.HasAnimatedProperties); Assert.Equal(size, button.RenderSize);
            button.IsEnabled = true; ControlFeedback.RefreshSystemSettings();
            button.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
            Assert.False(focus.HasAnimatedProperties);
            Assert.Equal(originalAccent, accent.Color); Assert.False(accent.HasAnimatedProperties);
        });
    }

    [Fact]
    public void Menu_templates_keep_shortcut_checkmark_submenu_and_default_cancel_semantics()
    {
        Sta(() =>
        {
            var view = View("""<StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><MenuItem Header="发送回显" IsCheckable="True" IsChecked="True" InputGestureText="Ctrl+E"><MenuItem Header="子菜单"/></MenuItem><Button Content="保存" IsDefault="True"/><Button Content="取消" IsCancel="True"/></StackPanel>""");
            using var host = Host(view); var panel = (StackPanel)view.Children[0]; var menu = (MenuItem)panel.Children[0];
            Assert.Equal(Visibility.Visible, ((FrameworkElement)menu.Template.FindName("Check", menu)).Visibility);
            Assert.IsType<Popup>(menu.Template.FindName("PART_Popup", menu)); Assert.Equal("Ctrl+E", menu.InputGestureText);
            Assert.True(((Button)panel.Children[1]).IsDefault); Assert.True(((Button)panel.Children[2]).IsCancel);
        });
    }

    [Fact]
    public void Progress_uses_actual_value_and_keeps_indeterminate_semantics_without_sweep()
    {
        Sta(() =>
        {
            var view = View("""<ProgressBar xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Value="25" Maximum="100" Width="400" Height="8"/>""");
            using var host = Host(view); var bar = (ProgressBar)view.Children[0];
            var track = (FrameworkElement)bar.Template.FindName("PART_Track", bar); var indicator = (FrameworkElement)bar.Template.FindName("PART_Indicator", bar);
            Assert.Equal(track.ActualWidth * .25, indicator.ActualWidth, 2);
            bar.Value = 75; view.UpdateLayout(); Assert.Equal(track.ActualWidth * .75, indicator.ActualWidth, 2);
            bar.IsIndeterminate = true; view.UpdateLayout(); Assert.True(bar.IsIndeterminate);
            Assert.Null(bar.Template.FindName("PART_GlowRect", bar));
        });
    }

    [Fact]
    public void Rendered_button_labels_inherit_foreground_including_access_keys_and_explicit_text()
    {
        Sta(() =>
        {
            var view = View("""<StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><Button Style="{StaticResource PrimaryButton}" Content="连接"/><Button Style="{StaticResource PrimaryButton}" Content="保存(_S)"/><Button Style="{StaticResource PrimaryButton}"><TextBlock Text="加入命令"/></Button><Button Style="{StaticResource DangerButton}" Content="删除"/></StackPanel>""");
            AppearanceResources.Apply(view, "#0EAD4F");
            using var host = Host(view);
            var primaryButton = ((StackPanel)view.Children[0]).Children.OfType<Button>().First();
            Assert.Equal(((SolidColorBrush)view.Resources["PrimaryBackgroundBrush"]).Color, ((SolidColorBrush)primaryButton.Background).Color);
            Assert.Equal(((SolidColorBrush)view.Resources["PrimaryTextBrush"]).Color, ((SolidColorBrush)primaryButton.Foreground).Color);
            static IEnumerable<TextBlock> Labels(DependencyObject root)
            {
                for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
                {
                    var child = VisualTreeHelper.GetChild(root, i);
                    if (child is TextBlock label) yield return label;
                    foreach (var nested in Labels(child)) yield return nested;
                }
            }
            foreach (var button in ((StackPanel)view.Children[0]).Children.OfType<Button>())
            {
                var labels = Labels(button).ToArray();
                Assert.NotEmpty(labels);
                foreach (var label in labels)
                    Assert.Equal(((SolidColorBrush)button.Foreground).Color, ((SolidColorBrush)label.Foreground).Color);
            }
        });
    }

    [Fact]
    public void About_dialog_uses_themed_content_and_enter_escape_close_button()
    {
        Sta(() =>
        {
            XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
            var source = XElement.Load(Path.Combine(Repo, "src/SerialLog.App/Views/AboutWindow.xaml"));
            var content = new XElement(source.Element(ui + "Grid")!);
            content.Descendants().Attributes("Click").Remove();
            content.Descendants(ui + "TextBlock").Single(e => (string?)e.Attribute(x + "Name") == "VersionText").SetAttributeValue("Text", "版本 v0.4.0");
            var view = View(content.ToString()); AppearanceResources.Apply(view, "#0EAD4F");
            view.Width = 460; view.Height = 220; view.HorizontalAlignment = HorizontalAlignment.Left; view.VerticalAlignment = VerticalAlignment.Top;
            view.SetResourceReference(Panel.BackgroundProperty, "BackgroundBrush");
            using var host = Host(view);
            view.Measure(new Size(460, 220)); view.Arrange(new Rect(0, 0, 460, 220)); view.UpdateLayout();
            var button = (Button)view.FindName("CloseButton");
            Assert.True(button.IsDefault); Assert.True(button.IsCancel);
            Assert.Equal(((SolidColorBrush)view.Resources["PrimaryTextBrush"]).Color, ((SolidColorBrush)button.Foreground).Color);
            var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(460, 220, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
            var directory = Path.Combine(Repo, "artifacts/appearance-20261021"); Directory.CreateDirectory(directory);
            using var file = File.Create(Path.Combine(directory, "about-after-100.png")); encoder.Save(file);
        });
    }

    private sealed class TextModel { public string Text { get; set; } = ""; }
    [Fact]
    public void Startup_hooks_apply_to_loaded_controls_and_owned_window_resources()
    {
        Sta(() =>
        {
            AppearanceResources.Install(); AppearanceResources.Install();
            var view = View("""<StackPanel xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><Button Content="保存"/><ComboBox IsEditable="True" Text="460800"/><CheckBox Content="共享"/></StackPanel>""");
            using var host = Host(view);
            var button = (Button)((StackPanel)view.Children[0]).Children[0];
            button.IsEnabled = false;
            Assert.Equal(0, ((Border)button.Template.FindName("FocusLayer", button)).Opacity);
            var window = new Window { Resources = View("""<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"/>""").Resources };
            window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            Assert.True(window.Resources.Contains("OnAccentBrush")); window.Close();
        });
    }
    private static void Sta(Action action)
    {
        Exception? failure = null; var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "外观交互测试超时");
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
