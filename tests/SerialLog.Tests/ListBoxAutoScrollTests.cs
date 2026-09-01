using SerialLog.App.Behaviors;

namespace SerialLog.Tests;

public sealed class ListBoxAutoScrollTests
{
    [Theory]
    [InlineData(100, 100, true)]
    [InlineData(99.5, 100, true)]
    [InlineData(99.4, 100, false)]
    [InlineData(0, 0, true)]
    public void Bottom_detection_allows_a_small_scroll_tolerance(
        double verticalOffset,
        double scrollableHeight,
        bool expected)
    {
        Assert.Equal(expected, ListBoxAutoScroll.IsAtBottom(verticalOffset, scrollableHeight));
    }
}
