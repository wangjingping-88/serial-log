namespace SerialLog.Core.Logging;

public interface IClock
{
    DateTimeOffset Now { get; }
}
