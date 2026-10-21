import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import { z } from "zod";
import { PaintSession } from "./session.mjs";

const session = new PaintSession();
const server = new McpServer({ name: "paintdotnet", version: "0.1.0" });

const common = {
  pid: z.number().int().optional().describe("Paint.NET process id. Required when more than one instance is open."),
  document: z.string().optional().describe("Title text of the document to use. The active document must already contain this text."),
  timeoutSeconds: z.number().int().optional().describe("Unused by the Node host. Commands wait on the plugin and the UI helper."),
};

function text(report) {
  return { content: [{ type: "text", text: JSON.stringify(report) }] };
}

function tool(name, title, description, shape, run) {
  server.registerTool(name, {
    title,
    description,
    inputSchema: shape,
  }, async (args) => text(await run(args)));
}

tool("pdn_status", "Paint.NET status",
  "Lists running Paint.NET instances and whether the MCP plugin is loaded. Does not launch Paint.NET. If several instances are open, pass pid.",
  { pid: common.pid, timeoutSeconds: common.timeoutSeconds },
  (args) => session.status(args));

tool("document_info", "Document info",
  "Reads the active document through MCP Inspect. Returns size, resolution, layers, selection bounds, colors, generation, and documentToken. Layer index is the order of this list. Coordinates are document pixels, origin top-left, x right, y down. Colors are #RRGGBBAA.",
  common,
  (args) => session.documentInfo(args));

tool("document_snapshot", "Document snapshot",
  "Writes a PNG of the composite, the active layer, or one layer, plus a preview whose long edge is at most 512 pixels. The image long edge is at most 1024 unless full is true. kind is composite, active, or layer. Pass layerIndex only for kind=layer. The index is the order document_info returns. A region is x, y, width, height in document pixels. Planes over 16 megapixels need allowLarge, and planes over 80 megapixels are refused.",
  {
    ...common,
    kind: z.string().optional().describe("composite, active, or layer."),
    layerIndex: z.number().int().optional(),
    x: z.number().int().optional(),
    y: z.number().int().optional(),
    width: z.number().int().optional(),
    height: z.number().int().optional(),
    full: z.boolean().optional(),
    allowLarge: z.boolean().optional(),
  },
  (args) => session.documentSnapshot(args));

tool("draw_batch", "Draw a batch",
  "Draws into the active layer as one History entry named MCP Apply. Pass expectedToken from document_info. A mismatch returns conflict and does not draw. The selection clips the result unless ignoreSelection is true. Coordinates are document pixels, origin top-left. Colors are #RGB, #RRGGBB, or #RRGGBBAA. ops is a JSON array. Each object has op set to clear, rect, rounded_rect, ellipse, polygon, line, brush, blit, pixels, or text. text is rasterized with GDI+ and is not a Paint.NET text object. blit is a PNG path. pixels is a raw BGRA file plus width and height. Drawing uses normal src-over into the layer's stored pixels.",
  {
    ...common,
    ops: z.union([z.string(), z.array(z.record(z.string(), z.any()))]).describe("JSON array of draw operations, or that array already parsed."),
    expectedToken: z.string().describe("documentToken from document_info."),
    ignoreSelection: z.boolean().optional(),
  },
  (args) => session.drawBatch(args));

tool("history_undo", "Undo", "Invokes Edit → Undo and then reads the document again.", common, (args) => session.historyUndo(args));
tool("history_redo", "Redo", "Invokes Edit → Redo and then reads the document again.", common, (args) => session.historyRedo(args));

tool("document_create", "New document",
  "Opens File → New and finishes that dialog. It sets the pixel Width and Height, not the print-size fields. The New dialog is a child of the main window. If Maintain aspect ratio changes the other number, the tool turns that off and sets both again. It does not press OK until both pixel numbers match. width and height are pixels. Do not retry this call while the dialog is open.",
  { ...common, width: z.number().int(), height: z.number().int() },
  (args) => session.documentCreate(args));

tool("document_open", "Open document",
  "Opens File → Open, types path into the file-name field, reads it back, and presses Open. The Open dialog is a child of the main window. If Paint.NET shows an error dialog, the text is returned and the dialog is left open. Do not retry this call while a dialog is open.",
  { ...common, path: z.string() },
  (args) => session.documentOpen(args));

