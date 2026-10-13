using System.Text.Json;
using System.Text.Json.Serialization;

namespace SerialLog.Core.Configuration;

public static class WorkspaceConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new Firmware.YmodemModeConverter(), new JsonStringEnumConverter() }
    };

    public static WorkspaceConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            return new WorkspaceConfig();
        }

        var json = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<WorkspaceConfig>(json, JsonOptions) ?? throw new InvalidDataException("工作区配置为空。");
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty(nameof(WorkspaceConfig.YmodemProfiles), out _))
        {
            File.Copy(path, $"{path}.ymodem-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.bak");
            Save(path, config);
        }
        return config;
    }

    public static void Save(string path, WorkspaceConfig config)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        foreach (var profile in config.YmodemProfiles) Firmware.YmodemRuleValidator.Validate(profile);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, config, JsonOptions); stream.Flush(true); }
            if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
