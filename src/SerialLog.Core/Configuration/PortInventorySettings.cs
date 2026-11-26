using System.Text.Json;
using System.Text.RegularExpressions;
using SerialLog.Core.Serial;

namespace SerialLog.Core.Configuration;

public sealed record LocalPortGroup(string Id, string Name, string Color);

public sealed class PortInventorySettings
{
    public const string AllOrder = "all";
    public const string UngroupedOrder = "ungrouped";
    public int SchemaVersion { get; set; } = 1;
    public bool ShowNotifications { get; set; } = true;
    public Dictionary<string, string> Aliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<LocalPortGroup> Groups { get; set; } = [];
    public Dictionary<string, string> Assignments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> Orders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string Alias(LocalPortInfo port) => Aliases.GetValueOrDefault(port.Key) ??
        Aliases.GetValueOrDefault(LocalPortInfo.LegacyKey(port.Name)) ?? "";

    public string Label(LocalPortInfo port) => Alias(port) is { Length: > 0 } alias ? $"{port.Name} · {alias}" : port.Name;

    public string GroupId(LocalPortInfo port) => Assignments.GetValueOrDefault(port.Key) ??
        Assignments.GetValueOrDefault(LocalPortInfo.LegacyKey(port.Name)) ?? "";
}

public static class PortInventorySettingsStore
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public const string DefaultGroupColor = "#138A8A";

    public static PortInventorySettings Load(string path, out string? warning)
    {
        warning = null;
        if (!File.Exists(path) && !File.Exists(path + ".bak")) return new();
        try { return ParseCurrent(path); }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            // 未来版本不可降级覆盖，即使旁边还有较旧备份。
            if (ex is NotSupportedException) throw;
            try
            {
                var settings = ParseCurrent(path + ".bak");
                warning = "本机串口设置主文件不可用，已读取备份。";
                return settings;
            }
            catch (Exception backup) when (IsReadFailure(backup))
            { throw new InvalidDataException("本机串口设置无法读取，已保留原文件。", ex); }
        }
    }

    public static void Save(string path, PortInventorySettings settings)
    {
        Normalize(settings);
        var json = JsonSerializer.SerializeToUtf8Bytes(settings, Json);
        if (json.LongLength > MaxBytes) throw new InvalidDataException("本机串口设置超过大小限制。");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, json);
            if (File.Exists(path))
            {
                var backup = path + ".bak";
                try { _ = ParseCurrent(path); }
                catch (Exception ex) when (ex is JsonException or InvalidDataException)
                {
                    // 不用损坏的主文件覆盖最后一份有效备份。
                    backup = path + ".invalid-" + Guid.NewGuid().ToString("N") + ".bak";
                }
                File.Replace(temp, path, backup);
            }
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static PortInventorySettings Clone(PortInventorySettings settings) =>
        Normalize(JsonSerializer.Deserialize<PortInventorySettings>(JsonSerializer.SerializeToUtf8Bytes(settings, Json), Json)!);

    public static PortInventorySettings ReadLegacy(IEnumerable<string> files)
    {
        var settings = new PortInventorySettings();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files)
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            if (!seen.Add(name)) throw new InvalidDataException("同一类旧配置只能选择一个文件（主文件或备份）。");
            using var doc = ReadJson(path);
            var root = doc.RootElement;
            CheckVersion(root, optional: true);
            switch (name.ToLowerInvariant())
            {
                case "port-aliases.json":
                    var aliases = root.TryGetProperty("aliases", out var nested) ? nested : root;
                    if (aliases.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException("别名文件结构无效。");
                    settings.Aliases = aliases.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String)
                        .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
                    break;
                case "port-groups.json":
                    if (!root.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
                        throw new InvalidDataException("分组文件缺少分组列表。");
                    var groupSettings = root.Deserialize<PortInventorySettings>(Json)!;
                    settings.Groups = groupSettings.Groups;
                    settings.Assignments = groupSettings.Assignments;
                    settings.Orders = groupSettings.Orders;
                    break;
                default: throw new InvalidDataException($"不是支持的旧串口管理配置：{name}");
            }
        }
        return Normalize(settings);
    }

    public static (PortInventorySettings Settings, int Conflicts) Merge(PortInventorySettings current, PortInventorySettings legacy,
        bool preferImported)
    {
        var result = Clone(current);
        var conflicts = 0;
        foreach (var group in legacy.Groups)
        {
            var existing = result.Groups.FindIndex(g => g.Id.Equals(group.Id, StringComparison.OrdinalIgnoreCase));
            if (existing < 0) result.Groups.Add(group);
            else if (result.Groups[existing] != group)
            {
                ++conflicts;
                if (preferImported) result.Groups[existing] = group;
            }
        }
        MergeMap(result.Aliases, legacy.Aliases, (a,b) => a == b);
        MergeMap(result.Assignments, legacy.Assignments, (a,b) => a.Equals(b, StringComparison.OrdinalIgnoreCase));
        MergeMap(result.Orders, legacy.Orders, (a,b) => a.SequenceEqual(b, StringComparer.OrdinalIgnoreCase));
        return (Clone(result), conflicts);

        void MergeMap<T>(Dictionary<string, T> target, Dictionary<string, T> source, Func<T, T, bool> equal)
        {
            foreach (var (key, value) in source)
            {
                if (target.TryAdd(key, value) || equal(target[key], value)) continue;
                ++conflicts;
                if (preferImported) target[key] = value;
            }
        }
    }

    public static string NormalizeColor(string? color) => Regex.IsMatch(color ?? "", @"\A#[0-9a-fA-F]{6}\z")
        ? color!.ToUpperInvariant() : DefaultGroupColor;

    private static PortInventorySettings Normalize(PortInventorySettings settings)
    {
        if (settings.SchemaVersion != 1) throw new NotSupportedException("本机串口设置版本不兼容。");
        settings.Aliases = NormalizeMap(settings.Aliases, 80);
        settings.Groups = (settings.Groups ?? []).Where(g => g is not null && !string.IsNullOrWhiteSpace(g.Id)
                && !string.IsNullOrWhiteSpace(g.Name) && g.Id.Trim().Length <= 128
                && !new[] { PortInventorySettings.AllOrder, PortInventorySettings.UngroupedOrder }.Contains(g.Id.Trim(), StringComparer.OrdinalIgnoreCase))
            .Select(g => new LocalPortGroup(g.Id.Trim(), Limit(g.Name, 24), NormalizeColor(g.Color)))
            .DistinctBy(g => g.Id, StringComparer.OrdinalIgnoreCase).ToList();
        var ids = settings.Groups.Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        settings.Assignments = NormalizeMap(settings.Assignments, 128);
        foreach (var pair in settings.Assignments.Where(p => p.Value.Length > 0 && !ids.Contains(p.Value)).ToArray())
            settings.Assignments[pair.Key] = "";
        var orders = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        ids.UnionWith([PortInventorySettings.AllOrder, PortInventorySettings.UngroupedOrder]);
        foreach (var (key, list) in settings.Orders ?? [])
            if (ids.Contains(key) && list is not null)
                orders[key] = list.Where(k => !string.IsNullOrWhiteSpace(k) && k.Trim().Length <= 1024)
                    .Select(k => k.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        settings.Orders = orders;
        return settings;
    }

    private static Dictionary<string, string> NormalizeMap(Dictionary<string, string>? source, int maxLength)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in source ?? [])
            if (!string.IsNullOrWhiteSpace(key) && key.Trim().Length <= 1024 && value is not null)
                result[key.Trim()] = Limit(value, maxLength);
        return result;
    }

    private static string Limit(string value, int max) { var trimmed = value.Trim(); return trimmed[..Math.Min(max, trimmed.Length)]; }
    private static bool IsReadFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or NotSupportedException;

    private static PortInventorySettings ParseCurrent(string path)
    {
        using var doc = ReadJson(path);
        CheckVersion(doc.RootElement, optional: false);
        return Normalize(doc.RootElement.Deserialize<PortInventorySettings>(Json)!);
    }

    private static JsonDocument ReadJson(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaxBytes) throw new InvalidDataException("串口配置文件超过大小限制。");
        return JsonDocument.Parse(stream);
    }

    private static void CheckVersion(JsonElement root, bool optional)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("串口配置文件不是 JSON 对象。");
        if (!root.TryGetProperty("schemaVersion", out var version))
        {
            if (!optional) throw new InvalidDataException("缺少串口配置版本。");
            return;
        }
        if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1)
            throw new NotSupportedException("串口配置版本不兼容。");
    }
}
