using System.IO.Ports;
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
}
