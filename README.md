# Paint.NET MCP

A local MCP server that lets an agent use a running Paint.NET 5.1 document. Pixels go through a `BitmapEffect` plugin, so each draw is one real History entry in document pixels. Layers, files, selection commands, and colors go through UI Automation, because the effect API cannot change them.

The server does not launch Paint.NET. It does not listen on the network. The plugin never receives a file path. `blit` and `pixels` images are opened by the server and handed to the plugin as memory-mapped pixels.

## Requirements

- Windows, classic Paint.NET 5.1 or newer. This tree was built against 5.1.12 at `C:\Program Files\paint.net`.
- The .NET 9 SDK, to build. Paint.NET 5.1.12 hosts `net9.0` with the Windows desktop framework, and the plugin targets `net9.0-windows`.
- Paint.NET 5.0 and earlier are out of scope. The 5.2 file-type document API is not used.

The Microsoft Store build was not the install on this machine. The named pipe `\\.\pipe\pdn-mcp-{pid}` is the transport. A file mailbox is not implemented.

## Install

```powershell
.\installer\install.ps1 -Register
```

The script builds the plugin, copies it to `Documents\paint.net App Files\Effects\PaintDotNet.Mcp\`, publishes the server to `%LOCALAPPDATA%\PaintDotNetMcp\server\`, and prints the Grok fragment. It does not start Paint.NET. `-System` copies into `<Paint.NET>\Effects\PaintDotNet.Mcp` instead, which needs a writable Program Files. `-Register` appends the server to `%USERPROFILE%\.grok\config.toml` when that section is absent.

Restart Paint.NET after installing. The plugin assembly file is `PdnMcp.dll`. Paint.NET skips every DLL whose file name starts with `PaintDotNet.`. The pipe starts when Paint.NET constructs the effect during its plugin scan, not from a static constructor. A discovery file appears at `%LOCALAPPDATA%\PaintDotNetMcp\sessions\{pid}.json`. The pipe and that file are ACL'd to the current user. On this machine `MyDocuments` is the OneDrive Documents folder, and that is the folder Paint.NET scans.

Uninstall by deleting the plugin folder and removing `[mcp_servers.paintdotnet]` from `config.toml`.

## How a call works

`document_info` and `document_snapshot` open **Effects → MCP → MCP Inspect...**, copy metadata (and, for a snapshot, pixels) out, and cancel the dialog. On Paint.NET 5.1.12 that cancel leaves Edit → Undo disabled.

`draw_batch` opens **Effects → MCP → MCP Apply...**. One successful call is one undo step. Paint.NET names that step with the effect name, `MCP Apply`. The batch must include `expectedToken` from the last `document_info`. If the canvas changed, the call returns `conflict` and does not draw. An empty batch cancels and does not enable Redo.

The menu walk looks for a submenu whose name is the untranslated string `MCP`, then `MCP Inspect...` or `MCP Apply...`. Paint.NET adds the ellipsis because the effects are configurable.

## Coordinates, color, selection

- Document pixels. Origin is the top left. X increases to the right. Y increases down.
- Colors are `#RGB`, `#RRGGBB`, or `#RRGGBBAA`. A missing alpha is `FF`.
- A selection clips `draw_batch` unless `ignoreSelection` is true.
- Drawing composites in normal src-over into the active layer's stored pixels. The layer's own blend mode still applies when Paint.NET composites the stack.
- `text` is rasterized with GDI+. It is not a live Text tool object, and it will not match that tool's shaping.
- Layer `index` is the order `document_info` returns. On Paint.NET 5.1.12, index 0 is the bottom of the Layers panel (the original Background). Add New Layer appends the highest index and activates it. Go to Layer Above increases the index. Go to Layer Below decreases it. The driver still checks the direction by taking one step and reading the index back.
- Opacity in `layer_properties` is 0 to 1, the same number `document_info` returns. The dialog is given that value as a 0–255 byte. That mapping is unverified until the dialog has been read on a live canvas.

Snapshots are PNG files. The preview long edge is at most 512 pixels. The image long edge is at most 1024 unless `full` is true. A region over 16 megapixels needs `allowLarge`. A region over 80 megapixels is refused unless `allowLarge` is set, and the plugin still enforces the cap it was given.

## Tools

| Tool | What it does |
| --- | --- |
| `pdn_status` | Running instances, plugin presence, window title, modal dialog. |
| `document_info` | Size, layers, selection, colors, generation, document token. |
| `document_snapshot` | PNG paths for the composite, the active layer, or one layer. |
| `draw_batch` | `clear`, `rect`, `rounded_rect`, `ellipse`, `polygon`, `line`, `brush`, `blit`, `pixels`, `text`. |
| `history_undo`, `history_redo` | Ctrl+Z / Ctrl+Y, then a fresh `document_info`. |
| `document_create`, `document_open`, `document_save`, `document_save_as`, `document_close` | File commands. Overwrite and discard require `confirm=true`. |
| `layer_add`, `layer_delete`, `layer_duplicate`, `layer_merge_down`, `layer_select`, `layer_properties` | Layer commands. Delete and merge require `confirm=true`. |
| `selection_all`, `selection_none`, `selection_invert`, `selection_crop` | Ctrl+A / D / I and Ctrl+Shift+X. Crop requires `confirm=true`. |
| `color_set` | Primary or secondary hex color, read back through Inspect. |

`document`, when passed, must match the active window title or the driver cycles Ctrl+Tab until it does. `pid` is required when more than one Paint.NET is open.

