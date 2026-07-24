using System.Text.Json;

namespace SerialLog.Cli;

public static class OtaDeviceStatusParser
{
    public static bool TryParse(
        string payload,
        out uint deviceId,
        out bool online)
    {
        deviceId = 0;
        online = false;
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (!root.TryGetProperty("cmd", out var command)
                || !command.TryGetInt32(out var commandValue)
                || commandValue != 201
                || !root.TryGetProperty("dev_id", out var device)
                || !device.TryGetUInt32(out deviceId)
                || deviceId == 0
                || !root.TryGetProperty("status", out var status)
                || !status.TryGetInt32(out var statusValue)
                || statusValue is < 0 or > 1)
            {
                deviceId = 0;
                return false;
            }

            online = statusValue == 1;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
