using System.Text.Json;

namespace SerialLog.Cli;

public static class OtaUpgradeCommandBuilder
{
    public static string Build(
        EcoLinkOtaTargetConfig target,
        EcoLinkOtaPackage package)
    {
        var ota = new Dictionary<string, object?>
        {
            ["active"] = 1,
            ["type"] = target.UpgradeType,
            ["range"] = target.Range,
            ["new_ver"] = target.NewVersion,
            ["old_ver"] = target.OldVersion,
            ["dev_type"] = target.DevType,
            ["md5"] = package.Md5,
            ["net"] = new Dictionary<string, object>
            {
                ["access"] = 1,
                ["addr"] = package.DownloadUri.AbsoluteUri,
                ["file"] = package.FileName
            }
        };

        if (target.Range == 1 || target.DeviceIds.Length > 0)
        {
            ota["iote"] = target.DeviceIds;
        }

        if (target.SessionId != 0)
        {
            ota["session_id"] = target.SessionId;
        }

        if (target.NodeIds.Length > 0)
        {
            ota["nodes"] = target.NodeIds;
        }

        var root = new Dictionary<string, object>
        {
            ["cmd"] = 5,
            ["ver"] = "v2.0",
            ["src"] = 0,
            ["dst"] = 0,
            ["ota"] = ota
        };
        return JsonSerializer.Serialize(root, JsonOptions);
    }

    public static string BuildVersionQuery(IEnumerable<uint> deviceIds)
    {
        var ids = deviceIds.ToArray();
        var root = new Dictionary<string, object>
        {
            ["cmd"] = 150,
            ["ver"] = "v2.0",
            ["src"] = 0
        };
        if (ids.Length > 0)
        {
            root["dst"] = ids;
        }

        return JsonSerializer.Serialize(root, JsonOptions);
    }

    private static JsonSerializerOptions JsonOptions => new()
    {
        WriteIndented = false
    };
}
