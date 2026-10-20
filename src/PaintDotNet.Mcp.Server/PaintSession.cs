using System.Diagnostics;
using PaintDotNet.Mcp.Protocol;
using PaintDotNet.Mcp.Raster;
using PaintDotNet.Mcp.UiDriver;

namespace PaintDotNet.Mcp.Server;

public sealed class PaintSession : IDisposable
{
    private readonly IPaintDriver driver;
    private readonly Func<IReadOnlyList<DiscoveryRecord>> readSessions;
    private readonly Func<IReadOnlyList<int>> readProcesses;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<int, BridgeClient> clients = [];
    private readonly Dictionary<int, HandshakeResponse> handshakes = [];

    public PaintSession(IPaintDriver driver)
        : this(driver, SessionFiles.ReadAlive, PaintProcesses)
    {
    }

    public PaintSession(
        IPaintDriver driver,
        Func<IReadOnlyList<DiscoveryRecord>> readSessions,
        Func<IReadOnlyList<int>> readProcesses)
    {
        this.driver = driver;
        this.readSessions = readSessions;
        this.readProcesses = readProcesses;
    }

    public static IReadOnlyList<int> PaintProcesses()
    {
        var list = new List<int>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.ProcessName.Contains("paintdotnet", StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(process.Id);
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                process.Dispose();
            }
        }

