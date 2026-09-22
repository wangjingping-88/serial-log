using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SerialLog.App.Controls;
using SerialLog.App.ViewModels;

namespace SerialLog.Tests;

public class SerialWindowGridPanelTests
{
    [Fact]
    public void Slot_changes_relayout_existing_children_without_recreating_log_controls()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var a = new SerialWindowViewModel("a", "A");
                using var b = new SerialWindowViewModel("b", "B");
                var slot = new SerialWindowSlotViewModel(a);
                var otherSlot = new SerialWindowSlotViewModel(b, pagePosition: 1);
                var first = new ListBox { DataContext = slot };
                var other = new ListBox { DataContext = otherSlot, ItemsSource = new[] { "原有日志", "选中日志" }, SelectedIndex = 1 };
                first.SetBinding(SerialWindowGridPanel.LayoutRevisionProperty, new Binding(nameof(SerialWindowSlotViewModel.LayoutRevision)));
                var panel = new SerialWindowGridPanel { Rows = 2, Columns = 3 };
                panel.Children.Add(first);
                panel.Children.Add(other);
                panel.Measure(new Size(600, 400));
                panel.Arrange(new Rect(0, 0, 600, 400));
                panel.UpdateLayout();
                Assert.Equal(200, first.ActualHeight);
                slot.UpdateLayout(new(a, gridRowSpan: 2, isExpanded: true));
                Assert.False(panel.IsMeasureValid);
                panel.UpdateLayout();
                Assert.Equal(400, first.ActualHeight);
                Assert.Same(other, panel.Children[1]);
                Assert.Equal(200, other.ActualHeight);
                Assert.Equal(1, other.SelectedIndex);
                slot.UpdateLayout(new(a));
                panel.UpdateLayout();
                Assert.Equal(200, first.ActualHeight);
                Assert.Equal(1, other.SelectedIndex);
            }
            catch (Exception e) { failure = e; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "布局测试超时");
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
