using System.IO;

namespace SerialLog.App.ViewModels;

public static class LogSessionPathFactory
{
    public static string CreateSessionDirectory(string logRootDirectory, DateTimeOffset timestamp)
    {
        var day = timestamp.ToString("yyyy-MM-dd");
        var session = timestamp.ToString("yyyyMMdd_HHmmss_fff");
        return Path.Combine(logRootDirectory, day, session);
    }
}
