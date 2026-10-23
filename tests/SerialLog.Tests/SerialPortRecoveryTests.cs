using System.IO.Ports;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SerialLog.Core.Serial;

namespace SerialLog.Tests;

public class SerialPortRecoveryTests
{
    private static SerialNativeSnapshot Healthy(uint count = 0) => new(true, false, false, true, true, 0, 0, count, 0, 460800, "模拟有效原句柄");

    [Fact]
    public void Event_loop_exit_keeps_original_stream_and_switches_once_without_replaying_writes()
    {
        using var stream = new TestStream("日志一\r\n");
        var diagnostics = new List<string>();
        using var runtime = new SerialPortRuntime(stream, () => false, () => throw new Exception("不可使用 SerialPort 队列 API"),
            _ => Healthy((uint)stream.Pending), diagnostics.Add);
        Assert.Equal(Encoding.UTF8.GetByteCount("日志一\r\n"), runtime.BytesToRead());
        var buffer = new byte[100];
        var length = runtime.Read(buffer, runtime.BytesToRead());
        Assert.Equal("日志一\r\n", Encoding.UTF8.GetString(buffer, 0, length));
        runtime.Write(Encoding.UTF8.GetBytes("AT\r\n"));
        Assert.Equal("AT\r\n", Encoding.UTF8.GetString(stream.Written.ToArray()));
        Assert.Equal(1, stream.WriteCalls);
        Assert.True(runtime.IsPolling);
        Assert.Single(diagnostics);
        Assert.Single(runtime.TakeNotices());
        Assert.Empty(runtime.TakeNotices());
    }

    [Theory]
    [InlineData(false, false, false, true, true)] // 不支持的运行库
    [InlineData(true, true, false, true, true)]  // 已关闭
    [InlineData(true, false, true, true, true)]  // 无效句柄
    [InlineData(true, false, false, false, true)] // 不是事件线程退出
    [InlineData(true, false, false, true, false)] // 驱动访问失败
    public void Unsafe_or_unproven_handle_never_enters_polling(bool supported, bool closed, bool invalid, bool stopped, bool success)
    {
        using var stream = new TestStream("");
        using var runtime = new SerialPortRuntime(stream, () => false, () => 0,
            _ => new(supported, closed, invalid, stopped, success, 31, 0, 0, 0, 0, "失败探测"), _ => { });
        Assert.Throws<IOException>(() => runtime.BytesToRead());
        Assert.Throws<IOException>(() => runtime.Write([1]));
        Assert.False(runtime.IsPolling);
        Assert.Equal(0, stream.WriteCalls);
    }

    [Fact]
    public void Unknown_event_loop_state_is_not_treated_as_recoverable()
    {
        Assert.False((Healthy() with { EventLoopStopped = null }).CanPoll);
        using var stream = new MemoryStream();
        Assert.False(SerialNativeProbe.Capture(stream, true).CanPoll);
        Assert.Equal(28, Marshal.SizeOf<SerialNativeProbe.Dcb>());
        Assert.Equal(12, Marshal.SizeOf<SerialNativeProbe.ComStat>());
        var streamType = typeof(SerialPort).Assembly.GetType("System.IO.Ports.SerialStream", true)!;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        Assert.NotNull(streamType.GetField("_handle", flags));
        var runner = streamType.GetField("_eventRunner", flags);
        Assert.NotNull(runner);
        Assert.NotNull(runner.FieldType.GetField("endEventLoop", flags));
    }

    [Fact]
    public void Normal_connection_does_not_probe_native_handle_and_queue_exit_race_recovers()
    {
        using var stream = new TestStream("");
        var open = true;
        var probeCalls = 0;
        using var runtime = new SerialPortRuntime(stream, () => open,
            () => { open = false; throw new InvalidOperationException("刚退出"); },
            _ => { probeCalls++; return Healthy(); }, _ => { });
        runtime.Write([1]); Assert.Equal(0, probeCalls);
        Assert.Equal(0, runtime.BytesToRead()); Assert.True(runtime.IsPolling);
        Assert.Equal(1, probeCalls);
    }

