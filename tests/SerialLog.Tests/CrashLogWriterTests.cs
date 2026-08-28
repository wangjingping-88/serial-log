using SerialLog.App.Diagnostics;

namespace SerialLog.Tests;

public sealed class CrashLogWriterTests
{
    [Fact]
    public void Write_creates_traceable_crash_log()
    {
        var directory = Path.Combine(Path.GetTempPath(), "serial-log-crash-" + Guid.NewGuid().ToString("N"));

        try
        {
            CrashLogWriter.Write(directory, "测试来源", new InvalidOperationException("测试异常"));

            var logFile = Assert.Single(Directory.GetFiles(directory, "crash-*.log"));
            var content = File.ReadAllText(logFile);
            Assert.Contains("来源：测试来源", content);
            Assert.Contains("System.InvalidOperationException: 测试异常", content);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