Statuses: `ok`, `not_running`, `plugin_not_installed`, `busy`, `conflict`, `verify_failed`, `reverted`, `dirty`, `error`, `too_large`. A foreign modal dialog returns `busy` and is left alone. Destructive tools without `confirm=true` describe the action and do not perform it.

## Draw ops

`ops` is a JSON array. Each object has an `op` field.

```json
[
  {"op":"clear","color":"#FFFFFF"},
  {"op":"rect","x":0,"y":0,"width":10,"height":10,"fill":"#FF0000"},
  {"op":"ellipse","x":4,"y":4,"width":20,"height":12,"stroke":"#000000","strokeWidth":2},
  {"op":"brush","points":[0,0,30,12],"color":"#000000","diameter":8,"hardness":50,"spacing":0.25},
  {"op":"text","text":"Hello","x":12,"y":24,"color":"#000000FF","fontFamily":"Segoe UI","size":16},
  {"op":"blit","path":"C:\\art\\stamp.png","x":8,"y":8,"blend":"src_over"}
]
```

`blit` blend is `src_over` or `replace`. `pixels` is a raw BGRA file plus `width` and `height`, stamped with `replace`.

## What v1 does not do

It does not drive the brush, eraser, clone stamp, wand, gradient, or shape tools. It does not make a rectangle or lasso selection. It does not run arbitrary built-in effects, flatten, record macros, edit a `.pdn` that is not open, or launch Paint.NET.

## Layout

- `src/PaintDotNet.Mcp.Protocol` — frames, discovery files, pixel slots.
- `src/PaintDotNet.Mcp.Raster` — the BGRA rasterizer and PNG IO.
- `src/PaintDotNet.Mcp.Plugin` — Inspect and Apply effects. Paint.NET assemblies are referenced with `Private=false` and are not copied.
- `src/PaintDotNet.Mcp.UiDriver` — FlaUI driver.
- `src/PaintDotNet.Mcp.Server` — stdio MCP server. Logs go to stderr.
- `src/PaintDotNet.Mcp.Spike` — manual smoke test. `dotnet run --project src/PaintDotNet.Mcp.Spike -- --draw` paints a 10×10 red rectangle at (0,0) when Paint.NET is already open with the plugin loaded.
- `tests/PaintDotNet.Mcp.Tests` — protocol, rasterizer, rewriter, and session tests. They do not need Paint.NET.

`Directory.Build.props` reads `PAINTDOTNET_DIR`, otherwise `C:\Program Files\paint.net`.

## Spike notes from this machine

Paint.NET 5.1.12, classic desktop install, product version 5.112.9563.32325, process name `paintdotnet`. The host runtime is `net9.0` plus `Microsoft.WindowsDesktop.App` 9.0.13, rolled forward to 9.0.21.

Smart App Control is on. It blocks `dotnet test` and `dotnet exec` of unsigned assemblies with `0x800711C7`. It did not block `paintdotnet.exe` from loading `PdnMcp.dll`: there was no Code Integrity event, no Plugin Errors dialog, and the pipe answered a handshake. `dotnet test` has not run. Turning Smart App Control off is not part of the installer.

A live session on 2026-10-20 showed:

- Handshake `{"protocol":1}` returns protocol 1 and `pdnVersion` `5.112.9563.32325`.
- The Effects menu opens with ExpandCollapse. The items are `MCP Inspect...` and `MCP Apply...`. After a successful apply, Edit also shows `Repeat MCP Apply`.
- Inspect cancels. Edit → Undo stays disabled, and a second Inspect returns the same document token.
- Apply of a 10×10 `#FF0000` rectangle at (0,0) commits. The following Inspect reads BGRA `0,0,255,255` at (0,0), (5,5), and (9,9), and `255,255,255,255` at (10,0). Edit → Undo changes from disabled to enabled. Redo stays disabled.
- An empty Apply returns `committed: false` and `error: empty`. Redo stays disabled.
- The config form must override `OnCreateInitialToken`, `OnUpdateDialogFromToken`, and `OnUpdateTokenFromDialog`. Without the token override, Paint.NET defers a `NotImplementedException` and exits when the dialog loads, before `OnRender`.
- A rectangle with no stroke color fills its full bounds. The default stroke width is not applied unless a stroke color is set.
- Add New Layer on a one-layer document yields index 0 `Background` and index 1 `Layer 2`, with index 1 active. Go to Layer Below selects 0. Go to Layer Above selects 1.
- The History window (`HistoryForm`) is custom-drawn. Its entries are not UI Automation list items, so the undo label is taken from the effect name and from `Repeat MCP Apply`. Screen capture of that window was covered by another foreground application.
- Keystrokes do not reach Paint.NET while another application holds the foreground. Menu Expand and Invoke do. Layer add, delete, duplicate, merge, step, undo, and redo use those menu items.

Still open on a live canvas:

- A partial selection was not drawn, because a drag would go to the foreground application. `ignoreSelection` still sets `DisableSelectionClipping`.
- AutomationIds for New, Save As, and Layer Properties were not recorded. The driver looks them up by English names and returns the control tree when a lookup misses.
- Only an English UI was available. The walk still keys off the literal submenu name `MCP`.
- `dotnet exec` of the MCP server is still expected to fail under Smart App Control until the server binary is signed or the policy allows it.

Logs: `%LOCALAPPDATA%\PaintDotNetMcp\logs\plugin-{pid}.log`.
