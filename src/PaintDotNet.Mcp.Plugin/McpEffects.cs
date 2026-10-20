using System.Windows.Forms;
using PaintDotNet.Effects;
using PaintDotNet.Imaging;
using PaintDotNet.Mcp.Protocol;
using PaintDotNet.Mcp.Raster;
using PaintDotNet.Rendering;
using RasterSurface = PaintDotNet.Mcp.Raster.Surface;

namespace PaintDotNet.Mcp.Plugin;

public abstract class McpEffectBase : BitmapEffect
{
    private readonly string effectName;
    private AutoCloseForm? form;
    private RasterSurface? cache;

    protected McpEffectBase(string effectName)
        : base(effectName, "MCP", BitmapEffectOptions.Create() with { IsConfigurable = true })
    {
        this.effectName = effectName;
        BridgeRuntime.EnsureStarted();
        BridgeRuntime.Log("ctor " + effectName);
    }

    protected override IEffectConfigForm OnCreateConfigForm()
    {
        BridgeRuntime.Log("OnCreateConfigForm " + effectName);
        cache = null;
        form = new AutoCloseForm(effectName);
        BridgeRuntime.Log("OnCreateConfigForm created " + effectName);
        return form;
    }

    protected override void OnInitializeRenderInfo(IBitmapEffectRenderInfo renderInfo)
    {
        BridgeRuntime.Log("OnInitializeRenderInfo " + effectName);
        try
        {
            WaitForMailbox(TimeSpan.FromSeconds(2));
            var flags = BitmapEffectRenderingFlags.SingleThreaded
                | BitmapEffectRenderingFlags.FirstTileIsRenderedWithBarrier;
            if (WantsFullLayer())
            {
                flags |= BitmapEffectRenderingFlags.DisableSelectionClipping;
            }

            renderInfo.Flags = flags;
            renderInfo.OutputPixelFormat = PixelFormats.Bgra32;
            base.OnInitializeRenderInfo(renderInfo);
            BridgeRuntime.Log("OnInitializeRenderInfo done " + effectName);
        }
        catch (Exception ex)
        {
            BridgeRuntime.Log("OnInitializeRenderInfo failed " + ex);
            throw;
        }
    }

    protected override void OnRender(IBitmapEffectOutput output)
    {
        BridgeRuntime.Log("OnRender " + effectName);
        try
        {
            cache ??= Build(form ?? throw new InvalidOperationException("MCP dialog is missing."));
            PixelTransfer.WriteTile(cache, output);
        }
        catch (Exception ex)
        {
            BridgeRuntime.Log(ex.ToString());
            form?.RequestClose(DialogResult.Cancel);
            throw;
        }
    }

    protected abstract bool WantsFullLayer();

    protected abstract bool MailboxReady();

    protected abstract RasterSurface Build(AutoCloseForm dialog);

    private static void WaitForMailbox(Func<bool> ready, TimeSpan timeout)
    {
        var start = System.Environment.TickCount64;
        while (!ready())
        {
            if (System.Environment.TickCount64 - start >= timeout.TotalMilliseconds)
            {
                return;
            }

            Thread.Sleep(10);
        }
    }

    private void WaitForMailbox(TimeSpan timeout) => WaitForMailbox(MailboxReady, timeout);

    protected RasterSurface ReadSource()
    {
        int width = Environment.Document.Size.Width;
        int height = Environment.Document.Size.Height;
        using var bitmap = Environment.GetSourceBitmapBgra32();
        return PixelTransfer.Read(bitmap, width, height);
    }

    protected string Identity()
    {
        var doc = Environment.Document;
        var names = doc.Layers.Select(layer => layer.Name ?? string.Empty);
        return $"{doc.Size.Width}x{doc.Size.Height}:{string.Join("|", names)}";
    }
}

public sealed class McpInspectEffect : McpEffectBase
{
    public McpInspectEffect()
        : base("MCP Inspect")
    {
    }

    protected override bool WantsFullLayer() => false;

    protected override bool MailboxReady() => BridgeRuntime.Mailbox.PeekInspect() is not null;

    protected override RasterSurface Build(AutoCloseForm dialog)
    {
        var request = BridgeRuntime.Mailbox.PeekInspect();
        var source = ReadSource();
        try
        {
            if (request is null)
            {
                return source;
            }

            var snapshot = SnapshotBuilder.Capture(Environment, request, source, BridgeRuntime.Observe(Identity()), publishPlanes: true);
            BridgeRuntime.Mailbox.CompleteInspect(new InspectResponse(request.RequestId, true, null, snapshot));
            return source;
        }
        catch (Exception ex)
        {
            BridgeRuntime.Log(ex.ToString());
            if (request is not null)
            {
                BridgeRuntime.Mailbox.CompleteInspect(new InspectResponse(request.RequestId, false, ex.Message, null));
            }

            return source;
        }
        finally
        {
            dialog.RequestClose(DialogResult.Cancel);
        }
    }
}

