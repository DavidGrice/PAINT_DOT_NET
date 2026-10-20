using System.ComponentModel;
using ModelContextProtocol.Server;
using PaintDotNet.Mcp.Protocol;

namespace PaintDotNet.Mcp.Server;

[McpServerToolType]
public sealed class PaintTools(PaintSession session)
{
    [McpServerTool(Name = "pdn_status", ReadOnly = true, Title = "Paint.NET status")]
    [Description("Lists running Paint.NET instances and whether the MCP plugin is loaded. Does not launch Paint.NET. If several instances are open, pass pid.")]
    public Task<ToolReport> Status(int? pid = null, int timeoutSeconds = 30, CancellationToken cancellationToken = default) =>
        session.Status(pid, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "document_info", ReadOnly = true, Title = "Document info")]
    [Description("Reads the active document through MCP Inspect. Returns size, resolution, layers, selection bounds, colors, generation, and documentToken. Layer index is the order of this list. Coordinates are document pixels, origin top-left, x right, y down. Colors are #RRGGBBAA.")]
    public Task<ToolReport> DocumentInfo(int? pid = null, string? document = null, int timeoutSeconds = 45, CancellationToken cancellationToken = default) =>
        session.DocumentInfo(pid, document, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "document_snapshot", ReadOnly = true, Title = "Document snapshot")]
    [Description("Writes a PNG of the composite, the active layer, or one layer, plus a preview whose long edge is at most 512 pixels. The image long edge is at most 1024 unless full is true. kind is composite, active, or layer. Pass layerIndex only for kind=layer. The index is the order document_info returns. A region is x, y, width, height in document pixels. Planes over 16 megapixels need allowLarge, and planes over 80 megapixels are refused.")]
    public Task<ToolReport> DocumentSnapshot(
        string kind = "composite",
        int? layerIndex = null,
        int? x = null,
        int? y = null,
        int? width = null,
        int? height = null,
        bool full = false,
        bool allowLarge = false,
        int? pid = null,
        string? document = null,
        int timeoutSeconds = 45,
        CancellationToken cancellationToken = default)
    {
        IntRect? region = null;
        if (x is not null || y is not null || width is not null || height is not null)
        {
            if (x is null || y is null || width is null || height is null)
            {
                return Task.FromResult(new ToolReport { Status = "error", Message = "Pass all of x, y, width, and height for a region, or none of them." });
            }

            region = new IntRect(x.Value, y.Value, width.Value, height.Value);
        }

        return session.DocumentSnapshot(pid, document, kind, layerIndex, region, full, allowLarge, timeoutSeconds, cancellationToken);
    }

    [McpServerTool(Name = "draw_batch", Title = "Draw a batch")]
    [Description("Draws into the active layer as one History entry named MCP Apply. Pass expectedToken from document_info. A mismatch returns conflict and does not draw. The selection clips the result unless ignoreSelection is true. Coordinates are document pixels, origin top-left. Colors are #RGB, #RRGGBB, or #RRGGBBAA. ops is a JSON array. Each object has op set to clear, rect, rounded_rect, ellipse, polygon, line, brush, blit, pixels, or text. text is rasterized with GDI+ and is not a Paint.NET text object. blit is a PNG path. pixels is a raw BGRA file plus width and height. Drawing uses normal src-over into the layer's stored pixels.")]
    public Task<ToolReport> DrawBatch(
        string ops,
        string expectedToken,
        bool ignoreSelection = false,
        int? pid = null,
        string? document = null,
        int timeoutSeconds = 45,
        CancellationToken cancellationToken = default) =>
        session.DrawBatch(pid, document, ops, expectedToken, ignoreSelection, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "history_undo", Title = "Undo")]
    [Description("Sends Ctrl+Z and then reads the document again.")]
    public Task<ToolReport> HistoryUndo(int? pid = null, string? document = null, int timeoutSeconds = 45, CancellationToken cancellationToken = default) =>
        session.HistoryUndo(pid, document, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "history_redo", Title = "Redo")]
    [Description("Sends Ctrl+Y and then reads the document again.")]
    public Task<ToolReport> HistoryRedo(int? pid = null, string? document = null, int timeoutSeconds = 45, CancellationToken cancellationToken = default) =>
        session.HistoryRedo(pid, document, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "document_create", Title = "New document")]
    [Description("Sends Ctrl+N, fills the size dialog, and checks that document_info reports the new size. width and height are pixels.")]
    public Task<ToolReport> DocumentCreate(int width, int height, int? pid = null, string? document = null, int timeoutSeconds = 45, CancellationToken cancellationToken = default) =>
        session.DocumentCreate(pid, document, width, height, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "document_open", Title = "Open document")]
    [Description("Sends Ctrl+O and opens path. If Paint.NET shows an error dialog, the text is returned and the dialog is left open.")]
    public Task<ToolReport> DocumentOpen(string path, int? pid = null, string? document = null, int timeoutSeconds = 45, CancellationToken cancellationToken = default) =>
        session.DocumentOpen(pid, document, path, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "document_save", Title = "Save")]
    [Description("Sends Ctrl+S. If the document is still untitled, the call fails and tells you to use document_save_as.")]
    public Task<ToolReport> DocumentSave(int? pid = null, string? document = null, int timeoutSeconds = 45, CancellationToken cancellationToken = default) =>
        session.DocumentSave(pid, document, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "document_save_as", Destructive = true, Title = "Save as")]
    [Description("Sends Ctrl+Shift+S to path. Overwriting an existing file requires confirm=true. The server does not delete files and does not write anywhere except path.")]
    public Task<ToolReport> DocumentSaveAs(string path, bool confirm = false, int? pid = null, string? document = null, int timeoutSeconds = 45, CancellationToken cancellationToken = default) =>
        session.DocumentSaveAs(pid, document, path, confirm, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "document_close", Destructive = true, Title = "Close document")]
    [Description("Sends Ctrl+W. A document with unsaved changes returns dirty and leaves the prompt open unless save=true, or discard=true together with confirm=true.")]
    public Task<ToolReport> DocumentClose(bool save = false, bool discard = false, bool confirm = false, int? pid = null, string? document = null, int timeoutSeconds = 45, CancellationToken cancellationToken = default) =>
        session.DocumentClose(pid, document, save, discard, confirm, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "layer_add", Title = "Add layer")]
    [Description("Sends Ctrl+Shift+N. The new layer becomes active. The result is checked with document_info.")]
    public Task<ToolReport> LayerAdd(int? pid = null, string? document = null, int timeoutSeconds = 45, CancellationToken cancellationToken = default) =>
        session.LayerAdd(pid, document, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "layer_delete", Destructive = true, Title = "Delete layer")]
    [Description("Sends Ctrl+Shift+Delete. Refuses the last layer. Requires confirm=true. The index is the order document_info returns.")]
    public Task<ToolReport> LayerDelete(bool confirm = false, int? pid = null, string? document = null, int timeoutSeconds = 45, CancellationToken cancellationToken = default) =>
        session.LayerDelete(pid, document, confirm, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "layer_duplicate", Title = "Duplicate layer")]
    [Description("Sends Ctrl+Shift+D and checks the layer count.")]
    public Task<ToolReport> LayerDuplicate(int? pid = null, string? document = null, int timeoutSeconds = 45, CancellationToken cancellationToken = default) =>
        session.LayerDuplicate(pid, document, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "layer_merge_down", Destructive = true, Title = "Merge layer down")]
    [Description("Sends Ctrl+M. Requires confirm=true. The result is checked with document_info.")]
    public Task<ToolReport> LayerMergeDown(bool confirm = false, int? pid = null, string? document = null, int timeoutSeconds = 45, CancellationToken cancellationToken = default) =>
        session.LayerMergeDown(pid, document, confirm, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "layer_select", Title = "Select layer")]
    [Description("Moves the active layer to index with Layers → Go to Layer Above or Go to Layer Below. index is the order document_info returns. On Paint.NET 5.1.12, index 0 is the bottom layer and a new layer gets the highest index. The result is read back.")]
    public Task<ToolReport> LayerSelect(int index, int? pid = null, string? document = null, int timeoutSeconds = 45, CancellationToken cancellationToken = default) =>
        session.LayerSelect(pid, document, index, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "layer_properties", Title = "Layer properties")]
    [Description("Opens Layer Properties with F4. Sets whichever of name, opacity, visible, and blendMode are present. opacity is 0 to 1, the same scale document_info returns. The dialog is given that value as a 0 to 255 byte. blendMode uses the name document_info returns, such as Normal or Multiply. The result is read back.")]
    public Task<ToolReport> LayerProperties(
        string? name = null,
        double? opacity = null,
        bool? visible = null,
        string? blendMode = null,
        int? pid = null,
        string? document = null,
        int timeoutSeconds = 45,
        CancellationToken cancellationToken = default) =>
        session.LayerProperties(pid, document, name, opacity, visible, blendMode, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "selection_all", Title = "Select all")]
    [Description("Sends Ctrl+A and checks that the selection covers the document.")]
    public Task<ToolReport> SelectionAll(int? pid = null, string? document = null, int timeoutSeconds = 45, CancellationToken cancellationToken = default) =>
        session.SelectionAll(pid, document, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "selection_none", Title = "Deselect")]
    [Description("Sends Ctrl+D. Rectangle and lasso selections are not in v1.")]
    public Task<ToolReport> SelectionNone(int? pid = null, string? document = null, int timeoutSeconds = 45, CancellationToken cancellationToken = default) =>
        session.SelectionNone(pid, document, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "selection_invert", Title = "Invert selection")]
    [Description("Sends Ctrl+I and reads the document again.")]
    public Task<ToolReport> SelectionInvert(int? pid = null, string? document = null, int timeoutSeconds = 45, CancellationToken cancellationToken = default) =>
        session.SelectionInvert(pid, document, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "selection_crop", Destructive = true, Title = "Crop to selection")]
    [Description("Sends Ctrl+Shift+X. Requires confirm=true. The new document size is checked against the selection bounds.")]
    public Task<ToolReport> SelectionCrop(bool confirm = false, int? pid = null, string? document = null, int timeoutSeconds = 45, CancellationToken cancellationToken = default) =>
        session.SelectionCrop(pid, document, confirm, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "color_set", Title = "Set color")]
    [Description("Sets the primary or secondary color from a #RGB, #RRGGBB, or #RRGGBBAA value and reads it back. primary defaults to true.")]
    public Task<ToolReport> ColorSet(string color, bool primary = true, int? pid = null, string? document = null, int timeoutSeconds = 45, CancellationToken cancellationToken = default) =>
        session.ColorSet(pid, document, primary, color, timeoutSeconds, cancellationToken);
}
