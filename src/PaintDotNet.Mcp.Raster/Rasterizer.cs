using System.Drawing;
using System.Drawing.Text;
using PaintDotNet.Mcp.Protocol;

namespace PaintDotNet.Mcp.Raster;

public sealed class Rasterizer
{
    public IntRect Execute(Surface surface, IReadOnlyList<DrawOp> ops, byte[]? mask, Func<string, Surface?>? slots)
    {
        if (mask is not null && mask.Length != surface.Width * surface.Height)
        {
            throw new ArgumentException("Selection mask length does not match the surface.", nameof(mask));
        }

        int minX = surface.Width;
        int minY = surface.Height;
        int maxX = 0;
        int maxY = 0;
        var touched = false;

        void Note(int x, int y, int w, int h)
        {
            int left = Math.Clamp(x, 0, surface.Width);
            int top = Math.Clamp(y, 0, surface.Height);
            int right = Math.Clamp(x + w, 0, surface.Width);
            int bottom = Math.Clamp(y + h, 0, surface.Height);
            if (right <= left || bottom <= top)
            {
                return;
            }

            touched = true;
            minX = Math.Min(minX, left);
            minY = Math.Min(minY, top);
            maxX = Math.Max(maxX, right);
            maxY = Math.Max(maxY, bottom);
        }

        foreach (var op in ops)
        {
            switch (op)
            {
                case ClearOp clear:
                    Clear(surface, ColorHex.Parse(clear.Color), mask);
                    Note(0, 0, surface.Width, surface.Height);
                    break;
                case RectOp rect:
                    FillStrokeRect(surface, rect.X, rect.Y, rect.Width, rect.Height, 0, rect.Fill, rect.Stroke, rect.StrokeWidth, mask);
                    NoteBounds(rect.X, rect.Y, rect.Width, rect.Height, rect.Stroke is null ? 0 : rect.StrokeWidth, Note);
                    break;
                case RoundedRectOp round:
                    FillStrokeRect(surface, round.X, round.Y, round.Width, round.Height, round.Radius, round.Fill, round.Stroke, round.StrokeWidth, mask);
                    NoteBounds(round.X, round.Y, round.Width, round.Height, round.Stroke is null ? 0 : round.StrokeWidth, Note);
                    break;
                case EllipseOp ellipse:
                    DrawEllipse(surface, ellipse, mask);
                    NoteBounds(ellipse.X, ellipse.Y, ellipse.Width, ellipse.Height, ellipse.Stroke is null ? 0 : ellipse.StrokeWidth, Note);
                    break;
                case PolygonOp polygon:
                    DrawPolygon(surface, polygon, mask);
                    NotePoints(polygon.Points, polygon.Stroke is null ? 0 : polygon.StrokeWidth, Note);
                    break;
                case LineOp line:
                    DrawLine(surface, line, mask);
                    NotePoints(line.Points, line.Width, Note);
                    break;
                case BrushOp brush:
                    DrawBrush(surface, brush, mask);
                    NotePoints(brush.Points, brush.Diameter, Note);
                    break;
                case StampOp stamp:
                    DrawStamp(surface, stamp, slots, mask);
                    NoteStamp(surface, stamp, slots, Note);
                    break;
                case TextOp text:
                    DrawText(surface, text, mask, Note);
                    break;
                case BlitOp:
                case PixelsOp:
                    throw new InvalidOperationException("blit and pixels ops must be rewritten to stamp ops before rendering.");
                default:
                    throw new InvalidOperationException($"Unknown draw op {op.GetType().Name}.");
            }
        }

        if (!touched)
        {
            return new IntRect(0, 0, 0, 0);
        }

        return new IntRect(minX, minY, maxX - minX, maxY - minY);
    }

