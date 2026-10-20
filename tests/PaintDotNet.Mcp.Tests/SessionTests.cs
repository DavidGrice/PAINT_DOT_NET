using PaintDotNet.Mcp.Protocol;
using PaintDotNet.Mcp.Raster;
using PaintDotNet.Mcp.Server;
using PaintDotNet.Mcp.UiDriver;
using Xunit;

namespace PaintDotNet.Mcp.Tests;

public sealed class SessionTests
{
    [Fact]
    public async Task StatusReportsNotRunningWhenNothingIsOpen()
    {
        using var session = new PaintSession(new FakeDriver(), () => [], () => []);
        var report = await session.Status(null, 5, CancellationToken.None);
        Assert.Equal("not_running", report.Status);
    }

    [Fact]
    public async Task StatusReportsAMissingPlugin()
    {
        using var session = new PaintSession(new FakeDriver(), () => [], () => [4]);
        var report = await session.Status(4, 5, CancellationToken.None);
        Assert.Equal("plugin_not_installed", report.Status);
        Assert.False(string.IsNullOrWhiteSpace(report.PluginPath));
    }

    [Fact]
    public async Task LayerDeleteWithoutConfirmDoesNotTouchTheUi()
    {
        var driver = new FakeDriver();
        using var session = new PaintSession(
            driver,
            () => [new DiscoveryRecord(7, "unused", 1, "5.1", "0.1.0", ["MCP Inspect", "MCP Apply"])],
            () => [7]);
        var report = await session.LayerDelete(7, null, false, 5, CancellationToken.None);
        Assert.Equal("error", report.Status);
        Assert.Empty(driver.Calls);
    }

    [Fact]
    public async Task DocumentInfoReadsThePluginResponse()
    {
        var mailbox = new BridgeMailbox();
        using var endpoint = new BridgeEndpoint(mailbox, () => "5.1.12");
        string pipe = "pdn-mcp-test-" + Guid.NewGuid().ToString("N");
        endpoint.Start(pipe);
        var driver = new FakeDriver { Mailbox = mailbox };
        using var session = new PaintSession(
            driver,
            () => [new DiscoveryRecord(7, pipe, 1, "5.1.12", "0.1.0", ["MCP Inspect", "MCP Apply"])],
            () => [7]);

        var report = await session.DocumentInfo(7, null, 10, CancellationToken.None);
        Assert.Equal("ok", report.Status);
        Assert.NotNull(report.Document);
        Assert.Equal(8, report.Document.Width);
        Assert.Equal("Background", report.Document.Layers[0].Name);
    }
}

public sealed class RewriterTests
{
    [Fact]
    public void BlitAndPixelsBecomeStampSlots()
    {
        var directory = Path.Combine(Path.GetTempPath(), "pdn-mcp-rewrite-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string png = Path.Combine(directory, "stamp.png");
            string raw = Path.Combine(directory, "pixels.bgra");
            var image = new Surface(1, 1);
            image.Set(0, 0, new Rgba8(1, 2, 3, 255));
            image.SavePng(png);
            File.WriteAllBytes(raw, [4, 5, 6, 255]);
            string json = $$"""
                [
                  {"op":"blit","path":{{Json(png)}},"x":1,"y":2,"blend":"src_over"},
                  {"op":"pixels","path":{{Json(raw)}},"x":3,"y":4,"width":1,"height":1},
                  {"op":"clear","color":"#FFFFFF"}
                ]
                """;
            var slots = new List<PixelSlot>();
            try
            {
                var ops = OpRewriter.Rewrite(json, 9, slots);
                Assert.Equal(3, ops.Count);
                var blit = Assert.IsType<StampOp>(ops[0]);
                var pixels = Assert.IsType<StampOp>(ops[1]);
                Assert.IsType<ClearOp>(ops[2]);
                Assert.Equal("src_over", blit.Blend);
                Assert.Equal("replace", pixels.Blend);
                using var opened = PixelSlot.Open(PixelSlot.MapName(9, blit.Slot));
                Assert.Equal(1, opened.Width);
            }
            finally
            {
                foreach (var slot in slots)
                {
                    slot.Dispose();
                }
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);
}

file sealed class FakeDriver : IPaintDriver
{
    public List<string> Calls { get; } = [];

    public BridgeMailbox? Mailbox { get; set; }

    public UiState ReadState(int pid) => new("Untitled - paint.net", false, null);

    public string DumpTree(int pid) => string.Empty;

    public void InvokeEffect(int pid, string effectName)
    {
        Calls.Add(effectName);
        var until = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < until)
        {
            if (effectName == "MCP Inspect" && Mailbox?.PeekInspect() is { } request)
            {
                Mailbox.CompleteInspect(new InspectResponse(request.RequestId, true, null, Sample()));
                return;
            }

            if (effectName == "MCP Apply" && Mailbox?.PeekBatch() is { } batch)
            {
                Mailbox.CompleteBatch(new BatchResponse(batch.BatchId, true, null, Sample(), null, null));
                return;
            }

            Thread.Sleep(10);
        }
    }

    public void DismissEffectDialog(int pid, bool commit)
    {
    }

    public void PressEscape(int pid) => Calls.Add("escape");

    public int? HistoryCount(int pid) => null;

    public void Undo(int pid) => Calls.Add("undo");

    public void Redo(int pid) => Calls.Add("redo");

    public void NewDocument(int pid, int width, int height) => Calls.Add("new");

    public void OpenDocument(int pid, string path) => Calls.Add("open");

    public bool Save(int pid) => true;

    public void SaveAs(int pid, string path, bool overwriteConfirmed) => Calls.Add("saveas");

    public ClosePrompt CloseDocument(int pid, bool save, bool discard) => ClosePrompt.Closed;

    public void AddLayer(int pid) => Calls.Add("add");

    public void DeleteLayer(int pid) => Calls.Add("delete");

    public void DuplicateLayer(int pid) => Calls.Add("duplicate");

    public void MergeDown(int pid) => Calls.Add("merge");

    public void LayerStep(int pid, bool pageUp) => Calls.Add(pageUp ? "up" : "down");

    public void SetLayerProperties(int pid, string? currentName, string? name, double? opacity, bool? visible, string? blendMode) =>
        Calls.Add("properties");

    public void SelectAll(int pid) => Calls.Add("all");

    public void SelectNone(int pid) => Calls.Add("none");

    public void InvertSelection(int pid) => Calls.Add("invert");

    public void CropToSelection(int pid) => Calls.Add("crop");

    public void SetColor(int pid, bool primary, string hex) => Calls.Add("color");

    public void ActivateDocument(int pid, string title) => Calls.Add("activate");

    public string? ModalText(int pid) => null;

    private static DocumentSnapshot Sample() => new(
        8,
        8,
        96,
        96,
        "Inch",
        0,
        [new LayerSnapshot(0, "Background", true, 1, "Normal")],
        new IntRect(0, 0, 8, 8),
        true,
        "#FF000000",
        "#FFFFFFFF",
        2,
        "token",
        []);
}
