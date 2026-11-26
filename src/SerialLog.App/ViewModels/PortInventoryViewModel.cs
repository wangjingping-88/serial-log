using System.Text.RegularExpressions;
using SerialLog.Core.Configuration;
using SerialLog.Core.Serial;

namespace SerialLog.App.ViewModels;

internal sealed record PortUse(TestWorkspaceRuntime Workspace, SerialWindowViewModel Window, int Priority)
{
    public string Label => $"{Workspace.Name} / {Window.Title}";
    public string State => Priority switch { 3 => "烧录中", 2 => "已连接", 1 => "连接中", _ => "仅配置" };
}

internal sealed record PortInventoryRow(LocalPortInfo Port, bool Present, string Alias, string GroupId,
    string GroupName, string GroupColor, string State, string Owner)
{
    public string PortName => Port.Name;
    public string DeviceName => Port.DeviceName;
    public string Manufacturer => Port.Manufacturer;
    public string DriverService => Port.DriverService;
    public string[] SearchValues => [PortName, Alias, GroupName, DeviceName, Manufacturer, DriverService, State, Owner, Port.DeviceId];
}

internal static class PortInventoryViewModel
{
    public static PortUse[] FindUses(IEnumerable<TestWorkspaceRuntime> workspaces, string port) =>
        workspaces.SelectMany(workspace => workspace.ViewModel.SerialWindows.Where(window => !window.IsRemote)
            .Select(window => new PortUse(workspace, window, string.Equals(window.ActiveLocalPortName, port, StringComparison.OrdinalIgnoreCase)
                ? window.IsFirmwareBusy ? 3 : window.IsConnected ? 2 : 1 : 0)))
            .Where(use => use.Priority > 0 || string.Equals(use.Window.PortName, port, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(use => use.Priority).ToArray();

    public static PortInventoryRow CreateRow(LocalPortInfo port, bool present, PortInventorySettings settings, PortUse[] uses)
    {
        var groupId = settings.GroupId(port);
        var group = settings.Groups.FirstOrDefault(g => g.Id.Equals(groupId, StringComparison.OrdinalIgnoreCase));
        var owner = uses.FirstOrDefault();
        return new(port, present, settings.Alias(port), group?.Id ?? "", group?.Name ?? "未分组", group?.Color ?? "Transparent",
            present ? owner?.State ?? "外部占用未知" : owner is { Priority: > 0 } ? "未检测到 · " + owner.State : "未检测到",
            string.Join("；", uses.Select(use => use.Label)));
    }

    public static string SortKey(PortInventorySettings settings, string group, LocalPortInfo port)
    {
        var order = settings.Orders.GetValueOrDefault(group);
        return order?.Contains(port.Key, StringComparer.OrdinalIgnoreCase) == true ? port.Key
            : order?.Contains(LocalPortInfo.LegacyKey(port.Name), StringComparer.OrdinalIgnoreCase) == true
                ? LocalPortInfo.LegacyKey(port.Name) : port.Key;
    }

    public static PortInventoryRow[] Filter(IEnumerable<PortInventoryRow> rows, string query, out string? error)
    {
        error = null;
        query = query.Trim();
        if (query.Length == 0) return rows.ToArray();
        if (!query.StartsWith('/') || query.LastIndexOf('/') <= 0)
            return rows.Where(row => string.Join(" ", row.SearchValues).Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();

        var end = query.LastIndexOf('/');
        var options = RegexOptions.CultureInvariant;
        var flags = query[(end + 1)..];
        if (flags.Contains('i')) options |= RegexOptions.IgnoreCase;
        if (flags.Contains('m')) options |= RegexOptions.Multiline;
        if (flags.Contains('s')) options |= RegexOptions.Singleline;
        if (flags.Any(flag => flag is not ('i' or 'm' or 's' or 'g' or 'y' or 'u')) ||
            flags.Distinct().Count() != flags.Length)
        { error = "正则修饰符无效。"; return []; }
        try
        {
            var matcher = new Regex(query[1..end], options, TimeSpan.FromMilliseconds(30));
            return rows.Where(row => row.SearchValues.Any(matcher.IsMatch)).ToArray();
        }
        catch (ArgumentException) { error = "正则表达式无效。"; return []; }
        catch (RegexMatchTimeoutException) { error = "正则匹配超时，请简化表达式。"; return []; }
    }
}
