using System.Text.Json.Serialization;

namespace PaintDotNet.Mcp.Protocol;

public readonly record struct IntRect(int X, int Y, int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public long Area => (long)Math.Max(0, Width) * Math.Max(0, Height);
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HandshakeRequest), "handshake")]
[JsonDerivedType(typeof(HandshakeResponse), "handshakeResult")]
[JsonDerivedType(typeof(PrepareInspectRequest), "prepareInspect")]
[JsonDerivedType(typeof(InspectResponse), "inspectResult")]
[JsonDerivedType(typeof(StageBatchRequest), "stageBatch")]
[JsonDerivedType(typeof(BatchResponse), "batchResult")]
[JsonDerivedType(typeof(ErrorResponse), "error")]
public abstract record BridgeMessage;

public sealed record HandshakeRequest(int Protocol) : BridgeMessage;

public sealed record HandshakeResponse(int Protocol, string PdnVersion, string PluginVersion) : BridgeMessage;

public sealed record PrepareInspectRequest(
    string RequestId,
    bool IncludeComposite,
    bool IncludeActiveLayer,
    int? LayerIndex,
    IntRect? Region,
    int MaxPixels) : BridgeMessage;

public sealed record InspectResponse(
    string RequestId,
    bool Ok,
    string? Error,
    DocumentSnapshot? Snapshot) : BridgeMessage;

public sealed record StageBatchRequest(
    string BatchId,
    bool IgnoreSelection,
    string? ExpectedToken,
    IReadOnlyList<DrawOp> Ops) : BridgeMessage;

public sealed record BatchResponse(
    string BatchId,
    bool Committed,
    string? Error,
    DocumentSnapshot? Snapshot,
    string? DirtyHash,
    IntRect? DirtyBounds) : BridgeMessage;

public sealed record ErrorResponse(string Code, string Message) : BridgeMessage;

public sealed record DocumentSnapshot(
    int Width,
    int Height,
    double DpiX,
    double DpiY,
    string DpiUnits,
    int ActiveLayerIndex,
    IReadOnlyList<LayerSnapshot> Layers,
    IntRect SelectionBounds,
    bool SelectionCoversDocument,
    string PrimaryColor,
    string SecondaryColor,
    long Generation,
    string DocumentToken,
    IReadOnlyList<PixelPlane> Planes);

public sealed record LayerSnapshot(
    int Index,
    string Name,
    bool Visible,
    double Opacity,
    string BlendMode);

public sealed record PixelPlane(
    string Kind,
    int? LayerIndex,
    string Slot,
    int Width,
    int Height);

public sealed record DiscoveryRecord(
    int Pid,
    string Pipe,
    int Protocol,
    string PdnVersion,
    string PluginVersion,
    string[] Effects);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(ClearOp), "clear")]
[JsonDerivedType(typeof(RectOp), "rect")]
[JsonDerivedType(typeof(RoundedRectOp), "rounded_rect")]
[JsonDerivedType(typeof(EllipseOp), "ellipse")]
[JsonDerivedType(typeof(PolygonOp), "polygon")]
[JsonDerivedType(typeof(LineOp), "line")]
[JsonDerivedType(typeof(BrushOp), "brush")]
[JsonDerivedType(typeof(StampOp), "stamp")]
[JsonDerivedType(typeof(TextOp), "text")]
[JsonDerivedType(typeof(BlitOp), "blit")]
[JsonDerivedType(typeof(PixelsOp), "pixels")]
public abstract record DrawOp;

public sealed record ClearOp(string Color) : DrawOp;

public sealed record RectOp(
    double X,
    double Y,
    double Width,
    double Height,
    string? Fill,
    string? Stroke,
    double StrokeWidth = 1) : DrawOp;

public sealed record RoundedRectOp(
    double X,
    double Y,
    double Width,
    double Height,
    double Radius,
    string? Fill,
    string? Stroke,
    double StrokeWidth = 1) : DrawOp;

public sealed record EllipseOp(
    double X,
    double Y,
    double Width,
    double Height,
    string? Fill,
    string? Stroke,
    double StrokeWidth = 1) : DrawOp;

public sealed record PolygonOp(
    IReadOnlyList<double> Points,
    string? Fill,
    string? Stroke,
    double StrokeWidth = 1) : DrawOp;

public sealed record LineOp(IReadOnlyList<double> Points, string Color, double Width) : DrawOp;

public sealed record BrushOp(
    IReadOnlyList<double> Points,
    string Color,
    double Diameter,
    double Hardness = 100,
    double Spacing = 0.25) : DrawOp;

public sealed record StampOp(string Slot, double X, double Y, string Blend) : DrawOp;

public sealed record TextOp(
    string Text,
    double X,
    double Y,
    string Color,
    string FontFamily = "Segoe UI",
    double Size = 16) : DrawOp;

/// <summary>Tool-facing image stamp. The server rewrites this to <see cref="StampOp"/> before it reaches the plugin.</summary>
public sealed record BlitOp(string Path, double X, double Y, string Blend = "src_over") : DrawOp;

/// <summary>Tool-facing raw BGRA rectangle. The server rewrites this to <see cref="StampOp"/>.</summary>
public sealed record PixelsOp(string Path, int X, int Y, int Width, int Height) : DrawOp;