public sealed class McpApplyEffect : McpEffectBase
{
    public McpApplyEffect()
        : base("MCP Apply")
    {
    }

    protected override bool WantsFullLayer() =>
        BridgeRuntime.Mailbox.PeekBatch()?.IgnoreSelection == true;

    protected override bool MailboxReady() => BridgeRuntime.Mailbox.PeekBatch() is not null;

    protected override RasterSurface Build(AutoCloseForm dialog)
    {
        var batch = BridgeRuntime.Mailbox.PeekBatch();
        var source = ReadSource();
        if (batch is null || batch.Ops.Count == 0)
        {
            if (batch is not null)
            {
                BridgeRuntime.Mailbox.CompleteBatch(new BatchResponse(batch.BatchId, false, "empty", null, null, null));
            }

            dialog.RequestClose(DialogResult.Cancel);
            return source;
        }

        try
        {
            var before = SnapshotBuilder.Capture(Environment, null, source, BridgeRuntime.CurrentGeneration, publishPlanes: false);
            if (batch.ExpectedToken is not null
                && !string.Equals(batch.ExpectedToken, before.DocumentToken, StringComparison.Ordinal))
            {
                BridgeRuntime.Mailbox.CompleteBatch(new BatchResponse(batch.BatchId, false, "conflict", before, null, null));
                dialog.RequestClose(DialogResult.Cancel);
                return source;
            }

            var edited = source.Clone();
            var dirty = new Rasterizer().Execute(edited, batch.Ops, null, BridgeRuntime.OpenSlot);
            var snapshot = SnapshotBuilder.Capture(Environment, null, edited, BridgeRuntime.Commit(Identity()), publishPlanes: false);
            string? hash = dirty.IsEmpty ? null : HashRegion(edited, dirty);
            BridgeRuntime.Mailbox.CompleteBatch(new BatchResponse(batch.BatchId, true, null, snapshot, hash, dirty));
            dialog.RequestClose(DialogResult.OK);
            return edited;
        }
        catch (Exception ex)
        {
            BridgeRuntime.Log(ex.ToString());
            BridgeRuntime.Mailbox.CompleteBatch(new BatchResponse(batch.BatchId, false, ex.Message, null, null, null));
            dialog.RequestClose(DialogResult.Cancel);
            return source;
        }
    }

    private static string HashRegion(RasterSurface surface, IntRect rect)
    {
        var crop = surface.Crop(rect);
        return DocumentToken.Sha256(crop.Pixels);
    }
}

public sealed class McpEffectConfigToken : EffectConfigToken
{
    public override object Clone() => new McpEffectConfigToken();
}

public sealed class AutoCloseForm : EffectConfigForm
{
    public AutoCloseForm(string title)
    {
        BridgeRuntime.Log("form ctor " + title);
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(360, 72);
        MaximizeBox = false;
        MinimizeBox = false;
        Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Text = "Paint.NET MCP",
        });
    }

    // EffectConfigForm.OnLoad throws a deferred NotImplementedException unless these
    // three methods are overridden. The empty token type is private to Paint.NET.
    protected override EffectConfigToken OnCreateInitialToken()
    {
        BridgeRuntime.Log("OnCreateInitialToken");
        return new McpEffectConfigToken();
    }

    protected override void OnUpdateDialogFromToken(EffectConfigToken token)
    {
        BridgeRuntime.Log("OnUpdateDialogFromToken");
    }

    protected override void OnUpdateTokenFromDialog(EffectConfigToken dstToken)
    {
        BridgeRuntime.Log("OnUpdateTokenFromDialog");
    }

    protected override void OnLoaded()
    {
        BridgeRuntime.Log("OnLoaded");
        base.OnLoaded();
    }

    public void RequestClose(DialogResult result)
    {
        void CloseNow()
        {
            DialogResult = result;
            Close();
        }

        BridgeRuntime.Log("RequestClose " + result + " handle=" + IsHandleCreated);
        if (!IsHandleCreated)
        {
            HandleCreated += (_, _) => BeginInvoke(CloseNow);
            return;
        }

        BeginInvoke(CloseNow);
    }
}

internal static class PixelTransfer
{
    public static RasterSurface Read(IEffectInputBitmap<ColorBgra32> bitmap, int width, int height)
    {
        using var locker = bitmap.Lock(new RectInt32(0, 0, width, height));
        var region = locker.AsRegionPtr();
        var surface = new RasterSurface(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                ColorBgra32 pixel = region[x, y];
                surface.Set(x, y, new Rgba8(pixel.R, pixel.G, pixel.B, pixel.A));
            }
        }

