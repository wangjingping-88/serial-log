using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SerialLog.Core.Configuration;

/// <summary>可独立交换的命令组配置；不包含串口、运行队列或工作区配置。</summary>
public sealed class CommandGroupPackage
{
    public string Format { get; set; } = "SerialLog.CommandGroups";
    public int Version { get; set; } = 1;
    public List<CommandGroupConfig> Groups { get; set; } = [];
    public List<CommandGroupListConfig> Lists { get; set; } = [];
}

public static class CommandGroupPackageCodec
{
    public const int MaxBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }, MaxDepth = 32
    };

    public static string Export(IEnumerable<CommandGroupConfig> groups, IEnumerable<CommandGroupListConfig> lists)
    {
        // 经序列化深拷贝，绝不修改正在使用的配置。
        var package = JsonSerializer.Deserialize<CommandGroupPackage>(JsonSerializer.Serialize(new CommandGroupPackage
        { Groups = groups.ToList(), Lists = lists.ToList() }, Options), Options)!;
        foreach (var group in package.Groups) group.TargetIds = [];
        Validate(package);
        var json = JsonSerializer.Serialize(package, Options);
        CheckSize(json);
        return json;
    }

    public static CommandGroupPackage Parse(string json)
    {
        CheckSize(json);
        CommandGroupPackage package;
        try { package = JsonSerializer.Deserialize<CommandGroupPackage>(json, Options) ?? throw new InvalidDataException("配置为空。"); }
        catch (JsonException e) { throw new InvalidDataException("命令组配置 JSON 无效。", e); }
        // 缺少格式标记的普通工作区文件不能被误当成空配置导入。
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("format", out _) || !document.RootElement.TryGetProperty("version", out _))
            throw new InvalidDataException("不是独立命令组配置文件。");
        Validate(package);
        foreach (var group in package.Groups) group.TargetIds = [];
        return package;
    }

    public static CommandGroupPackage PrepareImport(string json, IEnumerable<string> groupNames, IEnumerable<string> listNames)
    {
        var package = Parse(json);
        var usedGroups = groupNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var usedLists = listNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var list in package.Lists)
        {
            var id = Guid.NewGuid().ToString("N"); mapping[list.Id] = id; list.Id = id;
            list.Name = Unique(list.Name.Trim(), usedLists);
        }
        foreach (var group in package.Groups)
        {
            group.Id = Guid.NewGuid().ToString("N"); group.Name = Unique(group.Name.Trim(), usedGroups);
            group.ListIds = group.ListIds.Select(id => mapping[id]).ToList(); group.TargetIds = [];
        }
        return package;
    }

    private static string Unique(string proposed, HashSet<string> used)
    {
        var name = proposed; var suffix = 2;
        while (!used.Add(name)) name = $"{proposed} ({suffix++})";
        return name;
    }

    private static void CheckSize(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaxBytes)
            throw new InvalidDataException("命令组配置为空或超过 1 MiB。");
    }

    private static void Validate(CommandGroupPackage package)
    {
        if (package.Format != "SerialLog.CommandGroups" || package.Version != 1)
            throw new InvalidDataException("不支持的命令组配置格式或版本。");
        if (package.Groups is null || package.Lists is null || package.Groups.Count > 2000 || package.Lists.Count > 200)
            throw new InvalidDataException("命令组或列表数量无效。");
        static bool ValidId(string? id) => id is { Length: > 0 and <= 128 };
        static bool ValidName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 256;
        if (package.Lists.Any(l => l is null || !ValidId(l.Id) || !ValidName(l.Name)) ||
            package.Lists.Select(l => l.Id).Distinct().Count() != package.Lists.Count ||
            package.Lists.Select(l => l.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != package.Lists.Count)
            throw new InvalidDataException("分类列表名称或身份无效／重复。");
        var ids = package.Lists.Select(l => l.Id).ToHashSet(StringComparer.Ordinal);
        if (package.Groups.Any(g => g is null || !ValidId(g.Id) || !ValidName(g.Name) ||
            g.ListIds is null || g.ListIds.Any(id => !ids.Contains(id)) || g.ListIds.Distinct().Count() != g.ListIds.Count ||
            g.Commands is null || g.Commands.Count > 10000 || g.Commands.Any(c => c is null || c.Length > 65536) ||
            g.DelayMilliseconds < 0 || g.LoopIntervalMilliseconds < 0 || g.LoopCount < 0 || !Enum.IsDefined(g.LineEnding)) ||
            package.Groups.Select(g => g.Id).Distinct().Count() != package.Groups.Count)
            throw new InvalidDataException("命令组身份、命令、执行参数或列表引用无效。");
    }
}
