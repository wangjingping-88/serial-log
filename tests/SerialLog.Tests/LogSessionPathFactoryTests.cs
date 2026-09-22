using SerialLog.App.ViewModels;

namespace SerialLog.Tests;

public class LogSessionPathFactoryTests
{
    [Theory]
    [InlineData("网关稳定性测试", "网关稳定性测试")]
    [InlineData("测试/A:B", "测试_A_B")]
    [InlineData("..", "默认测试")]
    [InlineData("测试. ", "测试")]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("com1", "_com1")]
    public void Workspace_directory_uses_readable_safe_name(string name, string expected)
    {
        Assert.Equal(expected, LogSessionPathFactory.GetWorkspaceDirectoryName(name));
    }

    [Fact]
    public void Creates_new_connection_session_directory_under_day_folder()
    {
        var timestamp = new DateTimeOffset(2026, 6, 30, 10, 52, 31, 456, TimeSpan.FromHours(8));

        var path = LogSessionPathFactory.CreateSessionDirectory(@"D:\serial-log-data\logs", timestamp);

        Assert.Equal(@"D:\serial-log-data\logs\2026-06-30\20260630_105231_456", path);
    }
}