tool("document_save", "Save",
  "Clicks File → Save and finishes the dialogs that follow. If Save Configuration opens, the tool presses OK and leaves that dialog's current settings, including bit depth. If the document is still untitled, the call fails and tells you to use document_save_as. Do not retry this call while a dialog is open.",
  common,
  (args) => session.documentSave(args));

tool("document_save_as", "Save as",
  "Opens File → Save As, types path into the file-name field, and reads it back before pressing Save. It will not press Save while that field shows a different name. It then presses OK on Save Configuration and leaves that dialog's current settings, including bit depth. Confirm Save As is nested inside Save As and is answered only when confirm=true. Without confirm, the tool clicks No and closes Save As. Do not retry this call while a dialog is open. The server does not delete files and does not write anywhere except path.",
  { ...common, path: z.string(), confirm: z.boolean().optional() },
  (args) => session.documentSaveAs(args));

tool("document_close", "Close document",
  "Clicks File → Close and answers the unsaved-changes prompt when asked. A document with unsaved changes returns dirty and leaves the prompt open unless save=true, or discard=true together with confirm=true. Do not retry this call while that prompt is open.",
  { ...common, save: z.boolean().optional(), discard: z.boolean().optional(), confirm: z.boolean().optional() },
  (args) => session.documentClose(args));

tool("layer_add", "Add layer",
  "Invokes Layers → Add New Layer. The new layer becomes active. The result is checked with document_info.",
  common, (args) => session.layerAdd(args));

tool("layer_delete", "Delete layer",
  "Invokes Layers → Delete Layer. Refuses the last layer. Requires confirm=true.",
  { ...common, confirm: z.boolean().optional() },
  (args) => session.layerDelete(args));

tool("layer_duplicate", "Duplicate layer",
  "Invokes Layers → Duplicate Layer and checks the layer count.",
  common, (args) => session.layerDuplicate(args));

tool("layer_merge_down", "Merge layer down",
  "Invokes Layers → Merge Layer Down. Requires confirm=true. The result is checked with document_info.",
  { ...common, confirm: z.boolean().optional() },
  (args) => session.layerMergeDown(args));

tool("layer_select", "Select layer",
  "Moves the active layer to index with Layers → Go to Layer Above or Go to Layer Below. index is the order document_info returns. On Paint.NET 5.1.12, index 0 is the bottom layer and a new layer gets the highest index. The result is read back.",
  { ...common, index: z.number().int() },
  (args) => session.layerSelect(args));

tool("layer_properties", "Layer properties",
  "Opens Layers → Layer Properties and finishes it. Name is the text field. Opacity is 0 to 1, the same scale document_info returns, and the dialog is given that value as a 0 to 255 byte. blendMode uses the name document_info returns, such as Normal or Multiply, on the blend-mode list. The dialog is closed with Cancel if a field does not match. Visible is changed only when the checkbox reports a toggle state.",
  {
    ...common,
    name: z.string().optional(),
    opacity: z.number().optional(),
    visible: z.boolean().optional(),
    blendMode: z.string().optional(),
  },
  (args) => session.layerProperties(args));

tool("selection_all", "Select all",
  "Invokes Edit → Select All and checks that the selection covers the document.",
  common, (args) => session.selectionAll(args));

tool("selection_none", "Deselect",
  "Invokes Edit → Deselect. Rectangle and lasso selections are not in v1.",
  common, (args) => session.selectionNone(args));

tool("selection_invert", "Invert selection",
  "Invokes Edit → Invert Selection and reads the document again.",
  common, (args) => session.selectionInvert(args));

tool("selection_crop", "Crop to selection",
  "Invokes Image → Crop to Selection. Requires confirm=true. The new document size is checked against the selection bounds.",
  { ...common, confirm: z.boolean().optional() },
  (args) => session.selectionCrop(args));

tool("color_set", "Set color",
  "Sets the hex field in the Colors window from a #RGB, #RRGGBB, or #RRGGBBAA value and reads it back. primary defaults to true. The secondary-color swap is not sent as a keystroke, because keystrokes miss Paint.NET while another application holds the foreground.",
  { ...common, color: z.string(), primary: z.boolean().optional() },
  (args) => session.colorSet(args));

process.stderr.write("Paint.NET MCP node server is starting.\n");
const transport = new StdioServerTransport();
await server.connect(transport);
