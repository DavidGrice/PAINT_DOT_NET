using System.Security.Cryptography;

namespace PaintDotNet.Mcp.Protocol;

public static class DocumentToken
{
    public static byte[] Downsample32(int width, int height, ReadOnlySpan<byte> bgra)
    {
        if (bgra.Length != width * height * 4)
        {
            throw new ArgumentException("Pixel buffer length does not match the image size.", nameof(bgra));
        }

        var dst = new byte[32 * 32 * 4];
        for (int y = 0; y < 32; y++)
        {
            int sy = height <= 1 ? 0 : y * (height - 1) / 31;
            for (int x = 0; x < 32; x++)
            {
                int sx = width <= 1 ? 0 : x * (width - 1) / 31;
                int src = ((sy * width) + sx) * 4;
                int di = ((y * 32) + x) * 4;
                dst[di] = bgra[src];
                dst[di + 1] = bgra[src + 1];
                dst[di + 2] = bgra[src + 2];
                dst[di + 3] = bgra[src + 3];
            }
        }

        return dst;
    }

    public static string Compute(int width, int height, int layerCount, int activeLayer, ReadOnlySpan<byte> downsample32)
    {
        var buffer = new byte[16 + downsample32.Length];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), height);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), layerCount);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(12), activeLayer);
        downsample32.CopyTo(buffer.AsSpan(16));
        return Sha256(buffer);
    }

    public static string Sha256(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