        return surface;
    }

    public static void WriteTile(RasterSurface surface, IBitmapEffectOutput output)
    {
        RectInt32 bounds = output.Bounds;
        using var locker = output.LockBgra32();
        var region = locker.AsRegionPtr();
        for (int y = 0; y < bounds.Height; y++)
        {
            int sourceY = bounds.Y + y;
            for (int x = 0; x < bounds.Width; x++)
            {
                int sourceX = bounds.X + x;
                if ((uint)sourceX >= (uint)surface.Width || (uint)sourceY >= (uint)surface.Height)
                {
                    continue;
                }

                var pixel = surface.Get(sourceX, sourceY);
                region[x, y] = ColorBgra32.FromBgra(pixel.B, pixel.G, pixel.R, pixel.A);
            }
        }
    }
}

internal static class SnapshotBuilder
{
    public static DocumentSnapshot Capture(
        IBitmapEffectEnvironment environment,
        PrepareInspectRequest? request,
        RasterSurface activeLayer,
        long generation,
        bool publishPlanes)
    {
        var document = environment.Document;
        int width = document.Size.Width;
        int height = document.Size.Height;
        var classic = (IEffectEnvironment)environment;
        var layers = new List<LayerSnapshot>(document.Layers.Count);
        for (int i = 0; i < document.Layers.Count; i++)
        {
            var layer = document.Layers[i];
            layers.Add(new LayerSnapshot(i, layer.Name ?? string.Empty, layer.Visible, layer.Opacity, layer.BlendMode.ToString()));
        }

        var bounds = environment.Selection.RenderBounds;
        var selection = new IntRect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        bool covers = selection.X <= 0 && selection.Y <= 0 && selection.Width >= width && selection.Height >= height;
        var downsample = DocumentToken.Downsample32(activeLayer.Width, activeLayer.Height, activeLayer.Pixels);
        string token = DocumentToken.Compute(width, height, layers.Count, classic.SourceLayerIndex, downsample);
        var planes = new List<PixelPlane>();
        if (publishPlanes && request is not null)
        {
            BridgeRuntime.ReleaseSlots();
            Publish(request, document, activeLayer, classic.SourceLayerIndex, planes);
        }

        return new DocumentSnapshot(
            width,
            height,
            document.Resolution.X,
            document.Resolution.Y,
            document.Resolution.Units.ToString(),
            classic.SourceLayerIndex,
            layers,
            selection,
            covers,
            Format(classic.PrimaryColor),
            Format(classic.SecondaryColor),
            generation,
            token,
            planes);
    }

    private static void Publish(
        PrepareInspectRequest request,
        IBitmapEffectDocumentInfo document,
        RasterSurface activeLayer,
        int activeIndex,
        List<PixelPlane> planes)
    {
        if (request.IncludeActiveLayer)
        {
            AddPlane(request, planes, "active", activeIndex, activeLayer);
        }

        if (request.IncludeComposite)
        {
            using var bitmap = document.GetBitmapBgra32();
            var composite = PixelTransfer.Read(bitmap, document.Size.Width, document.Size.Height);
            AddPlane(request, planes, "composite", null, composite);
        }

        if (request.LayerIndex is int index && index >= 0 && index < document.Layers.Count)
        {
            using var bitmap = document.Layers[index].GetBitmapBgra32();
            var layer = PixelTransfer.Read(bitmap, document.Size.Width, document.Size.Height);
            AddPlane(request, planes, "layer", index, layer);
        }
    }

    private static void AddPlane(PrepareInspectRequest request, List<PixelPlane> planes, string kind, int? layerIndex, RasterSurface surface)
    {
        var cropped = request.Region is { } region ? surface.Crop(region) : surface;
        if (cropped.Width * cropped.Height > Math.Max(1, request.MaxPixels))
        {
            throw new InvalidOperationException(
                $"The requested image is {cropped.Width}x{cropped.Height}, above the {request.MaxPixels} pixel cap. Pass a smaller region or raise maxPixels.");
        }

        string slot = $"{kind}-{request.RequestId}";
        if (layerIndex is int index)
        {
            slot += "-" + index;
        }

        var map = PixelSlot.Create(System.Environment.ProcessId, slot, cropped.Width, cropped.Height, cropped.Pixels);
        BridgeRuntime.Hold(map);
        planes.Add(new PixelPlane(kind, layerIndex, map.Name, cropped.Width, cropped.Height));
    }

    private static string Format(ColorBgra32 color) =>
        ColorHex.Format(new Rgba8(color.R, color.G, color.B, color.A));
}
