using SerialLog.App.ViewModels;

namespace SerialLog.Tests;

public class LogSessionPathFactoryTests
{
    [Fact]
    public void Creates_new_connection_session_directory_under_day_folder()
    {
        var timestamp = new DateTimeOffset(2026, 6, 30, 10, 52, 31, 456, TimeSpan.FromHours(8));

        var path = LogSessionPathFactory.CreateSessionDirectory(@"D:\serial-log-data\logs", timestamp);

        Assert.Equal(@"D:\serial-log-data\logs\2026-06-30\20260630_105231_456", path);
    }
}
