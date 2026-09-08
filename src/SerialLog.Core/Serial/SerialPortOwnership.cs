using System.Collections.Concurrent;

namespace SerialLog.Core.Serial;

public interface ISerialPortOwner
{
    string DisplayName { get; }
    bool WantsConnection { get; }
    Task ReleaseForTransferAsync(string destination);
}

/// <summary>应用级端口租约。按端口串行化转移，不阻塞其他端口。</summary>
public sealed class SerialPortOwnership
{
    private sealed class Slot
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public ISerialPortOwner? Owner;
    }
    private readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.OrdinalIgnoreCase);

    public ISerialPortOwner? FindOwner(string port) => _slots.TryGetValue(port.Trim(), out var slot) ? slot.Owner : null;

    public async Task<bool> AcquireAsync(string port, ISerialPortOwner requester, bool automatic,
        Func<ISerialPortOwner, bool> confirmTransfer, CancellationToken cancellationToken = default)
    {
        var slot = _slots.GetOrAdd(port.Trim(), _ => new Slot());
        await slot.Gate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var previous = slot.Owner;
            if (ReferenceEquals(previous, requester)) return true;
            if (previous is not null)
            {
                if (previous.WantsConnection && (automatic || !confirmTransfer(previous))) return false;
                await previous.ReleaseForTransferAsync(requester.DisplayName).WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            slot.Owner = requester;
            return true;
        }
        finally { slot.Gate.Release(); }
    }
}
