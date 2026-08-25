using SerialLog.App.Infrastructure;

namespace SerialLog.Tests;

public sealed class StaClipboardTextServiceTests
{
    [Fact]
    public async Task Clipboard_write_runs_off_the_calling_thread_and_blocks_duplicate_operations()
    {
        using var writerStarted = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        var callingThreadId = Environment.CurrentManagedThreadId;
        var writerThreadId = 0;
        var service = new StaClipboardTextService(_ =>
        {
            writerThreadId = Environment.CurrentManagedThreadId;
            writerStarted.Set();
            releaseWriter.Wait();
        });

        var operation = service.SetTextAsync("log");
        Assert.True(writerStarted.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(service.IsBusy);
        Assert.NotEqual(callingThreadId, writerThreadId);
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = service.SetTextAsync("second");
        });

        releaseWriter.Set();
        await operation.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(service.IsBusy);
    }

    [Fact]
    public async Task Clipboard_writer_exception_is_returned_to_the_caller()
    {
        var service = new StaClipboardTextService(_ => throw new InvalidOperationException("clipboard failed"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SetTextAsync("log"));

        Assert.Equal("clipboard failed", exception.Message);
        Assert.False(service.IsBusy);
    }
}
