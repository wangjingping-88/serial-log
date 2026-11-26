namespace SerialLog.Core.Serial;

public sealed record LocalPortInfo(string Name, string DeviceId = "", string DeviceName = "",
    string Manufacturer = "", string DriverService = "")
{
    public string Key => DeviceId.Length > 0 ? DeviceId : LegacyKey(Name);
    public static string LegacyKey(string name) => "port:" + name;
}

public sealed record LocalPortEvent(bool Arrived, LocalPortInfo Port, string Label, DateTimeOffset Time)
{
    public string Description => $"{(Arrived ? "插入" : "拔出")} · {Label}";
}

/// <summary>单写者端口清单：只有成功的端口枚举可以更改在场状态。</summary>
public sealed class PortInventory
{
    public const int HistoryLimit = 80;
    private readonly Dictionary<string, LocalPortInfo> _ports = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _missingOnce = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<LocalPortEvent> _history = [];
    public bool Initialized { get; private set; }
    public string? ScanError { get; private set; }
    public IReadOnlyList<LocalPortInfo> Ports => SortNames(_ports.Keys).Select(name => _ports[name]).ToArray();
    public IReadOnlyList<LocalPortEvent> History => _history;
    public LocalPortInfo? Find(string name) => _ports.GetValueOrDefault(name);

    public IReadOnlyList<LocalPortEvent> ApplyNames(IEnumerable<string> names, DateTimeOffset time,
        Func<LocalPortInfo, string> label)
    {
        var next = SortNames(names.Select(n => n.Trim().ToUpperInvariant())).ToHashSet(StringComparer.OrdinalIgnoreCase);
        ScanError = null;
        var events = new List<LocalPortEvent>();
        foreach (var name in SortNames(_ports.Keys))
        {
            if (next.Contains(name)) { _missingOnce.Remove(name); continue; }
            if (_missingOnce.Add(name)) continue;
            _missingOnce.Remove(name);
            var old = _ports[name];
            _ports.Remove(name);
            events.Add(new(false, old, label(old), time));
        }
        foreach (var name in SortNames(next))
        {
            var port = new LocalPortInfo(name);
            if (_ports.TryAdd(name, port) && Initialized)
                events.Add(new(true, port, label(port), time));
        }
        Initialized = true;
        foreach (var entry in events) _history.Insert(0, entry);
        if (_history.Count > HistoryLimit) _history.RemoveRange(HistoryLimit, _history.Count - HistoryLimit);
        return events;
    }

    public bool HasUnconfirmedRemoval => _missingOnce.Count > 0;

    public void ReportFailure(string message)
    {
        ScanError = message;
        _missingOnce.Clear();
    }

    public bool ApplyDetails(IEnumerable<LocalPortInfo> details, Func<LocalPortInfo, string>? label = null)
    {
        var changed = false;
        // 同一 COM 如果报告了不同设备，不能猜测设备身份或沿用之前的别名。
        var byName = details.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.DistinctBy(p => p.DeviceId, StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        foreach (var (name, previous) in _ports.ToArray())
        {
            var matches = byName.GetValueOrDefault(name);
            var next = matches is { Length: 1 } ? matches[0] with { Name = name } : new LocalPortInfo(name);
            if (previous == next) continue;
            _ports[name] = next;
            if (label is not null && previous.DeviceId.Length == 0 && next.DeviceId.Length > 0)
            {
                var index = _history.FindIndex(e => e.Port.Name == name);
                if (index >= 0 && _history[index].Arrived && _history[index].Port.DeviceId.Length == 0)
                    _history[index] = _history[index] with { Port = next, Label = label(next) };
            }
            changed = true;
        }
        return changed;
    }

    public void ClearHistory() => _history.Clear();

    public static string[] SortNames(IEnumerable<string> names) => names.Where(n => !string.IsNullOrWhiteSpace(n))
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n =>
            n.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && int.TryParse(n.AsSpan(3), out var number) ? number : int.MaxValue)
        .ThenBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
}
