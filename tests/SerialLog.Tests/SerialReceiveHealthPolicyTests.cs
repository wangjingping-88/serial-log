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
}