    private static void Clear(Surface surface, Rgba8 color, byte[]? mask)
    {
        if ((mask is null || mask.Length == 0) && color.A == 255)
        {
            surface.Fill(color);
            return;
        }

        for (int y = 0; y < surface.Height; y++)
        {
            for (int x = 0; x < surface.Width; x++)
            {
                float coverage = MaskAt(mask, surface, x, y);
                if (coverage >= 1 && color.A == 255)
                {
                    surface.Set(x, y, color);
                }
                else if (coverage > 0)
                {
                    surface.Blend(x, y, color, coverage);
                }
            }
        }
    }

    private static void FillStrokeRect(
        Surface surface,
        double x,
        double y,
        double width,
        double height,
        double radius,
        string? fill,
        string? stroke,
        double strokeWidth,
        byte[]? mask)
    {
        Rgba8? fillColor = fill is null ? null : ColorHex.Parse(fill);
        Rgba8? strokeColor = stroke is null ? null : ColorHex.Parse(stroke);
        float sw = strokeColor is null ? 0f : (float)Math.Max(0, strokeWidth);
        float left = (float)x;
        float top = (float)y;
        float right = (float)(x + width);
        float bottom = (float)(y + height);
        int x0 = Math.Max(0, (int)Math.Floor(left - sw));
        int y0 = Math.Max(0, (int)Math.Floor(top - sw));
        int x1 = Math.Min(surface.Width - 1, (int)Math.Ceiling(right + sw));
        int y1 = Math.Min(surface.Height - 1, (int)Math.Ceiling(bottom + sw));
        for (int py = y0; py <= y1; py++)
        {
            for (int px = x0; px <= x1; px++)
            {
                float cx = px + 0.5f;
                float cy = py + 0.5f;
                float fillCoverage = 0;
                float strokeCoverage = 0;
                if (radius <= 0)
                {
                    float outer = RectCoverage(left - (sw / 2f), top - (sw / 2f), right + (sw / 2f), bottom + (sw / 2f), cx, cy);
                    float inner = sw <= 0
                        ? outer
                        : RectCoverage(left + (sw / 2f), top + (sw / 2f), right - (sw / 2f), bottom - (sw / 2f), cx, cy);
                    fillCoverage = sw <= 0 ? outer : inner;
                    strokeCoverage = Math.Clamp(outer - inner, 0, 1);
                }
                else
                {
                    float sd = SdRoundRect(cx, cy, left, top, right, bottom, (float)radius);
                    fillCoverage = Math.Clamp(0.5f - sd, 0, 1);
                    if (sw > 0 && strokeColor is not null)
                    {
                        strokeCoverage = Math.Clamp((sw / 2f) + 0.5f - MathF.Abs(sd), 0, 1);
                        fillCoverage *= 1f - strokeCoverage;
                    }
                }

                float maskCoverage = MaskAt(mask, surface, px, py);
                if (fillColor is { } fc && fillCoverage > 0)
                {
                    surface.Blend(px, py, fc, fillCoverage * maskCoverage);
                }

                if (strokeColor is { } sc && strokeCoverage > 0)
                {
                    surface.Blend(px, py, sc, strokeCoverage * maskCoverage);
                }
            }
        }
    }

