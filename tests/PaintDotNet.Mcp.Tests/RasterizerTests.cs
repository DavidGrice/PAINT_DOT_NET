using PaintDotNet.Mcp.Protocol;
using PaintDotNet.Mcp.Raster;
using Xunit;

namespace PaintDotNet.Mcp.Tests;

public sealed class RasterizerTests
{
    private readonly Rasterizer rasterizer = new();

    [Fact]
    public void ClearFillsEveryPixel()
    {
        var surface = new Surface(4, 4);
        rasterizer.Execute(surface, [new ClearOp("#336699")], null, null);
        Assert.Equal(new Rgba8(0x33, 0x66, 0x99, 0xFF), surface.Get(0, 0));
        Assert.Equal(new Rgba8(0x33, 0x66, 0x99, 0xFF), surface.Get(3, 3));
    }

    [Fact]
    public void RectFillIsExactInsideAndLeavesTheOutsideAlone()
    {
        var surface = new Surface(8, 8);
        surface.Fill(new Rgba8(255, 255, 255, 255));
        rasterizer.Execute(surface, [new RectOp(2, 2, 3, 3, "#FF0000", null)], null, null);
        Assert.Equal(new Rgba8(255, 0, 0, 255), surface.Get(2, 2));
        Assert.Equal(new Rgba8(255, 0, 0, 255), surface.Get(4, 4));
        Assert.Equal(new Rgba8(255, 255, 255, 255), surface.Get(1, 1));
        Assert.Equal(new Rgba8(255, 255, 255, 255), surface.Get(5, 5));
    }

    [Fact]
    public void SelectionMaskBlocksAPixel()
    {
        var surface = new Surface(2, 1);
        surface.Fill(new Rgba8(255, 255, 255, 255));
        var mask = new byte[] { 0, 255 };
        rasterizer.Execute(surface, [new ClearOp("#0000FF")], mask, null);
        Assert.Equal(new Rgba8(255, 255, 255, 255), surface.Get(0, 0));
        Assert.Equal(new Rgba8(0, 0, 255, 255), surface.Get(1, 0));
    }

    [Fact]
    public void EllipseCenterIsFilledAndTheFarCornerIsEmpty()
    {
        var surface = new Surface(21, 21);
        rasterizer.Execute(surface, [new EllipseOp(0, 0, 21, 21, "#00FF00", null)], null, null);
        Assert.Equal(255, surface.Get(10, 10).A);
        Assert.Equal(0, surface.Get(10, 10).R);
        Assert.Equal(255, surface.Get(10, 10).G);
        Assert.Equal(0, surface.Get(0, 0).A);
    }

    [Fact]
    public void SrcOverStampBlendsTranslucentRedOntoWhite()
    {
        var surface = new Surface(1, 1);
        surface.Fill(new Rgba8(255, 255, 255, 255));
        var stamp = new Surface(1, 1);
        stamp.Set(0, 0, new Rgba8(255, 0, 0, 128));
        var dirty = rasterizer.Execute(
            surface,
            [new StampOp("red", 0, 0, "src_over")],
            null,
            slot => slot == "red" ? stamp : null);
        var pixel = surface.Get(0, 0);
        Assert.Equal(255, pixel.A);
        Assert.InRange(pixel.R, 180, 255);
        Assert.InRange(pixel.G, 80, 200);
        Assert.Equal(new IntRect(0, 0, 1, 1), dirty);
    }

    [Fact]
    public void BrushCenterIsOpaque()
    {
        var surface = new Surface(11, 11);
        rasterizer.Execute(
            surface,
            [new BrushOp([5, 5], "#000000", 5, 100, 0.25)],
            null,
            null);
        Assert.Equal(new Rgba8(0, 0, 0, 255), surface.Get(5, 5));
        Assert.Equal(0, surface.Get(0, 0).A);
    }

    [Fact]
    public void PngRoundTripPreservesASolidPixel()
    {
        var surface = new Surface(2, 2);
        surface.Set(1, 1, new Rgba8(1, 2, 3, 255));
        var path = Path.Combine(Path.GetTempPath(), "pdn-mcp-pixel-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            surface.SavePng(path);
            var loaded = Surface.FromPngFile(path);
            Assert.Equal(new Rgba8(1, 2, 3, 255), loaded.Get(1, 1));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
