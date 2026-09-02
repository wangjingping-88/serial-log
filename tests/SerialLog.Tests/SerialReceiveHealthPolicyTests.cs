using System.IO.Ports;
using System.Reflection;
using SerialLog.Core.Serial;

namespace SerialLog.Tests;

public sealed class SerialReceiveHealthPolicyTests
{
    private static readonly DateTimeOffset LastActivity =
        DateTimeOffset.Parse("2026-08-25T10:00:00+08:00");

    [Fact]
    public void Connection_remains_healthy_before_silence_timeout()
    {
        var timedOut = SerialReceiveHealthPolicy.HasTimedOut(
            LastActivity,
            LastActivity.AddSeconds(89),
            TimeSpan.FromSeconds(90));

        Assert.False(timedOut);
    }

    [Fact]
    public void Connection_times_out_at_silence_limit()
    {
        var timedOut = SerialReceiveHealthPolicy.HasTimedOut(
            LastActivity,
            LastActivity.AddSeconds(90),
            TimeSpan.FromSeconds(90));

        Assert.True(timedOut);
    }

    [Fact]
    public void Backward_clock_adjustment_does_not_trigger_timeout()
    {
        var timedOut = SerialReceiveHealthPolicy.HasTimedOut(
            LastActivity,
            LastActivity.AddMinutes(-1),
            TimeSpan.FromSeconds(90));

        Assert.False(timedOut);
    }

    [Fact]
    public void Invalid_silence_timeout_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SerialReceiveHealthPolicy.HasTimedOut(
                LastActivity,
                LastActivity,
                TimeSpan.Zero));
    }

    [Theory]
    [InlineData(SerialError.RXOver)]
    [InlineData(SerialError.Overrun)]
    [InlineData(SerialError.RXParity)]
    [InlineData(SerialError.Frame)]
    [InlineData(SerialError.TXFull)]
    public void Driver_reported_errors_are_recoverable_warnings(SerialError error)
    {
        Assert.True(SerialReceiveHealthPolicy.IsRecoverableDriverError(error));
    }

    [Fact]
    public void Repeated_driver_warning_is_suppressed_inside_interval()
    {
        var shouldReport = SerialReceiveHealthPolicy.ShouldReportDriverError(
            LastActivity,
            LastActivity.AddSeconds(29),
            TimeSpan.FromSeconds(30));

        Assert.False(shouldReport);
    }

    [Fact]
    public void Different_driver_warnings_are_aggregated_inside_interval()
    {
        Assert.False(SerialReceiveHealthPolicy.ShouldReportDriverError(
            LastActivity,
            LastActivity.AddSeconds(1),
            TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Driver_warning_is_reported_after_interval()
    {
        Assert.True(SerialReceiveHealthPolicy.ShouldReportDriverError(
            LastActivity,
            LastActivity.AddSeconds(30),
            TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void Receive_health_timer_remains_active_when_silence_reconnect_is_disabled()
    {
        using var session = new SerialPortSession(
            "test",
            receiveHealthCheckInterval: TimeSpan.FromMinutes(1));
        using var serialPort = new SerialPort();
        var createTimer = GetPrivateMethod("CreateReceiveHealthTimer");

        using var timer = Assert.IsType<Timer>(createTimer.Invoke(session, [serialPort, 0L]));
    }

    [Fact]
    public void Periodic_receive_probe_marks_a_closed_driver_handle_as_faulted()
    {
        using var session = new SerialPortSession(
            "test",
            receiveHealthCheckInterval: TimeSpan.FromMinutes(1));
        var serialPort = new SerialPort();
        var diagnostics = new List<string>();
        session.LinesReceived += (_, lines) => diagnostics.AddRange(lines.Select(line => line.Text));
        SetPrivateField(session, "_serialPort", serialPort);
        SetPrivateField(session, "_isConnected", 1);

        GetPrivateMethod("CheckReceiveHealth").Invoke(session, [serialPort, 0L]);

        Assert.False(session.IsConnected);
        Assert.Contains(
            diagnostics,
            line => line.Contains("串口接收状态检查失败", StringComparison.Ordinal));
    }

    private static MethodInfo GetPrivateMethod(string name)
    {
        return typeof(SerialPortSession).GetMethod(
                   name,
                   BindingFlags.Instance | BindingFlags.NonPublic) ??
               throw new InvalidOperationException($"未找到私有方法：{name}");
    }

    private static void SetPrivateField(SerialPortSession session, string name, object value)
    {
        var field = typeof(SerialPortSession).GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new InvalidOperationException($"未找到私有字段：{name}");
        field.SetValue(session, value);
    }
}