    private static void DrawEllipse(Surface surface, EllipseOp ellipse, byte[]? mask)
    {
        Rgba8? fill = ellipse.Fill is null ? null : ColorHex.Parse(ellipse.Fill);
        Rgba8? stroke = ellipse.Stroke is null ? null : ColorHex.Parse(ellipse.Stroke);
        float sw = (float)Math.Max(0, ellipse.StrokeWidth);
        float left = (float)ellipse.X;
        float top = (float)ellipse.Y;
        float rx = (float)ellipse.Width / 2f;
        float ry = (float)ellipse.Height / 2f;
        float cx = left + rx;
        float cy = top + ry;
        int x0 = Math.Max(0, (int)Math.Floor(left - sw));
        int y0 = Math.Max(0, (int)Math.Floor(top - sw));
        int x1 = Math.Min(surface.Width - 1, (int)Math.Ceiling(left + ellipse.Width + sw));
        int y1 = Math.Min(surface.Height - 1, (int)Math.Ceiling(top + ellipse.Height + sw));
        for (int py = y0; py <= y1; py++)
        {
            for (int px = x0; px <= x1; px++)
            {
                float sd = EllipseSd(px + 0.5f, py + 0.5f, cx, cy, rx, ry);
                float fillCoverage = Math.Clamp(0.5f - sd, 0, 1);
                float strokeCoverage = sw <= 0 || stroke is null ? 0 : Math.Clamp((sw / 2f) + 0.5f - MathF.Abs(sd), 0, 1);
                if (stroke is not null)
                {
                    fillCoverage *= 1f - strokeCoverage;
                }

                float m = MaskAt(mask, surface, px, py);
                if (fill is { } fc && fillCoverage > 0)
                {
                    surface.Blend(px, py, fc, fillCoverage * m);
                }

                if (stroke is { } sc && strokeCoverage > 0)
                {
                    surface.Blend(px, py, sc, strokeCoverage * m);
                }
            }
        }
    }

    private static void DrawPolygon(Surface surface, PolygonOp polygon, byte[]? mask)
    {
        var points = Pairs(polygon.Points);
        if (points.Count < 3)
        {
            throw new ArgumentException("A polygon needs at least three points.");
        }

        Rgba8? fill = polygon.Fill is null ? null : ColorHex.Parse(polygon.Fill);
        Rgba8? stroke = polygon.Stroke is null ? null : ColorHex.Parse(polygon.Stroke);
        float sw = (float)Math.Max(0, polygon.StrokeWidth);
        var bounds = PointBounds(points, sw + 1);
        Iterate(surface, bounds, (px, py) =>
        {
            float fillCoverage = fill is null ? 0 : PolygonCoverage(points, px + 0.5f, py + 0.5f);
            float strokeCoverage = stroke is null ? 0 : StrokeCoverage(points, px + 0.5f, py + 0.5f, sw, closed: true);
            float m = MaskAt(mask, surface, px, py);
            if (fill is { } fc && fillCoverage > 0)
            {
                surface.Blend(px, py, fc, fillCoverage * (1f - strokeCoverage) * m);
            }

            if (stroke is { } sc && strokeCoverage > 0)
            {
                surface.Blend(px, py, sc, strokeCoverage * m);
            }
        });
    }

    private static void DrawLine(Surface surface, LineOp line, byte[]? mask)
    {
        var points = Pairs(line.Points);
        if (points.Count < 2)
        {
            throw new ArgumentException("A line needs at least two points.");
        }

        var color = ColorHex.Parse(line.Color);
        float width = (float)Math.Max(1, line.Width);
        var bounds = PointBounds(points, width);
        Iterate(surface, bounds, (px, py) =>
        {
            float coverage = StrokeCoverage(points, px + 0.5f, py + 0.5f, width, closed: false) * MaskAt(mask, surface, px, py);
            if (coverage > 0)
            {
                surface.Blend(px, py, color, coverage);
            }
        });
    }

