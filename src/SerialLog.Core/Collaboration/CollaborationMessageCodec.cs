using System.Text.Json;
using System.Text.Json.Serialization;

namespace SerialLog.Core.Collaboration;

public static class CollaborationMessageCodec
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Encode(CollaborationMessage message)
    {
        return JsonSerializer.Serialize(message, Options);
    }

    public static CollaborationMessage Decode(string line)
    {
        var message = JsonSerializer.Deserialize<CollaborationMessage>(line.TrimEnd(), Options);
        if (message is null)
        {
            throw new InvalidOperationException("协作消息为空。");
        }

        if (message.ProtocolVersion != CollaborationProtocol.CurrentVersion)
        {
            throw new InvalidOperationException(
                $"协作协议版本不兼容：收到 {message.ProtocolVersion}，当前 {CollaborationProtocol.CurrentVersion}。");
        }

        return message;
    }
}
