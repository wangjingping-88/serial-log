using System.Text.Json;
using System.Text.Json.Serialization;

namespace SerialLog.Core.Configuration;

public static class WorkspaceConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static WorkspaceConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            return new WorkspaceConfig();
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<WorkspaceConfig>(json, JsonOptions) ?? new WorkspaceConfig();
    }

    public static void Save(string path, WorkspaceConfig config)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(path, json);
    }
}