        return list;
    }

    public async Task<ToolReport> Status(int? pid, int timeoutSeconds, CancellationToken cancellationToken) =>
        await Run(pid, null, timeoutSeconds, cancellationToken, requireSession: false, (id, _) =>
        {
            var sessions = readSessions();
            var match = sessions.FirstOrDefault(item => item.Pid == id);
            if (match is null)
            {
                return Task.FromResult(PluginMissing(id));
            }

            string? title = null;
            bool? modal = null;
            string? modalText = null;
            string? message = null;
            try
            {
                var state = driver.ReadState(id);
                title = state.Title;
                modal = state.ModalOpen;
                modalText = state.ModalText;
            }
            catch (PaintUiException ex)
            {
                message = ex.Message;
            }

            return Task.FromResult(new ToolReport
            {
                Status = "ok",
                Message = message,
                Pid = id,
                PdnVersion = match.PdnVersion,
                PluginVersion = match.PluginVersion,
                WindowTitle = title,
                ModalOpen = modal,
                ModalText = modalText,
                Instances = ListInstances(),
            });
        });

    public Task<ToolReport> DocumentInfo(int? pid, string? document, int timeoutSeconds, CancellationToken cancellationToken) =>
        Run(pid, document, timeoutSeconds, cancellationToken, requireSession: true, (id, _) => Task.FromResult(Info(id)));

    public Task<ToolReport> DocumentSnapshot(
        int? pid,
        string? document,
        string kind,
        int? layerIndex,
        IntRect? region,
        bool full,
        bool allowLarge,
        int timeoutSeconds,
        CancellationToken cancellationToken) =>
        Run(pid, document, timeoutSeconds, cancellationToken, requireSession: true, (id, _) =>
            Task.FromResult(Snapshot(id, kind, layerIndex, region, full, allowLarge)));

    public Task<ToolReport> DrawBatch(
        int? pid,
        string? document,
        string ops,
        string expectedToken,
        bool ignoreSelection,
        int timeoutSeconds,
        CancellationToken cancellationToken) =>
        Run(pid, document, timeoutSeconds, cancellationToken, requireSession: true, (id, _) =>
            Task.FromResult(Draw(id, ops, expectedToken, ignoreSelection)));

    public Task<ToolReport> HistoryUndo(int? pid, string? document, int timeoutSeconds, CancellationToken cancellationToken) =>
        Run(pid, document, timeoutSeconds, cancellationToken, requireSession: true, (id, _) =>
        {
            driver.Undo(id);
            return Task.FromResult(Info(id));
        });

    public Task<ToolReport> HistoryRedo(int? pid, string? document, int timeoutSeconds, CancellationToken cancellationToken) =>
        Run(pid, document, timeoutSeconds, cancellationToken, requireSession: true, (id, _) =>
        {
            driver.Redo(id);
            return Task.FromResult(Info(id));
        });

    public Task<ToolReport> DocumentCreate(int? pid, string? document, int width, int height, int timeoutSeconds, CancellationToken cancellationToken) =>
        Run(pid, document, timeoutSeconds, cancellationToken, requireSession: true, (id, _) =>
        {
            driver.NewDocument(id, width, height);
            return Task.FromResult(Verify(id, doc =>
                doc.Width == width && doc.Height == height
                    ? null
                    : $"The document is {doc.Width}x{doc.Height} after New."));
        });

    public Task<ToolReport> DocumentOpen(int? pid, string? document, string path, int timeoutSeconds, CancellationToken cancellationToken) =>
        Run(pid, document, timeoutSeconds, cancellationToken, requireSession: true, (id, _) =>
        {
            driver.OpenDocument(id, path);
            var modal = driver.ModalText(id);
            if (modal is not null)
            {
                return Task.FromResult(new ToolReport
                {
                    Status = "error",
                    Pid = id,
                    Message = "Paint.NET showed a dialog after Open. It was left open. " + modal,
                    ModalOpen = true,
                    ModalText = modal,
                });
            }

            var name = Path.GetFileName(path);
            return Task.FromResult(Verify(id, doc =>
                driver.ReadState(id).Title?.Contains(name, StringComparison.OrdinalIgnoreCase) == true
                    ? null
                    : $"The window title does not contain {name}."));
        });

    public Task<ToolReport> DocumentSave(int? pid, string? document, int timeoutSeconds, CancellationToken cancellationToken) =>
        Run(pid, document, timeoutSeconds, cancellationToken, requireSession: true, (id, _) =>
        {
            if (!driver.Save(id))
            {
                return Task.FromResult(new ToolReport
                {
                    Status = "error",
                    Pid = id,
                    Message = "This document has no file name yet. Call document_save_as with a path.",
                });
            }

            return Task.FromResult(Info(id));
        });

    public Task<ToolReport> DocumentSaveAs(int? pid, string? document, string path, bool confirm, int timeoutSeconds, CancellationToken cancellationToken) =>
        Run(pid, document, timeoutSeconds, cancellationToken, requireSession: true, (id, _) =>
        {
            if (File.Exists(path) && !confirm)
            {
                return Task.FromResult(new ToolReport
                {
                    Status = "error",
                    Pid = id,
                    Message = $"Refused to overwrite {path}. Call again with confirm=true.",
                });
            }

            driver.SaveAs(id, path, confirm);
            if (!File.Exists(path))
            {
                return Task.FromResult(new ToolReport
                {
                    Status = "verify_failed",
                    Pid = id,
                    Message = $"Save As finished but {path} is not on disk.",
                });
            }

            var info = Info(id);
            return Task.FromResult(info with { Message = "Saved " + path });
        });

    public Task<ToolReport> DocumentClose(int? pid, string? document, bool save, bool discard, bool confirm, int timeoutSeconds, CancellationToken cancellationToken) =>
        Run(pid, document, timeoutSeconds, cancellationToken, requireSession: true, (id, _) =>
        {
            if (discard && !confirm)
            {
                return Task.FromResult(new ToolReport
                {
                    Status = "error",
                    Pid = id,
                    Message = "Refused to discard unsaved changes. Call again with discard=true and confirm=true, or with save=true.",
                });
            }

            var prompt = driver.CloseDocument(id, save, discard);
            return Task.FromResult(prompt switch
            {
                ClosePrompt.DirtyPromptLeftOpen => new ToolReport
                {
                    Status = "dirty",
                    Pid = id,
                    Message = "The document has unsaved changes. The save prompt was left open. Call again with save=true, or with discard=true and confirm=true.",
                    ModalOpen = true,
                },
                ClosePrompt.Saved => new ToolReport { Status = "ok", Pid = id, Message = "The document was saved and closed." },
                ClosePrompt.Discarded => new ToolReport { Status = "ok", Pid = id, Message = "The document was closed and the changes were discarded." },
                _ => new ToolReport { Status = "ok", Pid = id, Message = "The document was closed." },
            });
        });

    public Task<ToolReport> LayerAdd(int? pid, string? document, int timeoutSeconds, CancellationToken cancellationToken) =>
        Run(pid, document, timeoutSeconds, cancellationToken, requireSession: true, (id, _) =>
        {
            var before = Info(id);
            if (before.Document is null)
            {
                return Task.FromResult(before);
            }

            int count = before.Document.Layers.Count;
            driver.AddLayer(id);
            return Task.FromResult(Verify(id, doc =>
                doc.Layers.Count == count + 1 ? null : $"Layer count is {doc.Layers.Count}, expected {count + 1}."));
        });

    public Task<ToolReport> LayerDelete(int? pid, string? document, bool confirm, int timeoutSeconds, CancellationToken cancellationToken) =>
        Run(pid, document, timeoutSeconds, cancellationToken, requireSession: true, (id, _) =>
        {
            if (!confirm)
            {
                return Task.FromResult(new ToolReport
                {
                    Status = "error",
                    Pid = id,
                    Message = "Refused to delete the active layer. Call again with confirm=true.",
                });
            }

            var before = Info(id);
            if (before.Document is null)
            {
                return Task.FromResult(before);
            }

            if (before.Document.Layers.Count <= 1)
            {
                return Task.FromResult(new ToolReport
                {
                    Status = "error",
                    Pid = id,
                    Message = "Refused to delete the only layer.",
                });
            }

            int count = before.Document.Layers.Count;
            driver.DeleteLayer(id);
            return Task.FromResult(Verify(id, doc =>
                doc.Layers.Count == count - 1 ? null : $"Layer count is {doc.Layers.Count}, expected {count - 1}."));
        });

    public Task<ToolReport> LayerDuplicate(int? pid, string? document, int timeoutSeconds, CancellationToken cancellationToken) =>
        Run(pid, document, timeoutSeconds, cancellationToken, requireSession: true, (id, _) =>
        {
            var before = Info(id);
            if (before.Document is null)
            {
                return Task.FromResult(before);
            }

            int count = before.Document.Layers.Count;
            driver.DuplicateLayer(id);
            return Task.FromResult(Verify(id, doc =>
                doc.Layers.Count == count + 1 ? null : $"Layer count is {doc.Layers.Count}, expected {count + 1}."));
        });

    public Task<ToolReport> LayerMergeDown(int? pid, string? document, bool confirm, int timeoutSeconds, CancellationToken cancellationToken) =>
        Run(pid, document, timeoutSeconds, cancellationToken, requireSession: true, (id, _) =>
        {
            if (!confirm)
            {
                return Task.FromResult(new ToolReport
                {
                    Status = "error",
                    Pid = id,
                    Message = "Refused to merge the active layer down. Call again with confirm=true.",
                });
            }

            var before = Info(id);
            if (before.Document is null)
            {
                return Task.FromResult(before);
            }

            int count = before.Document.Layers.Count;
            driver.MergeDown(id);
            return Task.FromResult(Verify(id, doc =>
                doc.Layers.Count == count - 1 ? null : $"Layer count is {doc.Layers.Count}, expected {count - 1}."));
        });

    public Task<ToolReport> LayerSelect(int? pid, string? document, int index, int timeoutSeconds, CancellationToken cancellationToken) =>
        Run(pid, document, timeoutSeconds, cancellationToken, requireSession: true, (id, _) =>
            Task.FromResult(SelectLayer(id, index)));

    public Task<ToolReport> LayerProperties(
        int? pid,
        string? document,
        string? name,
        double? opacity,
        bool? visible,
        string? blendMode,
        int timeoutSeconds,
        CancellationToken cancellationToken) =>
        Run(pid, document, timeoutSeconds, cancellationToken, requireSession: true, (id, _) =>
        {
            var before = Info(id);
            if (before.Document is null)
            {
                return Task.FromResult(before);
            }

            var active = before.Document.Layers.First(layer => layer.Index == before.Document.ActiveLayerIndex);
            driver.SetLayerProperties(id, active.Name, name, opacity, visible, blendMode);
            return Task.FromResult(Verify(id, doc => CheckLayer(doc, name, opacity, visible, blendMode)));
        });

    public Task<ToolReport> SelectionAll(int? pid, string? document, int timeoutSeconds, CancellationToken cancellationToken) =>
        Run(pid, document, timeoutSeconds, cancellationToken, requireSession: true, (id, _) =>
        {
            driver.SelectAll(id);
            return Task.FromResult(Verify(id, doc => doc.SelectionCoversDocument ? null : "The selection does not cover the document."));
        });

    public Task<ToolReport> SelectionNone(int? pid, string? document, int timeoutSeconds, CancellationToken cancellationToken) =>
        Run(pid, document, timeoutSeconds, cancellationToken, requireSession: true, (id, _) =>
        {
            driver.SelectNone(id);
            return Task.FromResult(Verify(id, doc =>
                doc.SelectionBounds.IsEmpty || !doc.SelectionCoversDocument
                    ? null
                    : "The selection still covers the document."));
        });

    public Task<ToolReport> SelectionInvert(int? pid, string? document, int timeoutSeconds, CancellationToken cancellationToken) =>
        Run(pid, document, timeoutSeconds, cancellationToken, requireSession: true, (id, _) =>
        {
            driver.InvertSelection(id);
            return Task.FromResult(Info(id));
        });

    public Task<ToolReport> SelectionCrop(int? pid, string? document, bool confirm, int timeoutSeconds, CancellationToken cancellationToken) =>
        Run(pid, document, timeoutSeconds, cancellationToken, requireSession: true, (id, _) =>
        {
            if (!confirm)
            {
                return Task.FromResult(new ToolReport
                {
                    Status = "error",
                    Pid = id,
                    Message = "Refused to crop to the selection. Call again with confirm=true.",
                });
            }

            var before = Info(id);
            if (before.Document is null)
            {
                return Task.FromResult(before);
            }

            if (before.Document.SelectionBounds.IsEmpty)
            {
                return Task.FromResult(new ToolReport { Status = "error", Pid = id, Message = "There is no selection to crop." });
            }

            int width = before.Document.SelectionBounds.Width;
            int height = before.Document.SelectionBounds.Height;
            driver.CropToSelection(id);
            return Task.FromResult(Verify(id, doc =>
                doc.Width == width && doc.Height == height
                    ? null
                    : $"The document is {doc.Width}x{doc.Height} after crop, expected {width}x{height}."));
        });

    public Task<ToolReport> ColorSet(int? pid, string? document, bool primary, string color, int timeoutSeconds, CancellationToken cancellationToken) =>
        Run(pid, document, timeoutSeconds, cancellationToken, requireSession: true, (id, _) =>
        {
            if (!ColorHex.TryParse(color, out var parsed))
            {
                return Task.FromResult(new ToolReport { Status = "error", Pid = id, Message = "Color must be #RGB, #RRGGBB, or #RRGGBBAA." });
            }

            string formatted = ColorHex.Format(parsed);
            driver.SetColor(id, primary, formatted);
            return Task.FromResult(Verify(id, doc =>
            {
                var actual = primary ? doc.PrimaryColor : doc.SecondaryColor;
                return SameColor(actual, formatted) ? null : $"The color is {actual}, expected {formatted}.";
            }));
        });

    public void Dispose()
    {
        foreach (var client in clients.Values)
        {
            client.Dispose();
        }

        clients.Clear();
        gate.Dispose();
    }

    private async Task<ToolReport> Run(
        int? pid,
        string? document,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        bool requireSession,
        Func<int, CancellationToken, Task<ToolReport>> action)
    {
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 180)), cancellationToken))
        {
            return new ToolReport { Status = "busy", Message = "Another Paint.NET command is still running." };
        }

        try
        {
            var blocked = Resolve(pid, out int resolved);
            if (blocked is not null)
            {
                return blocked;
            }

            if (requireSession && readSessions().All(item => item.Pid != resolved))
            {
                return PluginMissing(resolved);
            }

            if (!string.IsNullOrWhiteSpace(document))
            {
                driver.ActivateDocument(resolved, document);
            }

            return await action(resolved, cancellationToken);
        }
        catch (PaintUiException ex)
        {
            return FromUi(ex, pid);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
        {
            return new ToolReport { Status = "error", Pid = pid, Message = ex.Message };
        }
        finally
        {
            gate.Release();
        }
    }

    private ToolReport? Resolve(int? pid, out int resolved)
    {
        var processes = readProcesses();
        var sessions = readSessions();
        if (pid is int wanted)
        {
            resolved = wanted;
            bool alive = processes.Contains(wanted) || sessions.Any(item => item.Pid == wanted);
            if (!alive)
            {
                return new ToolReport { Status = "not_running", Pid = wanted, Message = "That Paint.NET process is not running.", Instances = ListInstances() };
            }

            if (sessions.All(item => item.Pid != wanted))
            {
                return PluginMissing(wanted);
            }

            return null;
        }

        if (processes.Count == 0 && sessions.Count == 0)
        {
            resolved = 0;
            return new ToolReport { Status = "not_running", Message = "Paint.NET is not running. v1 does not launch it.", Instances = [] };
        }

        if (sessions.Count == 1)
        {
            resolved = sessions[0].Pid;
            return null;
        }

        if (sessions.Count == 0 && processes.Count == 1)
        {
            resolved = processes[0];
            return PluginMissing(processes[0]);
        }

        resolved = 0;
        return new ToolReport
        {
            Status = "error",
            Message = "More than one Paint.NET instance is open. Pass pid.",
            Instances = ListInstances(),
        };
    }

    private IReadOnlyList<InstanceReport> ListInstances()
    {
        var sessions = readSessions();
        var ids = readProcesses().Concat(sessions.Select(item => item.Pid)).Distinct();
        return ids.Select(id =>
        {
            var session = sessions.FirstOrDefault(item => item.Pid == id);
            return new InstanceReport(id, session?.PdnVersion, session?.PluginVersion, session is not null);
        }).ToArray();
    }

    private static ToolReport PluginMissing(int pid) => new()
    {
        Status = "plugin_not_installed",
        Pid = pid,
        PluginPath = SessionFiles.UserPluginDirectory,
        Message = "Paint.NET is running without the MCP plugin. Run installer/install.ps1 and restart Paint.NET. Plugin folder: " + SessionFiles.UserPluginDirectory,
    };

    private ToolReport Info(int pid)
    {
        var report = Inspect(pid, new PrepareInspectRequest(Guid.NewGuid().ToString("N"), false, false, null, null, 1));
        if (report.Document is null)
        {
            return report;
        }

        return report with { Document = report.Document with { Planes = [] } };
    }

    private ToolReport Snapshot(int pid, string kind, int? layerIndex, IntRect? region, bool full, bool allowLarge)
    {
        if (region is { } rect)
        {
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                return new ToolReport { Status = "error", Pid = pid, Message = "The snapshot region must have a positive width and height." };
            }

            if (rect.Area > ProtocolConstants.HardMaxPixels && !allowLarge)
            {
                return new ToolReport { Status = "too_large", Pid = pid, Message = $"The region is {rect.Area} pixels. The hard cap is {ProtocolConstants.HardMaxPixels}. Pass allowLarge or a smaller region." };
            }

            if (rect.Area > ProtocolConstants.DefaultMaxPixels && !allowLarge)
            {
                return new ToolReport { Status = "too_large", Pid = pid, Message = $"The region is {rect.Area} pixels. The default cap is {ProtocolConstants.DefaultMaxPixels}. Pass allowLarge (up to {ProtocolConstants.HardMaxPixels}) or a smaller region." };
            }
        }

        bool composite = string.Equals(kind, "composite", StringComparison.OrdinalIgnoreCase);
        bool active = string.Equals(kind, "active", StringComparison.OrdinalIgnoreCase);
        bool layer = string.Equals(kind, "layer", StringComparison.OrdinalIgnoreCase);
        if (!composite && !active && !layer)
        {
            return new ToolReport { Status = "error", Pid = pid, Message = "kind must be composite, active, or layer." };
        }

        if (layer && layerIndex is null)
        {
            return new ToolReport { Status = "error", Pid = pid, Message = "layerIndex is required when kind is layer. The index is the order document_info returns." };
        }

        int maxPixels = allowLarge ? ProtocolConstants.HardMaxPixels : ProtocolConstants.DefaultMaxPixels;
        if (region is { } chosen && allowLarge)
        {
            maxPixels = (int)Math.Min(int.MaxValue, Math.Max(maxPixels, chosen.Area));
        }

        var report = Inspect(pid, new PrepareInspectRequest(
            Guid.NewGuid().ToString("N"),
            composite,
            active,
            layer ? layerIndex : null,
            region,
            maxPixels));
        if (report.Status != "ok" || report.Document is null)
        {
            if (report.Message?.Contains("pixel cap", StringComparison.OrdinalIgnoreCase) == true)
            {
                return report with { Status = "too_large" };
            }

            return report;
        }

        var plane = report.Document.Planes.FirstOrDefault();
        if (plane is null)
        {
            return report with { Status = "error", Message = "Inspect returned no pixel plane." };
        }

        try
        {
            using var slot = PixelSlot.Open(plane.Slot);
            var surface = new Surface(slot.Width, slot.Height, slot.ReadPixels());
            var directory = Path.Combine(SessionFiles.ImageDirectory, pid.ToString());
            Directory.CreateDirectory(directory);
            string stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            var image = full ? surface : surface.FitLongEdge(1024);
            string imagePath = Path.Combine(directory, $"{stamp}-{plane.Kind}.png");
            string previewPath = Path.Combine(directory, $"{stamp}-{plane.Kind}-preview.png");
            image.SavePng(imagePath);
            surface.FitLongEdge(512).SavePng(previewPath);
            return report with
            {
                Document = report.Document with { Planes = [] },
                ImagePath = imagePath,
                PreviewPath = previewPath,
                ImageWidth = surface.Width,
                ImageHeight = surface.Height,
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FileNotFoundException)
        {
            return new ToolReport { Status = "error", Pid = pid, Message = "The pixel plane could not be read. " + ex.Message };
        }
    }

    private ToolReport Draw(int pid, string ops, string expectedToken, bool ignoreSelection)
    {
        if (string.IsNullOrWhiteSpace(expectedToken))
        {
            return new ToolReport { Status = "error", Pid = pid, Message = "Pass expectedToken from the documentToken returned by document_info." };
        }

        var slots = new List<PixelSlot>();
        try
        {
            var rewritten = OpRewriter.Rewrite(ops, pid, slots);
            if (rewritten.Count == 0)
            {
                return new ToolReport { Status = "error", Pid = pid, Message = "draw_batch needs at least one operation." };
            }

            int? before = TryHistory(pid);
            var batch = new StageBatchRequest(Guid.NewGuid().ToString("N"), ignoreSelection, expectedToken, rewritten);
            BridgeMessage raw;
            try
            {
                raw = Client(pid).RoundTrip(batch, () => driver.InvokeEffect(pid, "MCP Apply"), TimeSpan.FromSeconds(40));
            }
            catch (TimeoutException)
            {
                Drop(pid);
                try
                {
                    driver.PressEscape(pid);
                }
                catch (PaintUiException)
                {
                }

                if (before is int count && TryHistory(pid) is int after && after > count)
                {
                    driver.Undo(pid);
                    return new ToolReport { Status = "reverted", Pid = pid, Message = "Apply timed out. Escape was sent and the new history entry was undone." };
                }

                return new ToolReport { Status = "busy", Pid = pid, Message = "Apply timed out. Escape was sent. If a dialog is still open, close it before the next command." };
            }

            if (raw is not BatchResponse response)
            {
                return new ToolReport { Status = "error", Pid = pid, Message = "Unexpected bridge response." };
            }

            try
            {
                driver.DismissEffectDialog(pid, response.Committed);
            }
            catch (PaintUiException)
            {
            }

            if (!response.Committed)
            {
                string status = response.Error == "conflict" ? "conflict" : "error";
                return new ToolReport
                {
                    Status = status,
                    Pid = pid,
                    Message = response.Error == "conflict"
                        ? "The document changed since expectedToken was read. Call document_info and draw again."
                        : response.Error,
                    Document = response.Snapshot is null ? null : response.Snapshot with { Planes = [] },
                };
            }

            return new ToolReport
            {
                Status = "ok",
                Pid = pid,
                Message = "The batch is one History entry named MCP Apply.",
                Document = response.Snapshot is null ? null : response.Snapshot with { Planes = [] },
                DirtyHash = response.DirtyHash,
                DirtyBounds = response.DirtyBounds,
                PdnVersion = VersionOf(pid),
            };
        }
        finally
        {
            foreach (var slot in slots)
            {
                slot.Dispose();
            }
        }
    }

    private ToolReport SelectLayer(int pid, int index)
    {
        var start = Info(pid);
        if (start.Document is null)
        {
            return start;
        }

        if (index < 0 || index >= start.Document.Layers.Count)
        {
            return new ToolReport { Status = "error", Pid = pid, Message = $"Layer index {index} is outside 0..{start.Document.Layers.Count - 1}. The index is the order document_info returns." };
        }

        int current = start.Document.ActiveLayerIndex;
        if (current == index)
        {
            return start;
        }

        int distance = Math.Abs(index - current);
        int afterUp = Step(pid, pageUp: true);
        if (afterUp == index)
        {
            return Info(pid);
        }

        if (Math.Abs(index - afterUp) < distance)
        {
            return Walk(pid, index, pageUp: true, afterUp);
        }

        if (afterUp != current)
        {
            Step(pid, pageUp: false);
        }

        int afterDown = Step(pid, pageUp: false);
        if (afterDown == index)
        {
            return Info(pid);
        }

        if (Math.Abs(index - afterDown) < distance)
        {
            return Walk(pid, index, pageUp: false, afterDown);
        }

        return new ToolReport { Status = "error", Pid = pid, Message = "Go to Layer Above and Go to Layer Below did not move toward that layer index." };
    }

    private ToolReport Walk(int pid, int target, bool pageUp, int current)
    {
        for (int guard = 0; guard < 64 && current != target; guard++)
        {
            int next = Step(pid, pageUp);
            if (next == current)
            {
                return new ToolReport { Status = "verify_failed", Pid = pid, Message = "Layer navigation stopped before the requested index." };
            }

            if (Math.Abs(next - target) > Math.Abs(current - target))
            {
                return new ToolReport { Status = "verify_failed", Pid = pid, Message = "Layer navigation passed the requested index." };
            }

            current = next;
        }

        return Verify(pid, doc => doc.ActiveLayerIndex == target ? null : $"The active layer is {doc.ActiveLayerIndex}, expected {target}.");
    }

    private int Step(int pid, bool pageUp)
    {
        driver.LayerStep(pid, pageUp);
        var info = Info(pid);
        return info.Document?.ActiveLayerIndex ?? -1;
    }

    private ToolReport Verify(int pid, Func<DocumentSnapshot, string?> check)
    {
        var info = Info(pid);
        if (info.Status != "ok" || info.Document is null)
        {
            return info.Status == "ok"
                ? info with { Status = "verify_failed", Message = "Inspect did not return a document." }
                : info;
        }

        var problem = check(info.Document);
        return problem is null ? info : info with { Status = "verify_failed", Message = problem };
    }

    private ToolReport Inspect(int pid, PrepareInspectRequest request)
    {
        int? before = TryHistory(pid);
        BridgeMessage raw;
        try
        {
            raw = Client(pid).RoundTrip(request, () => driver.InvokeEffect(pid, "MCP Inspect"), TimeSpan.FromSeconds(40));
        }
        catch (TimeoutException)
        {
            Drop(pid);
            try
            {
                driver.PressEscape(pid);
            }
            catch (PaintUiException)
            {
            }

            return new ToolReport { Status = "busy", Pid = pid, Message = "Inspect timed out. Escape was sent." };
        }

        bool reverted = RevertIfGrew(pid, before);
        try
        {
            driver.DismissEffectDialog(pid, commit: false);
        }
        catch (PaintUiException)
        {
        }

        if (raw is ErrorResponse error)
        {
            return new ToolReport { Status = "error", Pid = pid, Message = error.Code + ": " + error.Message, InspectReverted = reverted };
        }

        if (raw is not InspectResponse inspect || !inspect.Ok || inspect.Snapshot is null)
        {
            return new ToolReport
            {
                Status = "error",
                Pid = pid,
                Message = raw is InspectResponse failed ? failed.Error : "Inspect failed.",
                InspectReverted = reverted,
            };
        }

        return new ToolReport
        {
            Status = "ok",
            Pid = pid,
            Document = inspect.Snapshot,
            InspectReverted = reverted,
            PdnVersion = VersionOf(pid),
            PluginVersion = handshakes.TryGetValue(pid, out var hello) ? hello.PluginVersion : null,
        };
    }

    private int? TryHistory(int pid)
    {
        try
        {
            return driver.HistoryCount(pid);
        }
        catch (PaintUiException)
        {
            return null;
        }
    }

    private bool RevertIfGrew(int pid, int? before)
    {
        if (before is not int count || TryHistory(pid) is not int after || after <= count)
        {
            return false;
        }

        driver.Undo(pid);
        return true;
    }

    private BridgeClient Client(int pid)
    {
        if (clients.TryGetValue(pid, out var existing))
        {
            return existing;
        }

        var record = readSessions().FirstOrDefault(item => item.Pid == pid)
            ?? throw new InvalidOperationException("The plugin discovery file disappeared.");
        var client = BridgeClient.Connect(record.Pipe, TimeSpan.FromSeconds(5));
        try
        {
            var hello = client.Handshake(TimeSpan.FromSeconds(5));
            if (hello.Protocol != ProtocolConstants.Version)
            {
                throw new InvalidOperationException($"Plugin protocol is {hello.Protocol}. This server speaks {ProtocolConstants.Version}. Reinstall the plugin.");
            }

            handshakes[pid] = hello;
            clients[pid] = client;
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private string? VersionOf(int pid) =>
        handshakes.TryGetValue(pid, out var hello) ? hello.PdnVersion : null;

    private void Drop(int pid)
    {
        if (clients.Remove(pid, out var client))
        {
            client.Dispose();
        }

        handshakes.Remove(pid);
    }

    private static string? CheckLayer(DocumentSnapshot doc, string? name, double? opacity, bool? visible, string? blendMode)
    {
        var active = doc.Layers.FirstOrDefault(layer => layer.Index == doc.ActiveLayerIndex);
        if (active is null)
        {
            return "The active layer is missing from document_info.";
        }

        if (name is not null && !string.Equals(active.Name, name, StringComparison.Ordinal))
        {
            return $"The layer name is '{active.Name}', expected '{name}'.";
        }

        if (visible is not null && active.Visible != visible.Value)
        {
            return $"The layer visibility is {active.Visible}, expected {visible.Value}.";
        }

        if (blendMode is not null && !SameBlend(active.BlendMode, blendMode))
        {
            return $"The blend mode is {active.BlendMode}, expected {blendMode}.";
        }

        if (opacity is not null && !SameOpacity(active.Opacity, opacity.Value))
        {
            return $"The layer opacity is {active.Opacity}, expected {opacity.Value} on a 0 to 1 scale.";
        }

        return null;
    }

    private static bool SameBlend(string actual, string expected) =>
        string.Equals(actual.Replace(" ", string.Empty), expected.Replace(" ", string.Empty), StringComparison.OrdinalIgnoreCase);

    private static bool SameOpacity(double actual, double expected) =>
        Math.Abs(actual - expected) <= 0.02 || Math.Abs(actual - (expected * 255)) <= 1.5;

    private static bool SameColor(string actual, string expected) =>
        string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
        || (actual.Length >= 7 && expected.Length >= 7
            && string.Equals(actual[..7], expected[..7], StringComparison.OrdinalIgnoreCase));

    private static ToolReport FromUi(PaintUiException ex, int? pid)
    {
        bool busy = ex.Message.Contains("dialog", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("mouse", StringComparison.OrdinalIgnoreCase);
        return new ToolReport { Status = busy ? "busy" : "error", Pid = pid, Message = ex.Message };
    }
}
