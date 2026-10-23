using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SerialLog.App.Infrastructure;

// 直接交付已渲染的 Unicode 文本，避免 OLE 写入成功后 Flush 再次争用剪贴板。
internal static class NativeClipboardTextWriter
{
    internal const int OpenAttempts = 20;

    internal static void SetText(string text) => Write(text, new WindowsClipboardApi(), Thread.Sleep);

    internal static void Write(string text, IClipboardTextApi api, Action<int> delay)
    {
        ArgumentNullException.ThrowIfNull(text);
        var owner = api.CreateOwner();
        nint memory = 0;
        var opened = false;
        try
        {
            // 在清空剪贴板之前准备内存，分配失败时保留原剪贴板。
            memory = api.AllocateText(text);
            for (var attempt = 0; attempt < OpenAttempts; attempt++)
            {
                if (api.TryOpen(owner))
                {
                    opened = true;
                    break;
                }

                if (attempt + 1 < OpenAttempts)
                    delay(50);
            }

            if (!opened)
                throw new ExternalException("剪贴板持续被其他程序占用，请稍后重试。", unchecked((int)0x800401D0));

            api.Empty();
            api.SetUnicodeText(memory);
            // SetClipboardData 成功即由系统持有；不可再次释放或重写。
            memory = 0;
        }
        finally
        {
            if (opened)
                api.Close();
            if (memory != 0)
                api.FreeText(memory);
            api.DestroyOwner(owner);
        }
    }
}

internal interface IClipboardTextApi
{
    nint CreateOwner();
    void DestroyOwner(nint owner);
    nint AllocateText(string text);
    void FreeText(nint memory);
    bool TryOpen(nint owner);
    void Empty();
    void SetUnicodeText(nint memory);
    void Close();
}

internal sealed class WindowsClipboardApi : IClipboardTextApi
{
    public nint CreateOwner()
    {
        // 必须有非空 HWND：OpenClipboard(NULL) 后 EmptyClipboard 会使写入失败。
        // STATIC 为系统窗口类，HWND_MESSAGE 不显示窗口，不依赖主窗口或 UI 线程。
        var owner = CreateWindowExW(0, "STATIC", "SerialLog Clipboard", 0,
            0, 0, 0, 0, new nint(-3), 0, 0, 0);
        if (owner == 0)
            throw NativeError("创建剪贴板所有者");
        return owner;
    }

    public void DestroyOwner(nint owner) => DestroyWindow(owner);

    public nint AllocateText(string text)
    {
        var byteCount = checked(((long)text.Length + 1) * sizeof(char));
        var memory = GlobalAlloc(0x0002, checked((nuint)byteCount)); // GMEM_MOVEABLE
        if (memory == 0)
            throw new OutOfMemoryException("无法分配剪贴板文本内存。");

        var pointer = GlobalLock(memory);
        if (pointer == 0)
        {
            var error = NativeError("锁定剪贴板文本内存");
            GlobalFree(memory);
            throw error;
        }

        try
        {
            Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
            Marshal.WriteInt16(pointer, checked(text.Length * sizeof(char)), 0);
        }
        catch
        {
            GlobalUnlock(memory);
            GlobalFree(memory);
            throw;
        }

        GlobalUnlock(memory);
        return memory;
    }

    public void FreeText(nint memory) => GlobalFree(memory);
    public bool TryOpen(nint owner) => OpenClipboard(owner);

    public void Empty()
    {
        if (!EmptyClipboard())
            throw NativeError("清空剪贴板");
    }

    public void SetUnicodeText(nint memory)
    {
        if (SetClipboardData(13, memory) == 0) // CF_UNICODETEXT，非延迟渲染
            throw NativeError("写入剪贴板");
    }

    public void Close() => CloseClipboard();

    private static ExternalException NativeError(string operation)
    {
        var error = Marshal.GetLastWin32Error();
        return new ExternalException($"{operation}失败：{new Win32Exception(error).Message}（Win32={error}）",
            error == 0 ? unchecked((int)0x80004005) : unchecked((int)(0x80070000u | ((uint)error & 0xffff))));
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint CreateWindowExW(uint extendedStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint memory);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalFree(nint memory);
}
