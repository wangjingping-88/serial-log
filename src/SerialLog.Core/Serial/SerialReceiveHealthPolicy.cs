namespace SerialLog.Core.Serial;

internal static class SerialReceiveHealthPolicy
{
    public static bool HasTimedOut(
        DateTimeOffset lastReceiveActivity,
        DateTimeOffset now,
        TimeSpan silenceTimeout)
    {
        if (silenceTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(silenceTimeout));
        }

        return now - lastReceiveActivity >= silenceTimeout;
    }
}
