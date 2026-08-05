using System.Text.Json;

namespace SerialLog.Update;

public sealed class UpdateStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _path;

    public UpdateStateStore(string path)
    {
        _path = path;
    }

    public UpdateState Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new UpdateState();
            }

            return JsonSerializer.Deserialize<UpdateState>(File.ReadAllText(_path), JsonOptions) ??
                new UpdateState();
        }
        catch
        {
            return new UpdateState();
        }
    }

    public void Save(UpdateState state)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = $"{_path}.tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(temporaryPath, _path, true);
    }

    public void ClearPendingUpdate()
    {
        var state = Load();
        state.PendingUpdate = null;
        Save(state);
    }
}