    [Fact]
    public async Task Session_polling_preserves_connection_parser_and_binary_exclusivity_then_faults_real_removal()
    {
        using var stream = new TestStream("第一");
        var valid = true;
        using var session = Session(stream, _ => valid ? Healthy((uint)stream.Pending) : Healthy() with { ProbeSucceeded = false, Error = 1167 });
        var lines = new List<string>();
        session.LinesReceived += (_, batch) => lines.AddRange(batch.Select(l => l.Text));
        Probe(session);
        Assert.True(session.IsConnected);
        Assert.Contains(lines, l => l.Contains("已切换轮询接收"));
        stream.Enqueue("行\r\n"); Probe(session);
        Assert.Contains("第一行", lines);
        await session.SendAsync("AT\r\n", default);
        Assert.Equal("AT\r\n", Encoding.UTF8.GetString(stream.Written.ToArray()));
        Assert.Throws<InvalidOperationException>(() => session.ChangeBaudRate(115200));
        await using var lease = await session.AcquireBinaryLeaseAsync(default);
        stream.Enqueue("C"); Probe(session);
        var one = new byte[1]; Assert.Equal(1, await lease.ReadAsync(one, default)); Assert.Equal(0x43, one[0]);
        Assert.DoesNotContain("C", lines);
        var waiting = lease.ReadAsync(one, default).AsTask();
        valid = false; Probe(session);
        Assert.False(session.IsConnected);
        var failure = await Assert.ThrowsAsync<System.Threading.Channels.ChannelClosedException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.IsType<IOException>(failure.InnerException);
        await session.WaitForDriverReleaseAsync();
        Assert.Equal(1, stream.DisposeCount);
    }

    [Fact]
    public void Log_subscriber_failure_does_not_disconnect_or_block_other_subscribers()
    {
        using var stream = new TestStream("正常输出\r\n");
        using var session = Session(stream, _ => Healthy((uint)stream.Pending));
        var received = new List<string>();
        session.LinesReceived += (_, _) => throw new InvalidOperationException("模拟界面/保存回调异常");
        session.LinesReceived += (_, lines) => received.AddRange(lines.Select(l => l.Text));
        Probe(session);
        Assert.True(session.IsConnected);
        Assert.Contains("正常输出", received);
    }

    [Fact]
    public async Task Explicit_close_disposes_stream_even_when_serial_port_is_open_is_false()
    {
        using var stream = new TestStream("");
        using var session = Session(stream, _ => Healthy());
        session.Close(); await session.WaitForDriverReleaseAsync();
        Assert.False(session.IsConnected);
        Assert.Equal(1, stream.DisposeCount);
        session.Close(); Assert.Equal(1, stream.DisposeCount);
    }

    [Fact]
    public void Independent_windows_do_not_share_polling_or_bytes()
    {
        using var a = new TestStream("A\r\n"); using var b = new TestStream("B\r\n");
        using var first = Session(a, _ => Healthy((uint)a.Pending));
        using var second = Session(b, _ => Healthy((uint)b.Pending) with { ProbeSucceeded = false });
        Probe(first); Probe(second);
        Assert.True(first.IsConnected); Assert.False(second.IsConnected);
        Assert.Equal(0, a.Pending); Assert.Equal(3, b.Pending);
    }

