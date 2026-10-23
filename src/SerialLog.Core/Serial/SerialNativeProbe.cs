using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SerialLog.Core.Serial;

internal sealed record SerialNativeSnapshot(bool Supported, bool HandleClosed, bool HandleInvalid,
    bool? EventLoopStopped, bool ProbeSucceeded, int Error, uint Errors, uint InputBytes, uint OutputBytes,
    uint BaudRate, string Detail)
{
    public bool CanPoll => Supported && !HandleClosed && !HandleInvalid && EventLoopStopped == true && ProbeSucceeded;
    public override string ToString() => $"supported={Supported}; handleClosed={HandleClosed}; handleInvalid={HandleInvalid}; " +
        $"eventLoopStopped={EventLoopStopped}; probeOk={ProbeSucceeded}; win32={Error}; commErrors=0x{Errors:X}; " +
        $"rxQueue={InputBytes}; txQueue={OutputBytes}; driverBaud={BaudRate}; detail={Detail}";
}

// System.IO.Ports 8.0 的 IsOpen 同时受事件线程状态影响。只读探测，绝不改内部字段或重启事件线程。
// 私有字段不可识别时保守失败，沿用原来的断开/重连流程。
internal static class SerialNativeProbe
{
    internal static SerialNativeSnapshot Capture(Stream stream, bool queryQueue)
    {
        bool closed = true, invalid = true;
        bool? stopped = null;
        try
        {
            if (!OperatingSystem.IsWindows()) return Failure("非 Windows 平台");
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var type = stream.GetType();
            if (type.FullName != "System.IO.Ports.SerialStream") return Failure("不支持的底层流");
            var runner = type.GetField("_eventRunner", flags)?.GetValue(stream);
            stopped = runner?.GetType().GetField("endEventLoop", flags)?.GetValue(runner) as bool?;
            if (type.GetField("_handle", flags)?.GetValue(stream) is not SafeFileHandle handle)
                return Failure("底层句柄不存在");
            closed = handle.IsClosed; invalid = handle.IsInvalid;
            if (closed || invalid) return new(true, closed, invalid, stopped, false, 6, 0, 0, 0, 0, "句柄不可用");
            var dcb = new Dcb { Length = (uint)Marshal.SizeOf<Dcb>() };
            if (!GetCommState(handle, ref dcb))
                return new(true, closed, invalid, stopped, false, Marshal.GetLastWin32Error(), 0, 0, 0, 0, "GetCommState 失败");
            if (!queryQueue) return new(true, closed, invalid, stopped, true, 0, 0, 0, 0, dcb.BaudRate, "仅探测配置，未清除错误标志");
            // 与 SerialPort.BytesToRead 相同的队列查询 API；保留并记录返回的硬件错误位。
            if (!ClearCommError(handle, out var errors, out var stat))
                return new(true, closed, invalid, stopped, false, Marshal.GetLastWin32Error(), 0, 0, 0, dcb.BaudRate, "ClearCommError 失败");
            return new(true, closed, invalid, stopped, true, 0, errors, stat.InputBytes, stat.OutputBytes, dcb.BaudRate, "原句柄探测成功");
        }
        catch (Exception ex) { return Failure($"{ex.GetType().Name}: {ex.Message}"); }

        SerialNativeSnapshot Failure(string detail) => new(false, closed, invalid, stopped, false, 0, 0, 0, 0, 0, detail);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Dcb
    {
        public uint Length, BaudRate, Flags;
        public ushort Reserved, XonLimit, XoffLimit;
        public byte ByteSize, Parity, StopBits, XonChar, XoffChar, ErrorChar, EofChar, EventChar;
        public ushort Reserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ComStat { public uint Flags, InputBytes, OutputBytes; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCommState(SafeFileHandle handle, ref Dcb dcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClearCommError(SafeFileHandle handle, out uint errors, out ComStat stat);
}
