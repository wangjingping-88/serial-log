using SerialLog.Core.Serial;

namespace SerialLog.Tests;

public sealed class SerialPortOwnershipTests
{
    private sealed class Owner(string name) : ISerialPortOwner
    {
        public string DisplayName => name;
        public bool WantsConnection { get; set; } = true;
        public int Releases { get; private set; }
        public Func<Task> Release { get; set; } = () => Task.CompletedTask;
        public async Task ReleaseForTransferAsync(string destination)
        {
            WantsConnection = false;
            Releases++;
            await Release();
        }
    }

    [Fact]
    public async Task Refusal_and_automatic_reconnect_never_revoke_owner()
    {
        var ports = new SerialPortOwnership(); var a = new Owner("A/node"); var b = new Owner("B/node");
        Assert.True(await ports.AcquireAsync("COM7", a, false, _ => false));
        Assert.False(await ports.AcquireAsync("com7", b, false, _ => false));
        Assert.False(await ports.AcquireAsync("COM7", b, true, _ => throw new Exception("自动重连不能询问转移")));
        Assert.Same(a, ports.FindOwner("COM7")); Assert.Equal(0, a.Releases); Assert.True(a.WantsConnection);
    }

    [Fact]
    public async Task Transfer_waits_for_release_and_does_not_block_other_ports()
    {
        var ports = new SerialPortOwnership();
        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = new Owner("A") { Release = () => released.Task }; var b = new Owner("B"); var c = new Owner("C");
        await ports.AcquireAsync("COM7", a, false, _ => false);
        var transfer = ports.AcquireAsync("COM7", b, false, _ => true);
        Assert.False(transfer.IsCompleted);
        Assert.False(a.WantsConnection);
        Assert.Same(a, ports.FindOwner("COM7"));
        Assert.True(await ports.AcquireAsync("COM8", c, false, _ => false));
        released.SetResult();
        Assert.True(await transfer);
        Assert.Same(b, ports.FindOwner("COM7"));
        Assert.False(await ports.AcquireAsync("COM7", a, true, _ => true));
    }

    [Fact]
    public async Task Release_failure_keeps_new_window_unowned_and_prevents_bypass()
    {
        var ports = new SerialPortOwnership();
        var a = new Owner("A") { Release = () => Task.FromException(new IOException("驱动仍占用")) }; var b = new Owner("B");
        await ports.AcquireAsync("COM7", a, false, _ => false);
        await Assert.ThrowsAsync<IOException>(() => ports.AcquireAsync("COM7", b, false, _ => true));
        Assert.Same(a, ports.FindOwner("COM7"));
        await Assert.ThrowsAsync<IOException>(() => ports.AcquireAsync("COM7", b, true, _ => false));
    }

    [Fact]
    public async Task Concurrent_connections_produce_exactly_one_owner()
    {
        var ports = new SerialPortOwnership();
        var owners = Enumerable.Range(0, 30).Select(i => new Owner(i.ToString())).ToArray();
        var results = await Task.WhenAll(owners.Select(o => Task.Run(() => ports.AcquireAsync("COM16", o, false, _ => false))));
        Assert.Single(results.Where(r => r));
        Assert.All(owners, o => Assert.Equal(0, o.Releases));
    }
}
