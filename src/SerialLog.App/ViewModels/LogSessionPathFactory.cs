using System.IO;

namespace SerialLog.App.ViewModels;

public static class LogSessionPathFactory
{
    internal static string GetWorkspaceDirectoryName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safeName = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "默认测试";
        // Windows 设备名即使带扩展名也不能用作目录名。
        var stem = safeName.Split('.')[0].TrimEnd();
        if (new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(stem, StringComparer.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(stem, @"^(COM|LPT)[1-9¹²³]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            safeName = "_" + safeName;
        return safeName;
    }

    public static string CreateSessionDirectory(string logRootDirectory, DateTimeOffset timestamp)
    {
        var day = timestamp.ToString("yyyy-MM-dd");
        var session = timestamp.ToString("yyyyMMdd_HHmmss_fff");
        return Path.Combine(logRootDirectory, day, session);
    }
}
