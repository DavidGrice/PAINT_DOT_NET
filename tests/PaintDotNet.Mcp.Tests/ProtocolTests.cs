using System.Diagnostics;
using PaintDotNet.Mcp.Protocol;
using Xunit;

namespace PaintDotNet.Mcp.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void ColorHexAcceptsShortAndLongForms()
    {
        Assert.Equal(new Rgba8(0x11, 0x22, 0x33, 0xFF), ColorHex.Parse("#123"));
        Assert.Equal(new Rgba8(0x11, 0x22, 0x33, 0x80), ColorHex.Parse("#11223380"));
        Assert.False(ColorHex.TryParse("red", out _));
    }

    [Fact]
    public void FrameRoundTripPreservesABatch()
    {
        using var stream = new MemoryStream();
        BridgeMessage message = new StageBatchRequest(
            "b1",
            true,
            "token",
            [new RectOp(1, 2, 3, 4, "#FF0000", "#000000", 2)]);
        FrameCodec.Write(stream, message);
        stream.Position = 0;
        var read = (StageBatchRequest)FrameCodec.Read(stream);
        var rect = Assert.IsType<RectOp>(Assert.Single(read.Ops));
        Assert.Equal("b1", read.BatchId);
        Assert.True(read.IgnoreSelection);
        Assert.Equal(1, rect.X);
        Assert.Equal("#FF0000", rect.Fill);
        Assert.Equal(2, rect.StrokeWidth);
    }

    [Fact]
    public void DocumentTokenChangesWhenTheDownsampleChanges()
    {
        var pixels = new byte[2 * 2 * 4];
        var first = DocumentToken.Compute(2, 2, 1, 0, DocumentToken.Downsample32(2, 2, pixels));
        pixels[0] = 9;
        var second = DocumentToken.Compute(2, 2, 1, 0, DocumentToken.Downsample32(2, 2, pixels));
        Assert.NotEqual(first, second);
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public void InspectCompletesWhenTheEffectSideAnswers()
    {
        var mailbox = new BridgeMailbox();
        using var endpoint = new BridgeEndpoint(mailbox, () => "test", TimeSpan.FromSeconds(5));
        string pipe = "pdn-mcp-test-" + Guid.NewGuid().ToString("N");
        endpoint.Start(pipe);
        using var client = BridgeClient.Connect(pipe, TimeSpan.FromSeconds(5));
        var hello = client.Handshake(TimeSpan.FromSeconds(5));
        Assert.Equal(ProtocolConstants.Version, hello.Protocol);

        var response = client.RoundTrip(
            new PrepareInspectRequest("r1", false, false, null, null, 100),
            () =>
            {
                var wait = Stopwatch.StartNew();
                while (mailbox.PeekInspect() is null)
                {
                    if (wait.Elapsed > TimeSpan.FromSeconds(5))
                    {
                        throw new TimeoutException("Inspect request was not staged.");
                    }

                    Thread.Sleep(10);
                }

                mailbox.CompleteInspect(new InspectResponse("r1", true, null, null));
            },
            TimeSpan.FromSeconds(5));

        var inspect = Assert.IsType<InspectResponse>(response);
        Assert.True(inspect.Ok);
        Assert.Equal("r1", inspect.RequestId);
    }

    [Fact]
    public void PixelSlotRoundTrip()
    {
        int pid = Environment.ProcessId;
        string slot = "unit-" + Guid.NewGuid().ToString("N");
        var pixels = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        using (var created = PixelSlot.Create(pid, slot, 2, 1, pixels))
        {
            using var opened = PixelSlot.Open(created.Name);
            Assert.Equal(pixels, opened.ReadPixels());
        }
    }
}
