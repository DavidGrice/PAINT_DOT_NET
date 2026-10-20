using System.Drawing;
using System.Drawing.Imaging;
using PaintDotNet.Mcp.Protocol;

namespace PaintDotNet.Mcp.Raster;

public sealed class Surface
{
    public Surface(int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Surface size must be positive.");
        }

        Width = width;
        Height = height;
        Pixels = new byte[width * height * 4];
    }

    public Surface(int width, int height, byte[] bgra)
    {
        if (bgra.Length != width * height * 4)
        {
            throw new ArgumentException("BGRA buffer length does not match width and height.", nameof(bgra));
        }

        Width = width;
        Height = height;
        Pixels = bgra;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }

    public Rgba8 Get(int x, int y)
    {
        int i = Index(x, y);
        return new Rgba8(Pixels[i + 2], Pixels[i + 1], Pixels[i], Pixels[i + 3]);
    }

    public void Set(int x, int y, Rgba8 color)
    {
        int i = Index(x, y);
        Pixels[i] = color.B;
        Pixels[i + 1] = color.G;
        Pixels[i + 2] = color.R;
        Pixels[i + 3] = color.A;
    }

    public void Blend(int x, int y, Rgba8 source, float coverage)
    {
        if (coverage <= 0)
        {
            return;
        }

        Set(x, y, Over(Get(x, y), source, coverage));
    }

    public void Fill(Rgba8 color)
    {
        for (int i = 0; i < Pixels.Length; i += 4)
        {
            Pixels[i] = color.B;
            Pixels[i + 1] = color.G;
            Pixels[i + 2] = color.R;
            Pixels[i + 3] = color.A;
        }
    }

    public Surface Clone()
    {
        var copy = new byte[Pixels.Length];
        Pixels.AsSpan().CopyTo(copy);
        return new Surface(Width, Height, copy);
    }

    public Surface Crop(IntRect rect)
    {
        int x = Math.Clamp(rect.X, 0, Width);
        int y = Math.Clamp(rect.Y, 0, Height);
        int right = Math.Clamp(rect.X + rect.Width, 0, Width);
        int bottom = Math.Clamp(rect.Y + rect.Height, 0, Height);
        int w = Math.Max(1, right - x);
        int h = Math.Max(1, bottom - y);
        var dst = new Surface(w, h);
        for (int row = 0; row < h; row++)
        {
            Pixels.AsSpan((((y + row) * Width) + x) * 4, w * 4).CopyTo(dst.Pixels.AsSpan(row * w * 4, w * 4));
        }

        return dst;
    }

    public Surface FitLongEdge(int maxEdge)
    {
        int longEdge = Math.Max(Width, Height);
        if (maxEdge <= 0 || longEdge <= maxEdge)
        {
            return Clone();
        }

        double scale = (double)maxEdge / longEdge;
        int w = Math.Max(1, (int)Math.Round(Width * scale));
        int h = Math.Max(1, (int)Math.Round(Height * scale));
        var dst = new Surface(w, h);
        for (int y = 0; y < h; y++)
        {
            int sy = Math.Clamp((int)((y + 0.5) * Height / h), 0, Height - 1);
            for (int x = 0; x < w; x++)
            {
                int sx = Math.Clamp((int)((x + 0.5) * Width / w), 0, Width - 1);
                dst.Set(x, y, Get(sx, sy));
            }
        }

        return dst;
    }

    public void Stamp(Surface source, int destX, int destY, bool replace)
    {
        for (int y = 0; y < source.Height; y++)
        {
            int dy = destY + y;
            if ((uint)dy >= (uint)Height)
            {
                continue;
            }

            for (int x = 0; x < source.Width; x++)
            {
                int dx = destX + x;
                if ((uint)dx >= (uint)Width)
                {
                    continue;
                }

                var pixel = source.Get(x, y);
                if (replace)
                {
                    Set(dx, dy, pixel);
                }
                else
                {
                    Blend(dx, dy, pixel, 1f);
                }
            }
        }
    }

    public void SavePng(string path)
    {
        using var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        var bits = bitmap.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(Pixels, y * Width * 4, bits.Scan0 + (y * bits.Stride), Width * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(bits);
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        bitmap.Save(path, ImageFormat.Png);
    }

    public static Surface FromPngFile(string path)
    {
        using var loaded = new Bitmap(path);
        return FromBitmap(loaded);
    }

    public static Surface FromRawBgra(string path, int width, int height)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length != width * height * 4)
        {
            throw new InvalidDataException($"Raw pixel file '{path}' is {data.Length} bytes. Expected {width * height * 4}.");
        }

        return new Surface(width, height, data);
    }

    public static Rgba8 Over(Rgba8 dst, Rgba8 src, float coverage)
    {
        if (coverage <= 0)
        {
            return dst;
        }

        if (coverage > 1)
        {
            coverage = 1;
        }

        float sa = src.A / 255f * coverage;
        float da = dst.A / 255f;
        float outA = sa + (da * (1f - sa));
        if (outA <= 0.0001f)
        {
            return new Rgba8(0, 0, 0, 0);
        }

        float r = ((src.R / 255f * sa) + (dst.R / 255f * da * (1f - sa))) / outA;
        float g = ((src.G / 255f * sa) + (dst.G / 255f * da * (1f - sa))) / outA;
        float b = ((src.B / 255f * sa) + (dst.B / 255f * da * (1f - sa))) / outA;
        return new Rgba8(Channel(r), Channel(g), Channel(b), Channel(outA));
    }

    private int Index(int x, int y)
    {
        if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(nameof(x), $"Pixel {x},{y} is outside {Width}x{Height}.");
        }

        return ((y * Width) + x) * 4;
    }

    private static byte Channel(float value) => (byte)Math.Clamp((int)Math.Round(value * 255f), 0, 255);

    private static Surface FromBitmap(Bitmap loaded)
    {
        using var bitmap = new Bitmap(loaded.Width, loaded.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.DrawImage(loaded, 0, 0, loaded.Width, loaded.Height);
        }

        var surface = new Surface(bitmap.Width, bitmap.Height);
        var bits = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < bitmap.Height; y++)
            {
                System.Runtime.InteropServices.Marshal.Copy(bits.Scan0 + (y * bits.Stride), surface.Pixels, y * bitmap.Width * 4, bitmap.Width * 4);
            }
        }
        finally
        {
            bitmap.UnlockBits(bits);
        }

        return surface;
    }
}
