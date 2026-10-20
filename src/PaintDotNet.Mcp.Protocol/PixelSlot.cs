using System.IO.MemoryMappedFiles;

namespace PaintDotNet.Mcp.Protocol;

public sealed class PixelSlot : IDisposable
{
    public const int HeaderBytes = 32;
    public const int Magic = 0x314E4450;

    private readonly MemoryMappedFile map;
    private bool disposed;

    public PixelSlot(MemoryMappedFile map, string name, int width, int height)
    {
        this.map = map;
        Name = name;
        Width = width;
        Height = height;
    }

    public string Name { get; }

    public int Width { get; }

    public int Height { get; }

    public static string MapName(int pid, string slot)
    {
        var cleaned = new string(slot.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
        if (cleaned.Length == 0)
        {
            cleaned = "slot";
        }

        return $@"Local\pdn-mcp-{pid}-{cleaned}";
    }

    public static PixelSlot Create(int pid, string slot, int width, int height, ReadOnlySpan<byte> bgra)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Pixel slot size must be positive.");
        }

        if (bgra.Length != width * height * 4)
        {
            throw new ArgumentException("BGRA buffer length does not match width and height.", nameof(bgra));
        }

        var name = MapName(pid, slot);
        long capacity = HeaderBytes + bgra.Length;
        var map = MemoryMappedFile.CreateNew(name, capacity, MemoryMappedFileAccess.ReadWrite);
        using (var view = map.CreateViewAccessor())
        {
            view.Write(0, Magic);
            view.Write(4, width);
            view.Write(8, height);
            view.Write(12, width * 4);
            view.Write(16, 1);
            view.Write(20, -1);
            view.WriteArray(HeaderBytes, bgra.ToArray(), 0, bgra.Length);
        }

        return new PixelSlot(map, name, width, height);
    }

    public static PixelSlot Open(string name)
    {
        var map = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
        using var view = map.CreateViewAccessor(0, HeaderBytes, MemoryMappedFileAccess.Read);
        int magic = view.ReadInt32(0);
        int width = view.ReadInt32(4);
        int height = view.ReadInt32(8);
        if (magic != Magic || width <= 0 || height <= 0)
        {
            map.Dispose();
            throw new InvalidDataException($"Pixel slot '{name}' has an unexpected header.");
        }

        return new PixelSlot(map, name, width, height);
    }

    public byte[] ReadPixels()
    {
        int count = Width * Height * 4;
        var data = new byte[count];
        using var view = map.CreateViewAccessor(HeaderBytes, count, MemoryMappedFileAccess.Read);
        view.ReadArray(0, data, 0, count);
        return data;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        map.Dispose();
    }
}
