using SerialLog.Core.Serial;

namespace SerialLog.Tests;

public class SerialReceivePumpTests
{
    [Fact]
    public void Signal_is_nonblocking_and_reads_on_one_non_pool_thread()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var next = new ManualResetEventSlim();
        var calls = 0; var poolThread = true; var threadIds = new HashSet<int>();
        using var pump = new SerialReceivePump(() =>
        {
            threadIds.Add(Environment.CurrentManagedThreadId);
            poolThread = Thread.CurrentThread.IsThreadPoolThread;
            if (Interlocked.Increment(ref calls) == 1) { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); }
            else next.Set();
        }, TimeSpan.FromMinutes(1), _ => { });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            // 工作线程被模拟驱动卡住时，事件回调只唤醒，不在调用线程中读取或等待。
            for (var i = 0; i < 100; i++) pump.Signal();
            Assert.Equal(1, calls);
            release.Set();
            Assert.True(next.Wait(TimeSpan.FromSeconds(3)));
        }
        finally { release.Set(); pump.Dispose(); Assert.True(pump.WaitForExit(TimeSpan.FromSeconds(3))); }
        Assert.False(poolThread);
        Assert.Single(threadIds);
        Assert.Equal(2, calls); // 多个事件合并，不堆积 100 个读取任务。
        pump.Signal(); // 关闭后迟到的事件安全忽略。
    }

    [Fact]
    public void Missed_event_is_polled_and_dispose_stops_future_reads()
    {
        using var received = new CountdownEvent(2);
        var calls = 0;
        using var pump = new SerialReceivePump(() =>
        {
            if (Interlocked.Increment(ref calls) <= 2) received.Signal();
        }, TimeSpan.FromMilliseconds(20), _ => { });
        Assert.True(received.Wait(TimeSpan.FromSeconds(3)));
        pump.Dispose(); Assert.True(pump.WaitForExit(TimeSpan.FromSeconds(3)));
        var stoppedCalls = calls;
        pump.Signal();
        Assert.Equal(stoppedCalls, calls);
    }

    [Fact]
    public void Diagnostic_retention_limits_global_size_and_preserves_unrelated_files()
    {
        var directory = Path.Combine(Path.GetTempPath(), "serial-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var now = DateTime.UtcNow;
            var stale = Path.Combine(directory, "serial-111.log.1");
            var older = Path.Combine(directory, "serial-222.log");
            var recent = Path.Combine(directory, "serial-333.log");
            var other = Path.Combine(directory, "workspace.json");
            foreach (var file in new[] { stale, older, recent, other }) File.WriteAllText(file, new string('a', 100));
            File.SetLastWriteTimeUtc(stale, now.AddDays(-8)); File.SetLastWriteTimeUtc(older, now.AddDays(-2));
            Assert.True(SerialDiagnosticLog.Prune(directory, now, 150, recent));
            Assert.False(File.Exists(stale)); Assert.False(File.Exists(older));
            Assert.True(File.Exists(recent)); Assert.True(File.Exists(other));
            Assert.False(SerialDiagnosticLog.Prune(directory, now, 50, recent));
            Assert.True(File.Exists(recent));
            File.SetLastWriteTimeUtc(recent, now.AddDays(-8)); // PID 复用也不能永久保留过期文件。
            Assert.True(SerialDiagnosticLog.Prune(directory, now, 50, recent));
            Assert.False(File.Exists(recent));
        }
        finally { Directory.Delete(directory, true); }
    }
}
