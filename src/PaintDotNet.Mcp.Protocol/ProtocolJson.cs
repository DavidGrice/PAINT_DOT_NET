using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaintDotNet.Mcp.Protocol;

public static class ProtocolJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static byte[] Serialize(BridgeMessage message) =>
        JsonSerializer.SerializeToUtf8Bytes(message, Options);

    public static BridgeMessage Deserialize(ReadOnlySpan<byte> utf8)
    {
        var message = JsonSerializer.Deserialize<BridgeMessage>(utf8, Options);
        if (message is null)
        {
            throw new InvalidDataException("Bridge message was empty.");
        }

        return message;
    }
}
