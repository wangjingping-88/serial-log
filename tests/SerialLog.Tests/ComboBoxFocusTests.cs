using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SerialLog.App;

namespace SerialLog.Tests;

public class ComboBoxFocusTests
{
    [Fact]
    public void Detached_dropdown_item_and_its_content_keep_focus_and_do_not_start_card_drag()
    {
        RunOnSta(() =>
        {
            // 模拟 Popup 边界：选项的可视父链中没有 ComboBox。
            var run = new Run("460800");
            var text = new TextBlock(run);
            var border = new Border { Child = text };
            var item = new ComboBoxItem { Content = border };
            var template = new ControlTemplate(typeof(ComboBoxItem));
            template.VisualTree = new FrameworkElementFactory(typeof(ContentPresenter));
            item.Template = template;
            item.Measure(new Size(100, 30));
            item.Arrange(new Rect(0, 0, 100, 30));
            item.UpdateLayout();
            Assert.NotNull(VisualTreeHelper.GetParent(text));
            foreach (var source in new DependencyObject[] { item, border, text, run })
            {
                Assert.True(MainWindow.IsTextEditingElement(source));
                Assert.True(MainWindow.IsInteractiveElement(source));
            }
        });
    }

    [Fact]
    public void Normal_editors_remain_protected_and_blank_area_can_release_focus()
    {
        RunOnSta(() =>
        {
            Assert.True(MainWindow.IsTextEditingElement(new TextBox()));
            Assert.True(MainWindow.IsTextEditingElement(new PasswordBox()));
            Assert.True(MainWindow.IsTextEditingElement(new ComboBox()));
            Assert.False(MainWindow.IsTextEditingElement(new Border()));
            Assert.False(MainWindow.IsTextEditingElement(null));
            Assert.False(MainWindow.IsInteractiveElement(new Border()));
        });
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { failure = e; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "下拉框焦点测试超时");
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
