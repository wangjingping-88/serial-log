using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SerialLog.Core.Configuration;

public sealed class WorkspacePackage
{
    public string Format { get; set; } = "SerialLog.Workspace";
    public int Version { get; set; } = 1;
    public TestWorkspaceConfig Workspace { get; set; } = new();
}

public static class WorkspacePackageCodec
{
    public const int MaxBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }, MaxDepth = 64
    };

    public static string Export(TestWorkspaceConfig source)
    {
        var snapshot = JsonSerializer.Deserialize<TestWorkspaceConfig>(JsonSerializer.Serialize(source, Options), Options)!;
        SanitizeAndValidate(snapshot);
        var json = JsonSerializer.Serialize(new WorkspacePackage { Workspace = snapshot }, Options);
        CheckSize(json); return json;
    }

    public static TestWorkspaceConfig Parse(string json)
    {
        CheckSize(json);
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("format", out _) || !document.RootElement.TryGetProperty("version", out _) ||
                !document.RootElement.TryGetProperty("workspace", out _))
                throw new InvalidDataException("不是独立工作区快照。");
            var package = JsonSerializer.Deserialize<WorkspacePackage>(json, Options)!;
            if (package.Format != "SerialLog.Workspace" || package.Version != 1 || package.Workspace is null)
                throw new InvalidDataException("工作区快照格式或版本不兼容。");
            SanitizeAndValidate(package.Workspace);
            return package.Workspace;
        }
        catch (JsonException ex) { throw new InvalidDataException("工作区快照 JSON 无效。", ex); }
        catch (Exception ex) when (ex is ArgumentException or NullReferenceException or InvalidOperationException or OverflowException)
        { throw new InvalidDataException("工作区快照内容不完整或参数无效。", ex); }
    }

    public static TestWorkspaceConfig PrepareImport(string json, string name, string localPcId, string localPcName, string logRoot)
    {
        var snapshot = Parse(json);
        var clone = WorkspaceCatalogStore.Clone(snapshot, name);
        clone.Configuration.LocalPcId = localPcId; clone.Configuration.LocalPcName = localPcName;
        clone.Configuration.LogRootDirectory = logRoot;
        return clone;
    }

    private static void CheckSize(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > MaxBytes)
            throw new InvalidDataException("工作区快照为空或超过 4 MiB。");
    }

    private static void SanitizeAndValidate(TestWorkspaceConfig snapshot)
    {
        var c = snapshot.Configuration ?? throw new InvalidDataException("工作区配置缺失。");
        if (snapshot.Name is null || snapshot.Name.Length > 256 || c.SerialWindows is null || c.SerialWindows.Count > 200 ||
            c.SerialWindows.Any(w => w is null || string.IsNullOrWhiteSpace(w.Id) || w.Title is null || w.Title.Length > 256 || w.BaudRate <= 0) ||
            c.ShortcutBindings is null || c.ShortcutBindings.Any(s => s is null || s.ActionId is null || s.Gesture is null) ||
            c.YmodemProfiles is null || c.YmodemProfiles.Any(p => p is null))
            throw new InvalidDataException("工作区窗口、快捷键或规则配置无效。");
        // 只交换可复用的测试配置，设备映射与本机路径由接收方决定。
        c.WorkspaceMode = WorkspaceMode.Local; c.LocalPcId = ""; c.LocalPcName = "";
        c.HostAddress = "127.0.0.1"; c.HostPort = 58730;
        c.LogRootDirectory = ApplicationDataPaths.LogDirectory;
        c.AtCommandFilePath = null; c.YmodemFilePath = ""; c.Subscriptions = [];
        foreach (var window in c.SerialWindows)
        {
            window.PortName = null; window.IsShared = false;
            window.OwnerPcId = ""; window.OwnerPcName = ""; window.OwnerPcColor = "";
        }
        WorkspaceCatalogStore.Validate(new() { CurrentWorkspaceId = snapshot.Id, Workspaces = [snapshot] });
        // 公用执行参数校验，目标关系只保留此快照内部的本地窗口。
        CommandGroupPackageCodec.Export(c.CommandGroups, c.CommandGroupLists);
        var localIds = c.SerialWindows.Select(w => w.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var group in c.CommandGroups) group.TargetIds = group.TargetIds.Where(localIds.Contains).Distinct().ToList();
        if (c.CommandHistory.Any(text => text is null) || c.AtCommandSets.Any(set => set.Name is null || set.Commands.Any(text => text is null)))
            throw new InvalidDataException("工作区命令内容无效。");
    }
}
