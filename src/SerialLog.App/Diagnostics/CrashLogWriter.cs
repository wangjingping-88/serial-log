using System.IO;
using System.Reflection;
using System.Text;
using SerialLog.Core.Configuration;

namespace SerialLog.App.Diagnostics;

internal static class CrashLogWriter
{
    private static readonly object SyncRoot = new();

    public static void Write(string source, Exception exception)
    {
        Write(ApplicationDataPaths.CrashLogDirectory, source, exception);
    }

    internal static void Write(string directory, string source, Exception exception)
    {
        try
        {
            lock (SyncRoot)
            {
                Directory.CreateDirectory(directory);
                var logPath = Path.Combine(directory, $"crash-{DateTime.Now:yyyyMMdd}.log");
                var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "未知";
                var message = new StringBuilder()
                    .AppendLine("============================================================")
                    .AppendLine($"时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}")
                    .AppendLine($"来源：{source}")
                    .AppendLine($"版本：{version}")
                    .AppendLine($"系统：{Environment.OSVersion}")
                    .AppendLine($"运行目录：{AppContext.BaseDirectory}")
                    .AppendLine(exception.ToString())
                    .AppendLine()
                    .ToString();
                File.AppendAllText(logPath, message, Encoding.UTF8);
            }
        }
        catch
        {
            // 崩溃记录不能再次影响主程序或覆盖原始异常。
        }
    }
}
