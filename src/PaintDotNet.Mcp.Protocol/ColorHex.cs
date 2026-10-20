namespace PaintDotNet.Mcp.Protocol;

public readonly record struct Rgba8(byte R, byte G, byte B, byte A)
{
    public static Rgba8 Opaque(byte r, byte g, byte b) => new(r, g, b, 255);
}

public static class ColorHex
{
    public static bool TryParse(string? text, out Rgba8 color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var s = text.Trim();
        if (s[0] != '#')
        {
            return false;
        }

        s = s[1..];
        if (s.Length == 3)
        {
            s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2], "FF");
        }
        else if (s.Length == 6)
        {
            s += "FF";
        }
        else if (s.Length != 8)
        {
            return false;
        }

        if (!byte.TryParse(s.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            || !byte.TryParse(s.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            || !byte.TryParse(s.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b)
            || !byte.TryParse(s.AsSpan(6, 2), System.Globalization.NumberStyles.HexNumber, null, out var a))
        {
            return false;
        }

        color = new Rgba8(r, g, b, a);
        return true;
    }

    public static Rgba8 Parse(string text)
    {
        if (!TryParse(text, out var color))
        {
            throw new FormatException($"Color '{text}' is not #RGB, #RRGGBB, or #RRGGBBAA.");
        }

        return color;
    }

    public static string Format(Rgba8 color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}{color.A:X2}";
}
