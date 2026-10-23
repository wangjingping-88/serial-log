using System.Runtime.InteropServices;
using SerialLog.App.Infrastructure;

namespace SerialLog.Tests;

public sealed class NativeClipboardTextWriterTests
{
    [Fact]
    public async Task Native_owner_and_unicode_buffer_are_valid_without_touching_clipboard()
    {
        var service = new StaClipboardTextService(_ =>
        {
            var api = new WindowsClipboardApi();
            var owner = api.CreateOwner();
            Assert.NotEqual(nint.Zero, owner);
            try
            {
                foreach (var text in new[] { "", "中文\r\nemoji😀\t日志", new string('长', 100_000) })
                {
                    var memory = api.AllocateText(text);
                    try
                    {
                        var pointer = GlobalLock(memory);
                        Assert.NotEqual(nint.Zero, pointer);
                        try
                        {
                            Assert.Equal(text, Marshal.PtrToStringUni(pointer));
                            Assert.Equal(0, Marshal.ReadInt16(pointer, text.Length * sizeof(char)));
                        }
                        finally { GlobalUnlock(memory); }
                    }
                    finally { api.FreeText(memory); }
                }
            }
            finally { api.DestroyOwner(owner); }
        });
        await service.SetTextAsync("test").WaitAsync(TimeSpan.FromSeconds(5));
    }

    [DllImport("kernel32.dll")]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint memory);

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Successful_write_transfers_memory_once_and_does_not_reopen(int busyAttempts)
    {
        var api = new FakeClipboard { BusyAttempts = busyAttempts };
        var delays = new List<int>();
        NativeClipboardTextWriter.Write("中文日志\r\n第二行😀", api, delays.Add);
        Assert.Equal("中文日志\r\n第二行😀", api.Text);
        Assert.Equal(busyAttempts + 1, api.OpenCount);
        Assert.Equal(busyAttempts, delays.Count);
        Assert.All(delays, milliseconds => Assert.Equal(50, milliseconds));
        Assert.Equal(1, api.SetCount);
        Assert.Equal(1, api.EmptyCount);
        Assert.Equal(0, api.FreeCount);
        Assert.Equal(1, api.CloseCount);
        Assert.Equal(1, api.DestroyCount);
    }

    [Fact]
    public void Persistent_contention_is_bounded_and_does_not_empty_existing_clipboard()
    {
        var api = new FakeClipboard { BusyAttempts = int.MaxValue };
        var error = Assert.Throws<ExternalException>(() =>
            NativeClipboardTextWriter.Write("log", api, _ => { }));
        Assert.Equal(unchecked((int)0x800401D0), error.ErrorCode);
        Assert.Equal(NativeClipboardTextWriter.OpenAttempts, api.OpenCount);
        Assert.Equal(0, api.EmptyCount);
        Assert.Equal(0, api.SetCount);
        Assert.Equal(0, api.CloseCount);
        Assert.Equal(1, api.FreeCount);
        Assert.Equal(1, api.DestroyCount);
    }

    [Theory]
    [InlineData("allocate", 0, 0)]
    [InlineData("empty", 1, 1)]
    [InlineData("set", 1, 1)]
    public void Real_failure_is_reported_and_resources_are_released(string stage, int free, int close)
    {
        var api = new FakeClipboard { FailAt = stage };
        var error = Assert.Throws<ExternalException>(() =>
            NativeClipboardTextWriter.Write("log", api, _ => { }));
        Assert.Equal(stage, error.Message);
        Assert.Equal(free, api.FreeCount);
        Assert.Equal(close, api.CloseCount);
        Assert.Equal(1, api.DestroyCount);
    }

    private sealed class FakeClipboard : IClipboardTextApi
    {
        public int BusyAttempts;
        public string? FailAt;
        public string? Text;
        public int OpenCount, EmptyCount, SetCount, FreeCount, CloseCount, DestroyCount;
        public nint CreateOwner() => 42;
        public void DestroyOwner(nint owner) { Assert.Equal((nint)42, owner); DestroyCount++; }
        public nint AllocateText(string text)
        {
            Fail("allocate");
            Text = text;
            return 123;
        }
        public void FreeText(nint memory) { Assert.Equal((nint)123, memory); FreeCount++; }
        public bool TryOpen(nint owner) { Assert.Equal((nint)42, owner); return ++OpenCount > BusyAttempts; }
        public void Empty() { EmptyCount++; Fail("empty"); }
        public void SetUnicodeText(nint memory) { Assert.Equal((nint)123, memory); SetCount++; Fail("set"); }
        public void Close() => CloseCount++;
        private void Fail(string stage)
        {
            if (FailAt == stage)
                throw new ExternalException(stage);
        }
    }
}