    [Fact]
    public void Diagnostic_writer_creates_files_without_log_autosave_and_tolerates_unwritable_path()
    {
        var directory = Path.Combine(Path.GetTempPath(), "serial-diagnostic-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            SerialDiagnosticLog.Write(directory, "会话失效 win32=31");
            var file = Assert.Single(Directory.GetFiles(directory));
            Assert.Contains("会话失效 win32=31", File.ReadAllText(file));
            SerialDiagnosticLog.Write(file, "不能把文件当目录");
            Assert.Single(Directory.GetFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Failed_baud_rate_change_restores_display_without_reopening_port()
    {
        using var window = new SerialLog.App.ViewModels.SerialWindowViewModel("baud-test", "波特率测试", refreshPortsOnCreate: false);
        var session = (SerialPortSession)typeof(SerialLog.App.ViewModels.SerialWindowViewModel)
            .GetField("_session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        using var stream = new TestStream("");
        Attach(session, stream, _ => Healthy());
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        window.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(window.StatusText) && window.StatusText.StartsWith("更新波特率失败")) completed.TrySetResult();
        };
        window.BaudRate = 460800;
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(115200, window.BaudRate);
        Assert.Equal("115200", window.BaudRateText);
        Assert.True(window.IsConnected);
        Assert.Equal(0, stream.WriteCalls);
    }

    [Fact]
    public async Task Write_failure_faults_session_without_automatically_replaying_command()
    {
        using var stream = new TestStream("") { FailWrite = true };
        using var session = Session(stream, _ => Healthy());
        await Assert.ThrowsAsync<IOException>(() => session.SendAsync("有副作用的命令", default));
        Assert.False(session.IsConnected);
        Assert.Equal(1, stream.WriteCalls);
        await session.WaitForDriverReleaseAsync();
        Assert.Equal(1, stream.DisposeCount);
    }

    private static SerialPortSession Session(TestStream stream, Func<bool, SerialNativeSnapshot> probe)
    {
        var session = new SerialPortSession("模拟窗口", receiveHealthCheckInterval: TimeSpan.FromMinutes(1));
        Attach(session, stream, probe);
        return session;
    }

    [Fact]
    public void First_complete_lines_publish_with_verification_before_next_read()
    {
        using var stream = new TestStream("第一行\r\n第二行\r\n") { MaxRead = Encoding.UTF8.GetByteCount("第一行\r\n") };
        using var session = Session(stream, _ => Healthy((uint)stream.Pending));
        var batches = new List<IReadOnlyList<SerialLog.Core.Logging.ReceivedLogLine>>();
        session.LinesReceived += (_, lines) => batches.Add(lines);
        Probe(session);
        var batch = Assert.Single(batches.Where(b => b.Any(l => l.Text == "第一行")));
        Assert.Contains(batch, l => l.Text.Contains("接收已验证"));
        Assert.Equal(batch[0].Timestamp, batch[^1].Timestamp);
        Assert.Equal(1, stream.ReadCalls);
        Assert.True(stream.Pending > 0);
        Probe(session);
        Assert.Contains(batches.SelectMany(b => b), l => l.Text == "第二行");
    }

    private static void Attach(SerialPortSession session, TestStream stream, Func<bool, SerialNativeSnapshot> probe)
    {
        var port = new SerialPort("COM16");
        Field("_serialPort").SetValue(session, port); Field("_isConnected").SetValue(session, 1);
        ((ConditionalWeakTable<SerialPort, SerialPortRuntime>)Field("_runtimes").GetValue(session)!).Add(port,
            new SerialPortRuntime(stream, () => false, () => throw new Exception("不应访问"), probe, _ => { }));
    }
    private static FieldInfo Field(string name) => typeof(SerialPortSession).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void Probe(SerialPortSession session) => typeof(SerialPortSession).GetMethod("CheckReceiveHealth", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(session, [Field("_serialPort").GetValue(session), Field("_connectionGeneration").GetValue(session)]);

    private sealed class TestStream(string text) : Stream
    {
        private readonly Queue<byte> _input = new(Encoding.UTF8.GetBytes(text));
        public int Pending => _input.Count;
        public List<byte> Written { get; } = [];
        public int WriteCalls { get; private set; }
        public int DisposeCount { get; private set; }
        public bool FailWrite { get; init; }
        public int MaxRead { get; init; } = int.MaxValue;
        public int ReadCalls { get; private set; }
        public void Enqueue(string data) { foreach (var b in Encoding.UTF8.GetBytes(data)) _input.Enqueue(b); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCalls++;
            var read = Math.Min(Math.Min(count, Pending), MaxRead);
            for (var i = 0; i < read; i++) buffer[offset + i] = _input.Dequeue();
            return read;
        }
        public override void Write(byte[] buffer, int offset, int count)
        {
            WriteCalls++;
            if (FailWrite) throw new IOException("模拟驱动写入失败");
            Written.AddRange(buffer.AsSpan(offset, count).ToArray());
        }
        protected override void Dispose(bool disposing) { DisposeCount++; base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
