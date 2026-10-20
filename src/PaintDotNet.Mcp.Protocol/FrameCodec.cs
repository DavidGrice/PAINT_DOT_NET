using System.Buffers.Binary;

namespace PaintDotNet.Mcp.Protocol;

public static class FrameCodec
{
    public static void Write(Stream stream, BridgeMessage message)
    {
        var payload = ProtocolJson.Serialize(message);
        if (payload.Length > ProtocolConstants.MaxMessageBytes)
        {
            throw new InvalidOperationException(
                $"Bridge message is {payload.Length} bytes. The limit is {ProtocolConstants.MaxMessageBytes}.");
        }

        Span<byte> header = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        stream.Write(header);
        stream.Write(payload);
        stream.Flush();
    }

    public static BridgeMessage Read(Stream stream)
    {
        Span<byte> header = stackalloc byte[4];
        ReadExact(stream, header);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length < 2 || length > ProtocolConstants.MaxMessageBytes)
        {
            throw new InvalidDataException($"Bridge frame length {length} is outside the allowed range.");
        }

        var payload = new byte[length];
        ReadExact(stream, payload);
        return ProtocolJson.Deserialize(payload);
    }

    private static void ReadExact(Stream stream, Span<byte> buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer[read..]);
            if (n == 0)
            {
                throw new EndOfStreamException("The Paint.NET bridge closed the pipe.");
            }

            read += n;
        }
    }
}
