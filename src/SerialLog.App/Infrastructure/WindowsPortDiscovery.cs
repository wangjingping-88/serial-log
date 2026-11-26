using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using SerialLog.Core.Serial;

namespace SerialLog.App.Infrastructure;

internal static class WindowsPortDiscovery
{
    private static readonly Guid PortsSetupClass = new("4D36E978-E325-11CE-BFC1-08002BE10318");
    private static readonly Guid ComPortInterface = new("86E0D1E0-8089-11D0-9CE4-08003E301F73");
    private const uint Present = 2;
    private const uint DeviceInterface = 16;
    private const int NoMoreItems = 259;
    private static readonly nint InvalidHandle = new(-1);

    // 只访问设备属性/设备注册表键，不打开 COM 设备。
    internal static LocalPortInfo[] ReadDetails() => Enumerate(PortsSetupClass, Present)
        .Concat(Enumerate(ComPortInterface, Present | DeviceInterface))
        .DistinctBy(p => (p.Name.ToUpperInvariant(), p.DeviceId.ToUpperInvariant())).ToArray();

    private static IEnumerable<LocalPortInfo> Enumerate(Guid guid, uint flags)
    {
        var list = SetupDiGetClassDevsW(ref guid, null, 0, flags);
        if (list == InvalidHandle) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            for (uint index = 0; ; index++)
            {
                var entry = new DeviceInfo { Size = Marshal.SizeOf<DeviceInfo>() };
                if (!SetupDiEnumDeviceInfo(list, index, ref entry))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == NoMoreItems) yield break;
                    throw new Win32Exception(error);
                }

                using var key = SetupDiOpenDevRegKey(list, ref entry, 1, 0, 1, 0x20019); // DICS_FLAG_GLOBAL / DIREG_DEV / KEY_READ
                if (key.IsInvalid) continue;
                using var registry = RegistryKey.FromHandle(key);
                if (registry.GetValue("PortName") is not string raw) continue;
                var name = raw.Trim().ToUpperInvariant();
                if (!name.StartsWith("COM", StringComparison.Ordinal) || !int.TryParse(name.AsSpan(3), out var port) || port <= 0)
                    continue;

                var id = new StringBuilder(512);
                if (!SetupDiGetDeviceInstanceIdW(list, ref entry, id, id.Capacity, out _)) continue;
                var friendlyName = Property(list, ref entry, 12); // SPDRP_FRIENDLYNAME
                if (friendlyName.Length == 0) friendlyName = Property(list, ref entry, 0);
                yield return new(name, id.ToString(), friendlyName,
                    Property(list, ref entry, 11), Property(list, ref entry, 4)); // MFG / SERVICE
            }
        }
        finally { _ = SetupDiDestroyDeviceInfoList(list); }
    }

    private static string Property(nint list, ref DeviceInfo entry, uint property)
    {
        var buffer = new byte[4096];
        return SetupDiGetDeviceRegistryPropertyW(list, ref entry, property, out var type, buffer, buffer.Length, out var size)
            && type is 1 or 2 && size <= buffer.Length && size >= 2
            ? Encoding.Unicode.GetString(buffer, 0, size).TrimEnd('\0') : "";
    }

    internal static nint RegisterNotifications(nint window)
    {
        var filter = new DeviceFilter { Size = Marshal.SizeOf<DeviceFilter>(), Type = 5, ClassGuid = ComPortInterface };
        return RegisterDeviceNotificationW(window, ref filter, 0);
    }

    internal static bool IsDeviceChange(int message, nint parameter) =>
        message == 0x219 && parameter.ToInt64() is 0x8000 or 0x8004 or 0x0007;

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfo { public int Size; public Guid ClassGuid; public uint DevInst; public nuint Reserved; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DeviceFilter { public int Size; public int Type; public int Reserved; public Guid ClassGuid; public char Name; }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint SetupDiGetClassDevsW(ref Guid guid, string? enumerator, nint parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(nint info, uint index, ref DeviceInfo data);
    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern SafeRegistryHandle SetupDiOpenDevRegKey(nint info, ref DeviceInfo data, uint scope, uint hw, uint type, uint access);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceIdW(nint info, ref DeviceInfo data, StringBuilder id, int length, out int required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(nint info, ref DeviceInfo data, uint property,
        out uint type, [Out] byte[] buffer, int length, out int required);
    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(nint info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern nint RegisterDeviceNotificationW(nint window, ref DeviceFilter filter, uint flags);
    [DllImport("user32.dll")]
    internal static extern bool UnregisterDeviceNotification(nint notification);
}
