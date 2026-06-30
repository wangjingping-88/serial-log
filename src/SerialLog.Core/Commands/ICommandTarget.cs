namespace SerialLog.Core.Commands;

public interface ICommandTarget
{
    string Id { get; }

    bool IsConnected { get; }

    Task SendAsync(string payload, CancellationToken cancellationToken);
}
