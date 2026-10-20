using PaintDotNet.Mcp.Protocol;

namespace PaintDotNet.Mcp.Server;

public sealed record ToolReport
{
    public string Status { get; init; } = "ok";

    public string? Message { get; init; }

    public int? Pid { get; init; }

    public string? PdnVersion { get; init; }

    public string? PluginVersion { get; init; }

    public string? WindowTitle { get; init; }

    public bool? ModalOpen { get; init; }

    public string? ModalText { get; init; }

    public bool? InspectReverted { get; init; }

    public DocumentSnapshot? Document { get; init; }

    public IReadOnlyList<InstanceReport>? Instances { get; init; }

    public string? ImagePath { get; init; }

    public string? PreviewPath { get; init; }

    public int? ImageWidth { get; init; }

    public int? ImageHeight { get; init; }

    public string? DirtyHash { get; init; }

    public IntRect? DirtyBounds { get; init; }

    public string? PluginPath { get; init; }
}

public sealed record InstanceReport(int Pid, string? PdnVersion, string? PluginVersion, bool PluginInstalled);