    private static void DrawBrush(Surface surface, BrushOp brush, byte[]? mask)
    {
        var points = Pairs(brush.Points);
        if (points.Count == 0)
        {
            throw new ArgumentException("A brush stroke needs at least one point.");
        }

        var color = ColorHex.Parse(brush.Color);
        float diameter = (float)Math.Max(1, brush.Diameter);
        float radius = diameter / 2f;
        float hardness = Math.Clamp((float)brush.Hardness, 0, 100) / 100f;
        float spacing = Math.Max(0.5f, diameter * Math.Clamp((float)brush.Spacing, 0.05f, 2f));
        var stamps = new List<(float X, float Y)>();
        var cursor = points[0];
        stamps.Add(cursor);
        float carried = 0;
        for (int i = 1; i < points.Count; i++)
        {
            var next = points[i];
            float dx = next.X - cursor.X;
            float dy = next.Y - cursor.Y;
            float length = MathF.Sqrt((dx * dx) + (dy * dy));
            if (length < 0.001f)
            {
                continue;
            }

            float traveled = carried;
            while (traveled + spacing <= length)
            {
                traveled += spacing;
                float t = traveled / length;
                var at = (X: cursor.X + (dx * t), Y: cursor.Y + (dy * t));
                stamps.Add(at);
            }

            carried = length - traveled;
            cursor = next;
        }

        foreach (var stamp in stamps)
        {
            int x0 = Math.Max(0, (int)Math.Floor(stamp.X - radius));
            int y0 = Math.Max(0, (int)Math.Floor(stamp.Y - radius));
            int x1 = Math.Min(surface.Width - 1, (int)Math.Ceiling(stamp.X + radius));
            int y1 = Math.Min(surface.Height - 1, (int)Math.Ceiling(stamp.Y + radius));
            float inner = radius * hardness;
            for (int py = y0; py <= y1; py++)
            {
                for (int px = x0; px <= x1; px++)
                {
                    float dx = (px + 0.5f) - stamp.X;
                    float dy = (py + 0.5f) - stamp.Y;
                    float dist = MathF.Sqrt((dx * dx) + (dy * dy));
                    float coverage = dist <= inner ? 1 : dist >= radius ? 0 : 1f - ((dist - inner) / Math.Max(0.001f, radius - inner));
                    coverage *= MaskAt(mask, surface, px, py);
                    if (coverage > 0)
                    {
                        surface.Blend(px, py, color, coverage);
                    }
                }
            }
        }
    }

