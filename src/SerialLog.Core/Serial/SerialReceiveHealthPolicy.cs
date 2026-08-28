using System.IO.Ports;

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

    public static bool IsRecoverableDriverError(SerialError error)
    {
        return error is SerialError.RXOver or
            SerialError.Overrun or
            SerialError.RXParity or
            SerialError.Frame or
            SerialError.TXFull;
    }

    public static bool ShouldReportDriverError(
        DateTimeOffset previousReportAt,
        DateTimeOffset now,
        TimeSpan minimumInterval)
    {
        if (minimumInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumInterval));
        }

        return now < previousReportAt ||
               now - previousReportAt >= minimumInterval;
    }
}
