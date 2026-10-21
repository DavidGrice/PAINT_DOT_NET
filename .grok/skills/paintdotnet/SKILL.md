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

## Safety

Pass `confirm: true` for `layer_delete`, `layer_merge_down`, `selection_crop`, and `document_close` when the document is dirty. If a call returns `busy` or says a dialog is open, stop and tell the user. A second inspect while one is still running keeps the plugin pipe busy.

Do not bring Paint.NET to the front or minimize the user's other applications.

## Out of this version

Real brush, eraser, clone, magic wand, gradient, shape tools, flatten, and launching Paint.NET are not available. Use the draw operations above for pixel work.