    private static void DrawStamp(Surface surface, StampOp stamp, Func<string, Surface?>? slots, byte[]? mask)
    {
        var source = slots?.Invoke(stamp.Slot) ?? throw new InvalidOperationException($"Pixel slot '{stamp.Slot}' is not available.");
        bool replace = string.Equals(stamp.Blend, "replace", StringComparison.OrdinalIgnoreCase);
        if (!replace && !string.Equals(stamp.Blend, "src_over", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Blend '{stamp.Blend}' is not src_over or replace.");
        }

        int destX = (int)Math.Round(stamp.X);
        int destY = (int)Math.Round(stamp.Y);
        for (int y = 0; y < source.Height; y++)
        {
            int dy = destY + y;
            if ((uint)dy >= (uint)surface.Height)
            {
                continue;
            }

            for (int x = 0; x < source.Width; x++)
            {
                int dx = destX + x;
                if ((uint)dx >= (uint)surface.Width)
                {
                    continue;
                }

                float m = MaskAt(mask, surface, dx, dy);
                if (m <= 0)
                {
                    continue;
                }

                var pixel = source.Get(x, y);
                if (replace && m >= 1)
                {
                    surface.Set(dx, dy, pixel);
                }
                else if (replace)
                {
                    surface.Blend(dx, dy, pixel, m);
                }
                else
                {
                    surface.Blend(dx, dy, pixel, m);
                }
            }
        }
    }

    private static void DrawText(Surface surface, TextOp text, byte[]? mask, Action<int, int, int, int> note)
    {
        if (string.IsNullOrEmpty(text.Text))
        {
            return;
        }

        var color = ColorHex.Parse(text.Color);
        var rendered = RenderText(text.Text, text.FontFamily, (float)Math.Max(1, text.Size), color);
        int destX = (int)Math.Round(text.X);
        int destY = (int)Math.Round(text.Y);
        for (int y = 0; y < rendered.Height; y++)
        {
            int dy = destY + y;
            if ((uint)dy >= (uint)surface.Height)
            {
                continue;
            }

            for (int x = 0; x < rendered.Width; x++)
            {
                int dx = destX + x;
                if ((uint)dx >= (uint)surface.Width)
                {
                    continue;
                }

                float coverage = MaskAt(mask, surface, dx, dy);
                if (coverage > 0)
                {
                    surface.Blend(dx, dy, rendered.Get(x, y), coverage);
                }
            }
        }

        note(destX, destY, rendered.Width, rendered.Height);
    }

    private static Surface RenderText(string text, string fontFamily, float sizePx, Rgba8 color)
    {
        using var font = new Font(fontFamily, sizePx, FontStyle.Regular, GraphicsUnit.Pixel);
        using var measureBitmap = new Bitmap(1, 1, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var measure = Graphics.FromImage(measureBitmap);
        var size = measure.MeasureString(text, font);
        int width = Math.Max(1, (int)Math.Ceiling(size.Width));
        int height = Math.Max(1, (int)Math.Ceiling(size.Height));
        using var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        graphics.TextRenderingHint = TextRenderingHint.AntiAlias;
        using var brush = new SolidBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
        graphics.DrawString(text, font, brush, 0, 0);
        return SurfaceFromBitmap(bitmap);
    }

    private static Surface SurfaceFromBitmap(Bitmap bitmap)
    {
        var surface = new Surface(bitmap.Width, bitmap.Height);
        var bits = bitmap.LockBits(
            new Rectangle(0, 0, bitmap.Width, bitmap.Height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);
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

    private static void NoteBounds(double x, double y, double w, double h, double stroke, Action<int, int, int, int> note)
    {
        float pad = (float)Math.Ceiling(stroke) + 1;
        note((int)Math.Floor(x - pad), (int)Math.Floor(y - pad), (int)Math.Ceiling(w + (pad * 2)), (int)Math.Ceiling(h + (pad * 2)));
    }

    private static void NotePoints(IReadOnlyList<double> points, double pad, Action<int, int, int, int> note)
    {
        var pairs = Pairs(points);
        if (pairs.Count == 0)
        {
            return;
        }

        var bounds = PointBounds(pairs, (float)pad + 1);
        note(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    private static void NoteStamp(Surface surface, StampOp stamp, Func<string, Surface?>? slots, Action<int, int, int, int> note)
    {
        var source = slots?.Invoke(stamp.Slot);
        if (source is null)
        {
            return;
        }

        note((int)Math.Round(stamp.X), (int)Math.Round(stamp.Y), source.Width, source.Height);
        _ = surface;
    }

    private static float MaskAt(byte[]? mask, Surface surface, int x, int y)
    {
        if (mask is null)
        {
            return 1;
        }

        return mask[(y * surface.Width) + x] / 255f;
    }

    private static float RectCoverage(float left, float top, float right, float bottom, float px, float py)
    {
        if (right <= left || bottom <= top)
        {
            return 0;
        }

        return Axis(left, right, px) * Axis(top, bottom, py);
    }

    private static float Axis(float start, float end, float pixel)
    {
        float overlap = MathF.Min(pixel + 0.5f, end) - MathF.Max(pixel - 0.5f, start);
        return Math.Clamp(overlap, 0, 1);
    }

    private static float SdRoundRect(float px, float py, float left, float top, float right, float bottom, float radius)
    {
        float hx = MathF.Max(0, (right - left) * 0.5f);
        float hy = MathF.Max(0, (bottom - top) * 0.5f);
        radius = Math.Clamp(radius, 0, MathF.Min(hx, hy));
        float dx = MathF.Abs(px - ((left + right) * 0.5f)) - hx + radius;
        float dy = MathF.Abs(py - ((top + bottom) * 0.5f)) - hy + radius;
        float outside = MathF.Sqrt((MathF.Max(dx, 0) * MathF.Max(dx, 0)) + (MathF.Max(dy, 0) * MathF.Max(dy, 0)));
        return outside + MathF.Min(MathF.Max(dx, dy), 0) - radius;
    }

    private static float EllipseSd(float px, float py, float cx, float cy, float rx, float ry)
    {
        if (rx <= 0.001f || ry <= 0.001f)
        {
            return 1000;
        }

        float nx = (px - cx) / rx;
        float ny = (py - cy) / ry;
        return (MathF.Sqrt((nx * nx) + (ny * ny)) - 1f) * MathF.Min(rx, ry);
    }

    private static float PolygonCoverage(IReadOnlyList<(float X, float Y)> points, float px, float py)
    {
        ReadOnlySpan<(float X, float Y)> offsets = [(-0.25f, -0.25f), (0.25f, -0.25f), (-0.25f, 0.25f), (0.25f, 0.25f)];
        int inside = 0;
        foreach (var offset in offsets)
        {
            if (PointInPolygon(points, px + offset.X, py + offset.Y))
            {
                inside++;
            }
        }

        return inside / 4f;
    }

    private static bool PointInPolygon(IReadOnlyList<(float X, float Y)> points, float px, float py)
    {
        bool inside = false;
        for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
        {
            var a = points[i];
            var b = points[j];
            if (((a.Y > py) != (b.Y > py))
                && (px < ((b.X - a.X) * (py - a.Y) / ((b.Y - a.Y) == 0 ? 1e-6f : (b.Y - a.Y)) + a.X)))
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static float StrokeCoverage(IReadOnlyList<(float X, float Y)> points, float px, float py, float width, bool closed)
    {
        if (width <= 0 || points.Count < 2)
        {
            return 0;
        }

        float best = float.MaxValue;
        int last = closed ? points.Count : points.Count - 1;
        for (int i = 0; i < last; i++)
        {
            var a = points[i];
            var b = points[(i + 1) % points.Count];
            best = MathF.Min(best, DistanceToSegment(px, py, a.X, a.Y, b.X, b.Y));
        }

        float radius = width / 2f;
        return Math.Clamp(radius + 0.5f - best, 0, 1);
    }

    private static float DistanceToSegment(float px, float py, float ax, float ay, float bx, float by)
    {
        float dx = bx - ax;
        float dy = by - ay;
        float len2 = (dx * dx) + (dy * dy);
        float t = len2 <= 1e-6f ? 0 : Math.Clamp((((px - ax) * dx) + ((py - ay) * dy)) / len2, 0, 1);
        float cx = ax + (t * dx);
        float cy = ay + (t * dy);
        float ex = px - cx;
        float ey = py - cy;
        return MathF.Sqrt((ex * ex) + (ey * ey));
    }

    private static List<(float X, float Y)> Pairs(IReadOnlyList<double> points)
    {
        if (points.Count % 2 != 0)
        {
            throw new ArgumentException("Point lists must be flat x,y pairs.");
        }

        var list = new List<(float X, float Y)>(points.Count / 2);
        for (int i = 0; i < points.Count; i += 2)
        {
            list.Add(((float)points[i], (float)points[i + 1]));
        }

        return list;
    }

    private static IntRect PointBounds(IReadOnlyList<(float X, float Y)> points, float pad)
    {
        float minX = points.Min(p => p.X) - pad;
        float minY = points.Min(p => p.Y) - pad;
        float maxX = points.Max(p => p.X) + pad;
        float maxY = points.Max(p => p.Y) + pad;
        return new IntRect((int)Math.Floor(minX), (int)Math.Floor(minY), (int)Math.Ceiling(maxX - minX), (int)Math.Ceiling(maxY - minY));
    }

    private static void Iterate(Surface surface, IntRect bounds, Action<int, int> body)
    {
        int x0 = Math.Clamp(bounds.X, 0, surface.Width);
        int y0 = Math.Clamp(bounds.Y, 0, surface.Height);
        int x1 = Math.Clamp(bounds.X + bounds.Width, 0, surface.Width);
        int y1 = Math.Clamp(bounds.Y + bounds.Height, 0, surface.Height);
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                body(x, y);
            }
        }
    }
}
