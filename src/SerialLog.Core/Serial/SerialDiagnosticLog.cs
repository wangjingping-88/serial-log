using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using SerialLog.Core.Configuration;

namespace SerialLog.Core.Serial;

internal static class SerialDiagnosticLog
{
    private static readonly object Gate = new();
    private const long MaxFileBytes = 2 * 1024 * 1024;
    internal const long MaxDirectoryBytes = 32 * 1024 * 1024;
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    internal static bool DetailedReceiveEnabled { get; } = Environment.GetEnvironmentVariable("SERIALLOG_TRACE_RECEIVE") == "1";
    private static readonly Channel<string> Pending = Channel.CreateBounded<string>(new BoundedChannelOptions(256)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private static long _dropped;
    // 文件维护只在后台执行，接收/发送路径永不等待磁盘或目录枚举。
    private static readonly Task Writer = Task.Run(async () =>
    {
        await foreach (var message in Pending.Reader.ReadAllAsync())
        {
            var lost = Interlocked.Exchange(ref _dropped, 0);
            Write(Path.Combine(ApplicationDataPaths.DataDirectory, "diagnostics"),
                lost == 0 ? message : $"诊断队列繁忙，已跳过 {lost} 条；{message}");
        }
    });
    static SerialDiagnosticLog()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            Pending.Writer.TryComplete();
            try { Writer.Wait(TimeSpan.FromMilliseconds(500)); } catch { }
        };
    }
    internal static void Write(string message)
    {
        // 在来源线程生成时间与线程号，不能用异步落盘时刻代替故障时刻。
        var entry = $"sourceTime={DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}; sourceThread={Environment.CurrentManagedThreadId}; {Bound(message)}";
        if (!Pending.Writer.TryWrite(entry)) Interlocked.Increment(ref _dropped);
    }
    private static string Bound(string message) => message.Length > 8192 ? message[..8192] : message;

    internal static void Write(string directory, string message)
    {
        try
        {
            lock (Gate)
            {
                var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    Encoding.UTF8.GetBytes(Path.GetFullPath(directory).ToUpperInvariant())));
                using var mutex = new Mutex(false, "SerialLogDiagnostics-" + key);
                var acquired = false;
                try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) return;
                try
                {
                    Directory.CreateDirectory(directory);
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
                    // 每个进程最多三份、每份约 2 MiB；同时施加整个目录的诊断文件上限。
                    var file = Path.Combine(directory, $"serial-{Environment.ProcessId}.log");
                    foreach (var target in new[] { file, file + ".1", file + ".2" })
                        if (File.Exists(target) && (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) return;
                    if (File.Exists(file) && new FileInfo(file).Length >= MaxFileBytes)
                    {
                        if (File.Exists(file + ".1")) File.Move(file + ".1", file + ".2", true);
                        File.Move(file, file + ".1", true);
                    }
                    var entry = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] pid={Environment.ProcessId} thread={Environment.CurrentManagedThreadId} {Bound(message)}{Environment.NewLine}";
                    if (Prune(directory, DateTime.UtcNow, MaxDirectoryBytes - Encoding.UTF8.GetByteCount(entry) - 3, file))
                        File.AppendAllText(file, entry, Encoding.UTF8);
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
        catch { /* 诊断写入失败不能影响串口和退出流程。 */ }
    }

    // 只管理本工具创建的 serial-数字.log/.1/.2，不递归，不触碰其他 data 文件。
    internal static bool Prune(string directory, DateTime nowUtc, long limit, string? currentFile = null)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return false;
        var files = new DirectoryInfo(directory).EnumerateFiles().Where(f =>
            Regex.IsMatch(f.Name, @"^serial-\d+\.log(?:\.[12])?$", RegexOptions.CultureInvariant) &&
            (f.Attributes & FileAttributes.ReparsePoint) == 0).OrderBy(f => f.LastWriteTimeUtc).ToList();
        foreach (var file in files.ToArray())
        {
            if (nowUtc - file.LastWriteTimeUtc > Retention)
                try { file.Delete(); files.Remove(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        long total = files.Sum(f => f.Length);
        foreach (var file in files)
        {
            if (total <= limit) break;
            if (file.FullName.Equals(currentFile, StringComparison.OrdinalIgnoreCase)) continue;
            try { var size = file.Length; file.Delete(); total -= size; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        // 旧文件被锁定时停止写入，而非突破总量限制。
        return total <= limit;
    }
}
