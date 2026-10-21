---
name: paintdotnet
description: >
  Edit an open Paint.NET 5.1 document through the paintdotnet MCP: inspect,
  draw, layers, selection, colors, and save. Use when the user mentions
  Paint.NET, paint.net, a .pdn file, or a 2D edit in Paint.NET, or runs
  /paintdotnet.
---

# Paint.NET

Paint.NET must already be running with the MCP effect plugin loaded. The tools do not launch it.

## Before editing

1. Call `pdn_status`. If the status is `not_running`, ask the user to open Paint.NET. If it is `plugin_not_installed`, the plugin DLL is missing from the user's Documents `paint.net App Files\Effects\PaintDotNet.Mcp` folder. Installing it is `installer/install.ps1` in the Paint.NET MCP repo, then a restart of Paint.NET.
2. When more than one instance is open, pass the `pid` from `pdn_status` on later calls.
3. Call `document_info` and keep `documentToken`. A later mutating call that omits the matching `expectedToken` is rejected.

## Drawing and reading

- `draw_batch` is one History entry named MCP Apply. `ops` is a JSON array of `clear`, `rect`, `rounded_rect`, `ellipse`, `polygon`, `line`, `brush`, `blit`, `pixels`, or `text`.
- Coordinates are document pixels, origin at the top left, x to the right, y down. Colors are `#RGB`, `#RRGGBB`, or `#RRGGBBAA`.
- The current selection clips drawing unless `ignoreSelection` is true. `text` is painted pixels, not a Paint.NET text object.
- `document_snapshot` returns an image path. Open that file to see pixels. The tool result does not contain the bitmap.
- Layer index 0 is the bottom row of the Layers panel.

## Dialogs

`document_create`, `document_open`, `document_save`, `document_save_as`, `document_close`, and `layer_properties` find the dialog inside the Paint.NET main window and finish it. Do not fill these dialogs yourself. Do not send the same call again while one is open.

New, Open, Save As, Save Configuration, and the unsaved-changes prompt are child windows of the main window. A search of top-level windows does not see them. Confirm Save As is nested inside Save As. The tool clicks the menu item. Invoke does not return until the dialog closes, so a second call cannot fill the fields.

- `document_create` is File → New. It sets pixel Width and Height. The same dialog also has a print-size Width and Height; those are not the pixel fields. When Maintain aspect ratio changes the other pixel number, the tool turns that off and sets both again. It presses OK only after both pixel numbers match.
- `document_save_as` types the full path into the file-name field and reads that field back before Save. It does not press Save while the field still shows another name, such as Untitled. Save Configuration then opens for PNG and other formats. The tool presses OK and leaves the current settings, including bit depth. If the file already exists, Confirm Save As requires `confirm: true`. Without it the tool clicks No and closes Save As.
- `document_save` presses OK if Save Configuration opens for a document that already has a file name. An untitled document needs `document_save_as`.
- `document_open` types the path and presses Open. If Paint.NET shows an error, that text comes back and the dialog stays open.
- `document_close` answers the unsaved-changes prompt only when `save: true`, or `discard: true` with `confirm: true`.
- `layer_properties` sets the Name field, the Opacity field as a 0–255 byte, and the blend-mode list. It presses Cancel instead of OK when a field does not match. It changes Visible only when that checkbox reports a toggle state.

If a call times out or says a dialog is open, stop and tell the user. The host finishes one dialog command at a time, so another call waits behind it. Do not retry that call in a loop.

These dialog commands click the dialog they opened. Do not minimize the user's other applications.

## Safety

Pass `confirm: true` for `layer_delete`, `layer_merge_down`, `selection_crop`, and `document_close` when the document is dirty. If a call returns `busy` or says a dialog is open, stop and tell the user. A second inspect while one is still running keeps the plugin pipe busy.

## Out of this version

Real brush, eraser, clone, magic wand, gradient, shape tools, flatten, and launching Paint.NET are not available. Use the draw operations above for pixel work.
