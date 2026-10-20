using PaintDotNet.Mcp.Protocol;
using PaintDotNet.Mcp.Raster;
using PaintDotNet.Mcp.UiDriver;

var sessions = SessionFiles.ReadAlive();
Console.Error.WriteLine("Paint.NET MCP spike");
Console.Error.WriteLine("Plugin folder: " + SessionFiles.UserPluginDirectory);
Console.Error.WriteLine("Sessions: " + sessions.Count);
if (sessions.Count == 0)
{
    Console.Error.WriteLine("FAIL no discovery file. Install the plugin, restart Paint.NET, and run this again.");
    return 2;
}

var session = sessions[0];
Console.Error.WriteLine($"pid {session.Pid} pipe {session.Pipe} pdn {session.PdnVersion} plugin {session.PluginVersion}");
using var client = BridgeClient.Connect(session.Pipe, TimeSpan.FromSeconds(5));
var hello = client.Handshake(TimeSpan.FromSeconds(5));
Console.Error.WriteLine($"PASS pipe handshake protocol {hello.Protocol} product {hello.PdnVersion}");

using var driver = new FlaUiPaintDriver();
int? historyBefore = Try(() => driver.HistoryCount(session.Pid));
Console.Error.WriteLine("History count before inspect: " + (historyBefore?.ToString() ?? "unavailable"));

var inspect = Round(client, driver, session.Pid, "MCP Inspect", new PrepareInspectRequest(
    Guid.NewGuid().ToString("N"),
    false,
    true,
    null,
    null,
    ProtocolConstants.DefaultMaxPixels));
if (inspect is not InspectResponse first || !first.Ok || first.Snapshot is null)
{
    Console.Error.WriteLine("FAIL inspect " + Describe(inspect));
    return 1;
}

Console.Error.WriteLine($"PASS inspect {first.Snapshot.Width}x{first.Snapshot.Height} layers {first.Snapshot.Layers.Count} active {first.Snapshot.ActiveLayerIndex}");
foreach (var layer in first.Snapshot.Layers)
{
    Console.Error.WriteLine($"  layer {layer.Index} '{layer.Name}' visible={layer.Visible} opacity={layer.Opacity} blend={layer.BlendMode}");
}

int? historyAfter = Try(() => driver.HistoryCount(session.Pid));
if (historyBefore is int before && historyAfter is int after && after > before)
{
    driver.Undo(session.Pid);
    Console.Error.WriteLine("NOTE inspect added a history entry. One undo was sent. inspectReverted would be true.");
}
else if (historyBefore is null)
{
    Console.Error.WriteLine("NOTE history count was not readable, so a silent inspect commit could not be checked.");
}
else
{
    Console.Error.WriteLine("PASS inspect left the history count unchanged.");
}

if (args.Contains("--dump"))
{
    Console.Error.WriteLine(driver.DumpTree(session.Pid));
}

if (!args.Contains("--draw"))
{
    Console.Error.WriteLine("Skip the red rectangle unless you pass --draw.");
    return 0;
}

var batch = new StageBatchRequest(
    Guid.NewGuid().ToString("N"),
    true,
    first.Snapshot.DocumentToken,
    [new RectOp(0, 0, 10, 10, "#FF0000", null)]);
var applied = Round(client, driver, session.Pid, "MCP Apply", batch);
if (applied is not BatchResponse done || !done.Committed)
{
    Console.Error.WriteLine("FAIL apply " + Describe(applied));
    return 1;
}

Console.Error.WriteLine("PASS apply committed. Dirty " + done.DirtyBounds);
var readBack = Round(client, driver, session.Pid, "MCP Inspect", new PrepareInspectRequest(
    Guid.NewGuid().ToString("N"),
    false,
    true,
    null,
    new IntRect(0, 0, 10, 10),
    1000));
if (readBack is InspectResponse second && second.Ok && second.Snapshot?.Planes.Count > 0)
{
    using var slot = PixelSlot.Open(second.Snapshot.Planes[0].Slot);
    var surface = new Surface(slot.Width, slot.Height, slot.ReadPixels());
    var pixel = surface.Get(0, 0);
    Console.Error.WriteLine($"Read-back pixel (0,0) is R={pixel.R} G={pixel.G} B={pixel.B} A={pixel.A}");
    Console.Error.WriteLine(pixel.R > 200 && pixel.G < 40 && pixel.B < 40 ? "PASS red rectangle" : "FAIL pixel is not red");
}

var empty = Round(client, driver, session.Pid, "MCP Apply", new StageBatchRequest(Guid.NewGuid().ToString("N"), false, null, []));
Console.Error.WriteLine(empty is BatchResponse { Committed: false }
    ? "PASS empty apply did not commit"
    : "FAIL empty apply " + Describe(empty));
return 0;

static BridgeMessage Round(BridgeClient client, FlaUiPaintDriver driver, int pid, string effect, BridgeMessage request)
{
    var response = client.RoundTrip(request, () => driver.InvokeEffect(pid, effect), TimeSpan.FromSeconds(40));
    try
    {
        driver.DismissEffectDialog(pid, response is BatchResponse { Committed: true });
    }
    catch (PaintUiException ex)
    {
        Console.Error.WriteLine("Dismiss: " + ex.Message);
    }

    return response;
}

static int? Try(Func<int?> action)
{
    try
    {
        return action();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("History: " + ex.Message);
        return null;
    }
}

static string Describe(BridgeMessage message) => message switch
{
    ErrorResponse error => error.Code + " " + error.Message,
    InspectResponse inspect => inspect.Error ?? "inspect",
    BatchResponse batch => batch.Error ?? "batch",
    _ => message.GetType().Name,
};
