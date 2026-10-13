using System.IO.Ports;
using System.Reflection;
using System.Text;
using SerialLog.Core.Serial;

namespace SerialLog.Tests;

public class SerialBinaryLeaseTests
{
    [Fact]
    public async Task Bounded_receive_buffer_fails_explicitly_and_cancel_interrupts_empty_read()
    {
        var released = 0;
        var lease = new SerialBinaryLease((_, _) => ValueTask.CompletedTask, _ => { released++; return ValueTask.CompletedTask; });
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lease.ReadAsync(new byte[1], canceled.Token).AsTask());
        lease.Receive(new byte[65537]);
        var error = await Assert.ThrowsAsync<IOException>(() => lease.ReadAsync(new byte[1], default).AsTask());
        Assert.Contains("溢出", error.Message);
        await lease.DisposeAsync(); await lease.DisposeAsync();
        Assert.Equal(1, released);
    }

    [Fact]
    public async Task Lease_routes_only_binary_bytes_and_replays_post_ack_startup_text_on_release()
    {
        using var session = CreateConnectedSession();
        var logs = new List<string>();
        session.LinesReceived += (_, lines) => logs.AddRange(lines.Select(l => l.Text));
        var lease = Assert.IsType<SerialBinaryLease>(await session.AcquireBinaryLeaseAsync(default));
        lease.Receive(new byte[] { 6 });
        lease.Receive(Encoding.ASCII.GetBytes("+SYSTEM:START\r\n"));
        var b = new byte[1];
        Assert.Equal(1, await lease.ReadAsync(b, default)); Assert.Equal(6, b[0]);
        Assert.Empty(logs);
        await lease.DisposeAsync();
        Assert.Contains(logs, l => l.Contains("+SYSTEM:START"));
        Assert.DoesNotContain(logs, l => l.Contains('\x06'));
        // 释放后同一会话允许新的租约，没有遗留锁。
        await using var second = await session.AcquireBinaryLeaseAsync(default);
    }

    [Fact]
    public async Task Lease_excludes_second_consumer_text_send_and_baud_rate_change()
    {
        using var session = CreateConnectedSession();
        await using var lease = await session.AcquireBinaryLeaseAsync(default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AcquireBinaryLeaseAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendAsync("ordinary", default));
        Assert.Throws<InvalidOperationException>(() => session.ChangeBaudRate(460800));
    }

    [Fact]
    public async Task Acquisition_waits_for_inflight_writer_and_can_be_canceled()
    {
        using var session = CreateConnectedSession();
        var gate = GetField<SemaphoreSlim>(session, "_writeGate");
        await gate.WaitAsync();
        using var cancellation = new CancellationTokenSource();
        var acquisition = session.AcquireBinaryLeaseAsync(cancellation.Token);
        Assert.False(acquisition.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquisition);
        gate.Release();
        await using var lease = await session.AcquireBinaryLeaseAsync(default);
    }

    [Fact]
    public async Task Health_probe_still_faults_closed_handle_and_wakes_binary_reader()
    {
        using var session = CreateConnectedSession();
        await using var lease = await session.AcquireBinaryLeaseAsync(default);
        var diagnostics = new List<string>();
        session.LinesReceived += (_, lines) => diagnostics.AddRange(lines.Select(l => l.Text));
        var read = lease.ReadAsync(new byte[1], default).AsTask();
        var port = GetField<SerialPort>(session, "_serialPort");
        typeof(SerialPortSession).GetMethod("CheckReceiveHealth", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(session, [port, 0L]);
        Assert.False(session.IsConnected);
        Assert.Contains(diagnostics, line => line.Contains("请手动重连"));
        await Assert.ThrowsAnyAsync<Exception>(() => read.WaitAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Post_text_matching_is_mirrored_once_and_mode_switch_keeps_partial_startup_line()
    {
        using var session = CreateConnectedSession(); var logs = new List<string>();
        session.LinesReceived += (_, lines) => logs.AddRange(lines.Select(l => l.Text));
        var lease = Assert.IsType<SerialBinaryLease>(await session.AcquireBinaryLeaseAsync(default));
        lease.Receive(new byte[] { 6 }); lease.Receive(Encoding.UTF8.GetBytes("+SYSTEM:START 设备\r\n"));
        var one = new byte[1]; await lease.ReadAsync(one, default); Assert.Equal(6, one[0]);
        await lease.SetReceiveModeAsync(BinaryReceiveMode.ObserveText, default);
        for (var i = 0; i < "+SYSTEM:START".Length; i++) await lease.ReadAsync(one, default);
        await lease.SetReceiveModeAsync(BinaryReceiveMode.LogOnly, default);
        Assert.Single(logs.Where(l => l.Contains("+SYSTEM:START 设备")));
        lease.Receive(Encoding.UTF8.GetBytes("正常日志\r\n"));
        await lease.DisposeAsync();
        Assert.Single(logs.Where(l => l.Contains("+SYSTEM:START"))); Assert.Single(logs.Where(l => l.Contains("正常日志")));
        Assert.DoesNotContain(logs, l => l.Contains('\x06'));
        await using var next = await session.AcquireBinaryLeaseAsync(default);
    }

    [Fact]
    public async Task Failure_cleanup_discards_protocol_residue_and_log_only_does_not_overflow_binary_queue()
    {
        var logs = new List<byte>();
        var lease = new SerialBinaryLease((_, _) => ValueTask.CompletedTask, _ => ValueTask.CompletedTask, bytes => logs.AddRange(bytes.ToArray()));
        lease.Receive(new byte[] { 0xAA, 1, 0, 4, 5, 0xEE });
        await lease.DiscardReceiveBufferAsync(); await lease.SetReceiveModeAsync(BinaryReceiveMode.LogOnly, default);
        Assert.Empty(logs);
        lease.Receive(new byte[65537]); Assert.Equal(65537, logs.Count);
        await lease.SetReceiveModeAsync(BinaryReceiveMode.Raw, default); lease.Receive(new byte[] { 0x43 });
        var one = new byte[1]; await lease.ReadAsync(one, default); Assert.Equal(0x43, one[0]);
        await lease.DisposeAsync();
    }

    [Fact]
    public async Task Mode_switch_notifies_log_subscribers_outside_receive_lock()
    {
        using var session = CreateConnectedSession();
        var notified = false;
        session.LinesReceived += (_, _) => { notified = true; Assert.False(Monitor.IsEntered(GetField<object>(session, "_receiveLock"))); };
        var lease = Assert.IsType<SerialBinaryLease>(await session.AcquireBinaryLeaseAsync(default));
        lease.Receive(Encoding.ASCII.GetBytes("startup\r\n"));
        await lease.SetReceiveModeAsync(BinaryReceiveMode.LogOnly, default);
        Assert.True(notified); Assert.True(lease.IsLogOnly);
        await lease.DisposeAsync();
    }

    private static SerialPortSession CreateConnectedSession()
    {
        // 不打开真实端口：验证会话同步与失效句柄路径。
        var session = new SerialPortSession("test");
        typeof(SerialPortSession).GetField("_serialPort", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, new SerialPort());
        typeof(SerialPortSession).GetField("_isConnected", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, 1);
        return session;
    }
    private static T GetField<T>(SerialPortSession session, string name) =>
        (T)typeof(SerialPortSession).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
}
