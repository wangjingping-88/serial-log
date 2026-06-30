using SerialLog.App.ViewModels;

namespace SerialLog.Tests;

public class ConnectionStatusTextTests
{
    [Fact]
    public void Failure_text_for_busy_port_is_short_and_actionable()
    {
        var text = ConnectionStatusText.FromException("COM13", new UnauthorizedAccessException("Access to the path 'COM13' is denied."));

        Assert.Equal("端口 COM13 被占用", text);
    }
}
