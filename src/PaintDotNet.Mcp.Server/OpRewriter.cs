using System.Text.Json;
using PaintDotNet.Mcp.Protocol;
using PaintDotNet.Mcp.Raster;

namespace PaintDotNet.Mcp.Server;

public static class OpRewriter
{
    public static IReadOnlyList<DrawOp> Rewrite(string opsJson, int pid, ICollection<PixelSlot> slots)
    {
        List<DrawOp> ops;
        try
        {
            ops = JsonSerializer.Deserialize<List<DrawOp>>(opsJson, ProtocolJson.Options) ?? [];
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
        {
            throw new InvalidOperationException("ops is not a JSON array of draw operations. " + ex.Message);
        }

        var rewritten = new List<DrawOp>(ops.Count);
        foreach (var op in ops)
        {
            switch (op)
            {
                case BlitOp blit:
                    rewritten.Add(StampFile(pid, slots, Surface.FromPngFile(blit.Path), blit.X, blit.Y, blit.Blend, "blit"));
                    break;
                case PixelsOp pixels:
                    rewritten.Add(StampFile(pid, slots, Surface.FromRawBgra(pixels.Path, pixels.Width, pixels.Height), pixels.X, pixels.Y, "replace", "pixels"));
                    break;
                default:
                    rewritten.Add(op);
                    break;
            }
        }

        return rewritten;
    }

    private static StampOp StampFile(int pid, ICollection<PixelSlot> slots, Surface surface, double x, double y, string blend, string kind)
    {
        string slot = kind + "-" + Guid.NewGuid().ToString("N");
        slots.Add(PixelSlot.Create(pid, slot, surface.Width, surface.Height, surface.Pixels));
        return new StampOp(slot, x, y, blend);
    }
}
