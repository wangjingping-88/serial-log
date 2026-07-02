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
        return message ?? throw new InvalidOperationException("协作消息为空。");
    }
}
