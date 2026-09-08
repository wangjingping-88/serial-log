using System.Text.Json;
using System.Text.Json.Serialization;

namespace SerialLog.Core.Configuration;

/// <summary>应用配置容器；运行状态不进入配置文件。</summary>
public sealed class WorkspaceCatalog
{
    public int FormatVersion { get; set; } = 2;
    public string CurrentWorkspaceId { get; set; } = string.Empty;
    public List<TestWorkspaceConfig> Workspaces { get; set; } = [];
}

public sealed class TestWorkspaceConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "默认测试";
    public WorkspaceConfig Configuration { get; set; } = new() { PageCount = 1 };
}

public static class WorkspaceCatalogStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static WorkspaceCatalog Load(string path)
    {
        if (!File.Exists(path))
        {
            var initial = new TestWorkspaceConfig();
            return new WorkspaceCatalog { CurrentWorkspaceId = initial.Id, Workspaces = [initial] };
        }

        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty(nameof(WorkspaceCatalog.FormatVersion), out _))
        {
            var catalog = JsonSerializer.Deserialize<WorkspaceCatalog>(json, Options)
                ?? throw new InvalidDataException("工作区配置为空，原文件已保留。");
            Validate(catalog);
            return catalog;
        }

        var legacy = JsonSerializer.Deserialize<WorkspaceConfig>(json, Options)
            ?? throw new InvalidDataException("旧工作区配置无法读取，原文件已保留。");
        foreach (var window in legacy.SerialWindows)
            window.IsShared = false;
        var migrated = new TestWorkspaceConfig { Configuration = legacy };
        var result = new WorkspaceCatalog { CurrentWorkspaceId = migrated.Id, Workspaces = [migrated] };
        Validate(result);
        // 先创建不可覆盖的原始备份；备份或原子替换失败时不使用默认配置覆盖原文件。
        File.Copy(path, $"{path}.legacy-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.bak");
        Save(path, result);
        return result;
    }

    public static void Save(string path, WorkspaceCatalog catalog)
    {
        Validate(catalog);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, catalog, Options);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(fullPath)) File.Replace(temporary, fullPath, null);
            else File.Move(temporary, fullPath);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static TestWorkspaceConfig Clone(TestWorkspaceConfig source, string name)
    {
        var configuration = JsonSerializer.Deserialize<WorkspaceConfig>(
            JsonSerializer.Serialize(source.Configuration, Options), Options)!;
        var ids = configuration.SerialWindows.ToDictionary(w => w.Id, _ => Guid.NewGuid().ToString("N"));
        foreach (var window in configuration.SerialWindows)
        {
            window.Id = ids[window.Id];
            window.IsShared = false;
        }
        foreach (var group in configuration.CommandGroups)
            group.TargetIds = group.TargetIds.Select(id => ids.GetValueOrDefault(id, id)).ToList();
        configuration.ExpandedWindowIds = configuration.ExpandedWindowIds.Where(ids.ContainsKey).Select(id => ids[id]).ToList();
        return new TestWorkspaceConfig { Name = name, Configuration = configuration };
    }

    private static void Validate(WorkspaceCatalog catalog)
    {
        if (catalog.FormatVersion != 2 || catalog.Workspaces is null || catalog.Workspaces.Count == 0 ||
            catalog.Workspaces.Any(w => !Guid.TryParseExact(w.Id, "N", out _) || string.IsNullOrWhiteSpace(w.Name) || w.Configuration is null) ||
            catalog.Workspaces.Select(w => w.Id).Distinct(StringComparer.Ordinal).Count() != catalog.Workspaces.Count ||
            !catalog.Workspaces.Any(w => w.Id == catalog.CurrentWorkspaceId))
            throw new InvalidDataException("工作区配置版本、身份或当前工作区无效，原文件已保留。");

        foreach (var workspace in catalog.Workspaces)
        {
            var c = workspace.Configuration;
            if (c.SerialWindows is null || c.CommandGroups is null || c.CommandHistory is null || c.AtCommandSets is null ||
                c.ExpandedWindowIds is null || c.Subscriptions is null ||
                c.SerialWindows.Any(w => w is null || string.IsNullOrWhiteSpace(w.Id)) ||
                c.SerialWindows.Select(w => w.Id).Distinct(StringComparer.Ordinal).Count() != c.SerialWindows.Count ||
                c.CommandGroups.Any(g => g is null || g.TargetIds is null || g.Commands is null) ||
                c.AtCommandSets.Any(s => s is null || s.Commands is null) ||
                c.Subscriptions.Any(s => s is null || s.Source is null || s.Window is null ||
                    string.IsNullOrWhiteSpace(s.Source.PcId) || string.IsNullOrWhiteSpace(s.Window.Id)))
                throw new InvalidDataException($"工作区“{workspace.Name}”内容不完整，原文件已保留。");
        }
    }
}
