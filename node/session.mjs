import fs from "node:fs";
import path from "node:path";
import { randomUUID } from "node:crypto";
import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";
import { Bridge } from "./bridge.mjs";

const protocolVersion = 1;
const defaultMaxPixels = 16_000_000;
const hardMaxPixels = 80_000_000;

const here = path.dirname(fileURLToPath(import.meta.url));

function asArray(value) {
  if (value == null) return [];
  return Array.isArray(value) ? value : [value];
}

function parseColor(input) {
  const match = /^#([0-9a-fA-F]{3}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$/.exec(String(input ?? "").trim());
  if (!match) return null;
  let hex = match[1];
  if (hex.length === 3) hex = hex.split("").map((ch) => ch + ch).join("");
  if (hex.length === 6) hex += "FF";
  return `#${hex.toUpperCase()}`;
}

function sameColor(actual, expected) {
  if (!actual || !expected) return false;
  if (actual.toLowerCase() === expected.toLowerCase()) return true;
  return actual.length >= 7 && expected.length >= 7 && actual.slice(0, 7).toLowerCase() === expected.slice(0, 7).toLowerCase();
}

function sameBlend(actual, expected) {
  return actual.replaceAll(" ", "").toLowerCase() === expected.replaceAll(" ", "").toLowerCase();
}

function sameOpacity(actual, expected) {
  return Math.abs(actual - expected) <= 0.02 || Math.abs(actual - expected * 255) <= 1.5;
}

function withoutPlanes(document) {
  if (!document) return document;
  return { ...document, planes: [] };
}

export class PaintSession {
  constructor() {
    this.clients = new Map();
    this.handshakes = new Map();
    this.chain = Promise.resolve();
    this.host = startHost();
    this.pluginPath = null;
  }

  async paths() {
    if (this.pluginPath) return this.pluginPath;
    const result = await this.host.call({ cmd: "paths" });
    this.pluginPath = result.plugin;
    this.localAppData = result.localAppData;
    return this.pluginPath;
  }

  run(work) {
    const run = this.chain.then(work, work);
    this.chain = run.then(() => {}, () => {});
    return run;
  }

  async status(args) {
    return this.run(() => this.withPid(args, false, async (pid) => {
      const sessions = this.readSessions();
      const match = sessions.find((item) => item.pid === pid);
      if (!match) return this.pluginMissing(pid);
      let title = null;
      let modalOpen = null;
      let modalText = null;
      let message = null;
      try {
        const state = await this.host.call({ cmd: "readState", pid });
        title = state.title;
        modalOpen = state.modalOpen;
        modalText = state.modalText;
      } catch (error) {
        message = error.message;
      }
      return {
        status: "ok",
        message,
        pid,
        pdnVersion: match.pdnVersion,
        pluginVersion: match.pluginVersion,
        windowTitle: title,
        modalOpen,
        modalText,
        instances: await this.listInstances(),
      };
    }));
  }

  documentInfo(args) {
    return this.run(() => this.withPid(args, true, (pid) => this.info(pid)));
  }

  documentSnapshot(args) {
    return this.run(() => this.withPid(args, true, (pid) => this.snapshot(pid, args)));
  }

  drawBatch(args) {
    return this.run(() => this.withPid(args, true, (pid) => this.draw(pid, args)));
  }

  historyUndo(args) {
    return this.run(() => this.withPid(args, true, async (pid) => {
      await this.host.call({ cmd: "menu", pid, top: "Edit", item: "Undo" });
      return this.info(pid);
    }));
  }

  historyRedo(args) {
    return this.run(() => this.withPid(args, true, async (pid) => {
      await this.host.call({ cmd: "menu", pid, top: "Edit", item: "Redo" });
      return this.info(pid);
    }));
  }

  documentCreate(args) {
    return this.run(() => this.withPid(args, true, async (pid) => {
      await this.host.call({ cmd: "newDocument", pid, width: args.width, height: args.height }, 20000);
      return this.verify(pid, (doc) => doc.width === args.width && doc.height === args.height
        ? null
        : `The document is ${doc.width}x${doc.height} after New.`);
    }));
  }

  documentOpen(args) {
    return this.run(() => this.withPid(args, true, async (pid) => {
      await this.host.call({ cmd: "openPath", pid, path: args.path }, 20000);
      const modal = await this.host.call({ cmd: "modalText", pid });
      if (modal.text) {
        return { status: "error", pid, message: "Paint.NET showed a dialog after Open. It was left open. " + modal.text, modalOpen: true, modalText: modal.text };
      }
      const name = path.basename(args.path);
      return this.verify(pid, async () => {
        const state = await this.host.call({ cmd: "readState", pid });
        return state.title && state.title.toLowerCase().includes(name.toLowerCase()) ? null : `The window title does not contain ${name}.`;
      });
    }));
  }

  documentSave(args) {
    return this.run(() => this.withPid(args, true, async (pid) => {
      const saved = await this.host.call({ cmd: "save", pid });
      if (!saved.saved) {
        return { status: "error", pid, message: "This document has no file name yet. Call document_save_as with a path." };
      }
      return this.info(pid);
    }));
  }

  documentSaveAs(args) {
    return this.run(() => this.withPid(args, true, async (pid) => {
      if (fs.existsSync(args.path) && !args.confirm) {
        return { status: "error", pid, message: `Refused to overwrite ${args.path}. Call again with confirm=true.` };
      }
      await this.host.call({ cmd: "saveAs", pid, path: args.path, confirm: !!args.confirm }, 20000);
      if (!fs.existsSync(args.path)) {
        return { status: "verify_failed", pid, message: `Save As finished but ${args.path} is not on disk.` };
      }
      const info = await this.info(pid);
      return { ...info, message: "Saved " + args.path };
    }));
  }

  documentClose(args) {
    return this.run(() => this.withPid(args, true, async (pid) => {
      if (args.discard && !args.confirm) {
        return { status: "error", pid, message: "Refused to discard unsaved changes. Call again with discard=true and confirm=true, or with save=true." };
      }
      const result = await this.host.call({ cmd: "closeDocument", pid, save: !!args.save, discard: !!(args.discard && args.confirm) });
      if (result.prompt === "dirty") {
        return { status: "dirty", pid, message: "The document has unsaved changes. The save prompt was left open. Call again with save=true, or with discard=true and confirm=true.", modalOpen: true };
      }
      if (result.prompt === "saved") return { status: "ok", pid, message: "The document was saved and closed." };
      if (result.prompt === "discarded") return { status: "ok", pid, message: "The document was closed and the changes were discarded." };
      return { status: "ok", pid, message: "The document was closed." };
    }));
  }

  layerAdd(args) {
    return this.changeLayerCount(args, "Add New Layer", 1, null);
  }

  layerDelete(args) {
    if (!args.confirm) {
      return this.run(() => this.withPid(args, true, async (pid) => ({ status: "error", pid, message: "Refused to delete the active layer. Call again with confirm=true." })));
    }
    return this.changeLayerCount(args, "Delete Layer", -1, (doc) => doc.layers.length <= 1 ? "Refused to delete the only layer." : null);
  }

  layerDuplicate(args) {
    return this.changeLayerCount(args, "Duplicate Layer", 1, null);
  }

  layerMergeDown(args) {
    if (!args.confirm) {
      return this.run(() => this.withPid(args, true, async (pid) => ({ status: "error", pid, message: "Refused to merge the active layer down. Call again with confirm=true." })));
    }
    return this.changeLayerCount(args, "Merge Layer Down", -1, null);
  }

  layerSelect(args) {
    return this.run(() => this.withPid(args, true, (pid) => this.selectLayer(pid, args.index)));
  }

  layerProperties(args) {
    return this.run(() => this.withPid(args, true, async (pid) => {
      const before = await this.info(pid);
      if (!before.document) return before;
      await this.host.call({
        cmd: "layerProperties",
        pid,
        name: args.name ?? null,
        opacity: args.opacity ?? null,
        visible: args.visible ?? null,
        blendMode: args.blendMode ?? null,
      }, 20000);
      return this.verify(pid, (doc) => this.checkLayer(doc, args));
    }));
  }

  selectionAll(args) {
    return this.run(() => this.withPid(args, true, async (pid) => {
      await this.host.call({ cmd: "menu", pid, top: "Edit", item: "Select All" });
      return this.verify(pid, (doc) => doc.selectionCoversDocument ? null : "The selection does not cover the document.");
    }));
  }

  selectionNone(args) {
    return this.run(() => this.withPid(args, true, async (pid) => {
      await this.host.call({ cmd: "menu", pid, top: "Edit", item: "Deselect" });
      return this.verify(pid, (doc) => doc.selectionBounds.width <= 0 || doc.selectionBounds.height <= 0 || !doc.selectionCoversDocument
        ? null
        : "The selection still covers the document.");
    }));
  }

  selectionInvert(args) {
    return this.run(() => this.withPid(args, true, async (pid) => {
      await this.host.call({ cmd: "menu", pid, top: "Edit", item: "Invert Selection" });
      return this.info(pid);
    }));
  }

  selectionCrop(args) {
    return this.run(() => this.withPid(args, true, async (pid) => {
      if (!args.confirm) return { status: "error", pid, message: "Refused to crop to the selection. Call again with confirm=true." };
      const before = await this.info(pid);
      if (!before.document) return before;
      const bounds = before.document.selectionBounds;
      if (!bounds || bounds.width <= 0 || bounds.height <= 0) return { status: "error", pid, message: "There is no selection to crop." };
      await this.host.call({ cmd: "menu", pid, top: "Image", item: "Crop to Selection" });
      return this.verify(pid, (doc) => doc.width === bounds.width && doc.height === bounds.height
        ? null
        : `The document is ${doc.width}x${doc.height} after crop, expected ${bounds.width}x${bounds.height}.`);
    }));
  }

  colorSet(args) {
    return this.run(() => this.withPid(args, true, async (pid) => {
      const formatted = parseColor(args.color);
      if (!formatted) return { status: "error", pid, message: "Color must be #RGB, #RRGGBB, or #RRGGBBAA." };
      const primary = args.primary !== false;
      await this.host.call({ cmd: "color", pid, hex: formatted, primary });
      return this.verify(pid, (doc) => {
        const actual = primary ? doc.primaryColor : doc.secondaryColor;
        return sameColor(actual, formatted) ? null : `The color is ${actual}, expected ${formatted}.`;
      });
    }));
  }

  async changeLayerCount(args, item, delta, refuse) {
    return this.run(() => this.withPid(args, true, async (pid) => {
      const before = await this.info(pid);
      if (!before.document) return before;
      if (refuse) {
        const reason = refuse(before.document);
        if (reason) return { status: "error", pid, message: reason };
      }
      const count = before.document.layers.length;
      await this.host.call({ cmd: "menu", pid, top: "Layers", item });
      const expected = count + delta;
      return this.verify(pid, (doc) => doc.layers.length === expected ? null : `Layer count is ${doc.layers.length}, expected ${expected}.`);
    }));
  }

  async withPid(args, requireSession, action) {
    try {
      if (args.document) {
        // Document switching is applied after the pid is known.
      }
      const resolved = await this.resolve(args.pid);
      if (resolved.report) return resolved.report;
      const pid = resolved.pid;
      if (requireSession && !this.readSessions().some((item) => item.pid === pid)) return this.pluginMissing(pid);
      if (args.document) {
        const state = await this.host.call({ cmd: "readState", pid });
        if (!state.title || !state.title.toLowerCase().includes(String(args.document).toLowerCase())) {
          return { status: "error", pid, message: `The active document title is '${state.title}'. Pass the open document, or close the other documents. Menu switching is not used while another application may hold the foreground.` };
        }
      }
      return await action(pid);
    } catch (error) {
      const message = error.message || String(error);
      const busy = /dialog|mouse/i.test(message);
      return { status: busy ? "busy" : "error", pid: args.pid ?? null, message };
    }
  }

  async resolve(pid) {
    const processes = await this.processes();
    const sessions = this.readSessions();
    if (pid != null) {
      const alive = processes.includes(pid) || sessions.some((item) => item.pid === pid);
      if (!alive) return { report: { status: "not_running", pid, message: "That Paint.NET process is not running.", instances: await this.listInstances() } };
      if (!sessions.some((item) => item.pid === pid)) return { report: await this.pluginMissing(pid) };
      return { pid };
    }
    if (processes.length === 0 && sessions.length === 0) {
      return { report: { status: "not_running", message: "Paint.NET is not running. v1 does not launch it.", instances: [] } };
    }
    if (sessions.length === 1) return { pid: sessions[0].pid };
    if (sessions.length === 0 && processes.length === 1) return { report: await this.pluginMissing(processes[0]) };
    return { report: { status: "error", message: "More than one Paint.NET instance is open. Pass pid.", instances: await this.listInstances() } };
  }

  async processes() {
    const result = await this.host.call({ cmd: "processes" });
    return asArray(result.pids).map((id) => Number(id));
  }

  readSessions() {
    const directory = path.join(process.env.LOCALAPPDATA || "", "PaintDotNetMcp", "sessions");
    if (!fs.existsSync(directory)) return [];
    const records = [];
    for (const name of fs.readdirSync(directory)) {
      if (!name.endsWith(".json")) continue;
      try {
        const record = JSON.parse(fs.readFileSync(path.join(directory, name), "utf8"));
        if (record && Number.isInteger(record.pid)) records.push(record);
      } catch {
        // A session file can be deleted while Paint.NET exits.
      }
    }
    return records;
  }

  async listInstances() {
    const sessions = this.readSessions();
    const ids = [...new Set([...(await this.processes()), ...sessions.map((item) => item.pid)])];
    return ids.map((id) => {
      const session = sessions.find((item) => item.pid === id);
      return { pid: id, pdnVersion: session?.pdnVersion ?? null, pluginVersion: session?.pluginVersion ?? null, pluginInstalled: !!session };
    });
  }

  async pluginMissing(pid) {
    const pluginPath = await this.paths();
    return {
      status: "plugin_not_installed",
      pid,
      pluginPath,
      message: "Paint.NET is running without the MCP plugin. Run installer/install.ps1 and restart Paint.NET. Plugin folder: " + pluginPath,
    };
  }

  async info(pid) {
    const report = await this.inspect(pid, {
      type: "prepareInspect",
      requestId: randomUUID().replaceAll("-", ""),
      includeComposite: false,
      includeActiveLayer: false,
      layerIndex: null,
      region: null,
      maxPixels: 1,
    });
    if (!report.document) return report;
    return { ...report, document: withoutPlanes(report.document) };
  }

  async snapshot(pid, args) {
    const kind = String(args.kind || "composite").toLowerCase();
    const hasRegion = [args.x, args.y, args.width, args.height].some((value) => value != null);
    let region = null;
    if (hasRegion) {
      if ([args.x, args.y, args.width, args.height].some((value) => value == null)) {
        return { status: "error", pid, message: "Pass all of x, y, width, and height for a region, or none of them." };
      }
      region = { x: args.x, y: args.y, width: args.width, height: args.height };
      if (region.width <= 0 || region.height <= 0) return { status: "error", pid, message: "The snapshot region must have a positive width and height." };
      const area = region.width * region.height;
      if (area > hardMaxPixels && !args.allowLarge) {
        return { status: "too_large", pid, message: `The region is ${area} pixels. The hard cap is ${hardMaxPixels}. Pass allowLarge or a smaller region.` };
      }
      if (area > defaultMaxPixels && !args.allowLarge) {
        return { status: "too_large", pid, message: `The region is ${area} pixels. The default cap is ${defaultMaxPixels}. Pass allowLarge (up to ${hardMaxPixels}) or a smaller region.` };
      }
    }
    if (!["composite", "active", "layer"].includes(kind)) return { status: "error", pid, message: "kind must be composite, active, or layer." };
    if (kind === "layer" && args.layerIndex == null) {
      return { status: "error", pid, message: "layerIndex is required when kind is layer. The index is the order document_info returns." };
    }
    let maxPixels = args.allowLarge ? hardMaxPixels : defaultMaxPixels;
    if (region && args.allowLarge) maxPixels = Math.max(maxPixels, region.width * region.height);
    const report = await this.inspect(pid, {
      type: "prepareInspect",
      requestId: randomUUID().replaceAll("-", ""),
      includeComposite: kind === "composite",
      includeActiveLayer: kind === "active",
      layerIndex: kind === "layer" ? args.layerIndex : null,
      region,
      maxPixels,
    });
    if (report.status !== "ok" || !report.document) {
      if (report.message && /pixel cap/i.test(report.message)) return { ...report, status: "too_large" };
      return report;
    }
    const plane = (report.document.planes || [])[0];
    if (!plane) return { ...report, status: "error", message: "Inspect returned no pixel plane." };
    const directory = path.join(process.env.LOCALAPPDATA || "", "PaintDotNetMcp", "images", String(pid));
    fs.mkdirSync(directory, { recursive: true });
    const stamp = new Date().toISOString().replace(/[-:.TZ]/g, "").slice(0, 17);
    const imagePath = path.join(directory, `${stamp}-${plane.kind}.png`);
    const previewPath = path.join(directory, `${stamp}-${plane.kind}-preview.png`);
    try {
      const saved = await this.host.call({
        cmd: "readSlot",
        map: plane.slot,
        imagePath,
        previewPath,
        imageLongEdge: args.full ? 100000 : 1024,
        previewLongEdge: 512,
      }, 20000);
      return {
        ...report,
        document: withoutPlanes(report.document),
        imagePath,
        previewPath,
        imageWidth: saved.width,
        imageHeight: saved.height,
      };
    } catch (error) {
      return { status: "error", pid, message: "The pixel plane could not be read. " + error.message };
    }
  }

  async draw(pid, args) {
    if (!args.expectedToken || !String(args.expectedToken).trim()) {
      return { status: "error", pid, message: "Pass expectedToken from the documentToken returned by document_info." };
    }
    let ops;
    try {
      ops = typeof args.ops === "string" ? JSON.parse(args.ops) : args.ops;
    } catch (error) {
      return { status: "error", pid, message: "ops is not a JSON array of draw operations. " + error.message };
    }
    if (!Array.isArray(ops) || ops.length === 0) return { status: "error", pid, message: "draw_batch needs at least one operation." };
    const rewritten = [];
    try {
      for (const op of ops) {
        if (op.op === "blit") {
          const slot = "blit-" + randomUUID().replaceAll("-", "");
          const created = await this.host.call({ cmd: "createSlot", pid, slot, png: op.path }, 20000);
          rewritten.push({ op: "stamp", slot, x: op.x, y: op.y, blend: op.blend || "src_over" });
          void created;
        } else if (op.op === "pixels") {
          const slot = "pixels-" + randomUUID().replaceAll("-", "");
          await this.host.call({ cmd: "createSlot", pid, slot, raw: op.path, width: op.width, height: op.height }, 20000);
          rewritten.push({ op: "stamp", slot, x: op.x, y: op.y, blend: "replace" });
        } else {
          rewritten.push(op);
        }
      }
      const before = await this.tryHistory(pid);
      let raw;
      try {
        raw = await this.client(pid).then((bridge) => bridge.roundTrip({
          type: "stageBatch",
          batchId: randomUUID().replaceAll("-", ""),
          ignoreSelection: !!args.ignoreSelection,
          expectedToken: args.expectedToken,
          ops: rewritten,
        }, () => this.host.call({ cmd: "effect", pid, name: "MCP Apply" }, 20000), 40000));
      } catch (error) {
        this.drop(pid);
        try { await this.host.call({ cmd: "dismiss", pid, commit: false }); } catch { /* The dialog may already be gone. */ }
        if (before != null) {
          const after = await this.tryHistory(pid);
          if (after != null && after > before) {
            await this.host.call({ cmd: "menu", pid, top: "Edit", item: "Undo" });
            return { status: "reverted", pid, message: "Apply timed out. Escape was sent and the new history entry was undone." };
          }
        }
        return { status: "busy", pid, message: "Apply timed out. Escape was sent. If a dialog is still open, close it before the next command." };
      }
      try { await this.host.call({ cmd: "dismiss", pid, commit: !!raw.committed }); } catch { /* RequestClose may have closed it. */ }
      if (raw.type !== "batchResult") return { status: "error", pid, message: "Unexpected bridge response." };
      if (!raw.committed) {
        const status = raw.error === "conflict" ? "conflict" : "error";
        return {
          status,
          pid,
          message: raw.error === "conflict" ? "The document changed since expectedToken was read. Call document_info and draw again." : raw.error,
          document: withoutPlanes(raw.snapshot),
        };
      }
      return {
        status: "ok",
        pid,
        message: "The batch is one History entry named MCP Apply.",
        document: withoutPlanes(raw.snapshot),
        dirtyHash: raw.dirtyHash ?? null,
        dirtyBounds: raw.dirtyBounds ?? null,
        pdnVersion: this.handshakes.get(pid)?.pdnVersion ?? null,
      };
    } finally {
      try { await this.host.call({ cmd: "disposeSlots" }); } catch { /* The slot helper is restarted on the next call if this failed. */ }
    }
  }

  async selectLayer(pid, index) {
    const start = await this.info(pid);
    if (!start.document) return start;
    if (index < 0 || index >= start.document.layers.length) {
      return { status: "error", pid, message: `Layer index ${index} is outside 0..${start.document.layers.length - 1}. The index is the order document_info returns.` };
    }
    let current = start.document.activeLayerIndex;
    if (current === index) return start;
    const distance = Math.abs(index - current);
    const afterUp = await this.step(pid, true);
    if (afterUp === index) return this.info(pid);
    if (Math.abs(index - afterUp) < distance) return this.walk(pid, index, true, afterUp);
    if (afterUp !== current) await this.step(pid, false);
    const afterDown = await this.step(pid, false);
    if (afterDown === index) return this.info(pid);
    if (Math.abs(index - afterDown) < distance) return this.walk(pid, index, false, afterDown);
    return { status: "error", pid, message: "Go to Layer Above and Go to Layer Below did not move toward that layer index." };
  }

  async walk(pid, target, pageUp, current) {
    for (let guard = 0; guard < 64 && current !== target; guard++) {
      const next = await this.step(pid, pageUp);
      if (next === current) return { status: "verify_failed", pid, message: "Layer navigation stopped before the requested index." };
      if (Math.abs(next - target) > Math.abs(current - target)) {
        return { status: "verify_failed", pid, message: "Layer navigation passed the requested index." };
      }
      current = next;
    }
    return this.verify(pid, (doc) => doc.activeLayerIndex === target ? null : `The active layer is ${doc.activeLayerIndex}, expected ${target}.`);
  }

  async step(pid, pageUp) {
    await this.host.call({ cmd: "menu", pid, top: "Layers", item: pageUp ? "Go to Layer Above" : "Go to Layer Below" });
    const info = await this.info(pid);
    return info.document ? info.document.activeLayerIndex : -1;
  }

  async verify(pid, check) {
    const info = await this.info(pid);
    if (info.status !== "ok" || !info.document) {
      return info.status === "ok" ? { ...info, status: "verify_failed", message: "Inspect did not return a document." } : info;
    }
    const problem = await check(info.document);
    return problem ? { ...info, status: "verify_failed", message: problem } : info;
  }

  async inspect(pid, request) {
    const before = await this.tryHistory(pid);
    let raw;
    try {
      const bridge = await this.client(pid);
      raw = await bridge.roundTrip(request, () => this.host.call({ cmd: "effect", pid, name: "MCP Inspect" }, 20000), 40000);
    } catch {
      this.drop(pid);
      try { await this.host.call({ cmd: "dismiss", pid, commit: false }); } catch { /* The dialog may already be gone. */ }
      return { status: "busy", pid, message: "Inspect timed out. Escape was sent." };
    }
    const reverted = await this.revertIfGrew(pid, before);
    try { await this.host.call({ cmd: "dismiss", pid, commit: false }); } catch { /* RequestClose may have closed it. */ }
    if (raw.type === "error") {
      return { status: "error", pid, message: `${raw.code}: ${raw.message}`, inspectReverted: reverted };
    }
    if (raw.type !== "inspectResult" || !raw.ok || !raw.snapshot) {
      return { status: "error", pid, message: raw.error || "Inspect failed.", inspectReverted: reverted };
    }
    const hello = this.handshakes.get(pid);
    return {
      status: "ok",
      pid,
      document: raw.snapshot,
      inspectReverted: reverted,
      pdnVersion: hello?.pdnVersion ?? null,
      pluginVersion: hello?.pluginVersion ?? null,
    };
  }

  async tryHistory(pid) {
    try {
      const result = await this.host.call({ cmd: "undoEnabled", pid });
      return result.count == null ? null : Number(result.count);
    } catch {
      return null;
    }
  }

  async revertIfGrew(pid, before) {
    if (before == null) return false;
    const after = await this.tryHistory(pid);
    if (after == null || after <= before) return false;
    await this.host.call({ cmd: "menu", pid, top: "Edit", item: "Undo" });
    return true;
  }

  async client(pid) {
    const existing = this.clients.get(pid);
    if (existing) return existing;
    const record = this.readSessions().find((item) => item.pid === pid);
    if (!record) throw new Error("The plugin discovery file disappeared.");
    const bridge = new Bridge(record.pipe);
    try {
      await bridge.connect(5000);
      const hello = await bridge.roundTrip({ type: "handshake", protocol: protocolVersion }, null, 5000);
      if (hello.type === "error") throw new Error(`${hello.code}: ${hello.message}`);
      if (hello.protocol !== protocolVersion) {
        throw new Error(`Plugin protocol is ${hello.protocol}. This server speaks ${protocolVersion}. Reinstall the plugin.`);
      }
      this.handshakes.set(pid, hello);
      this.clients.set(pid, bridge);
      return bridge;
    } catch (error) {
      bridge.destroy();
      throw error;
    }
  }

  drop(pid) {
    const bridge = this.clients.get(pid);
    if (bridge) bridge.destroy();
    this.clients.delete(pid);
    this.handshakes.delete(pid);
  }

  checkLayer(doc, args) {
    const active = (doc.layers || []).find((layer) => layer.index === doc.activeLayerIndex);
    if (!active) return "The active layer is missing from document_info.";
    if (args.name != null && active.name !== args.name) return `The layer name is '${active.name}', expected '${args.name}'.`;
    if (args.visible != null && active.visible !== args.visible) return `The layer visibility is ${active.visible}, expected ${args.visible}.`;
    if (args.blendMode != null && !sameBlend(active.blendMode, args.blendMode)) return `The blend mode is ${active.blendMode}, expected ${args.blendMode}.`;
    if (args.opacity != null && !sameOpacity(active.opacity, args.opacity)) return `The layer opacity is ${active.opacity}, expected ${args.opacity} on a 0 to 1 scale.`;
    return null;
  }
}

function startHost() {
  const child = spawn(
    path.join(process.env.SystemRoot || "C:\\Windows", "System32", "WindowsPowerShell", "v1.0", "powershell.exe"),
    ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", path.join(here, "host.ps1")],
    { stdio: ["pipe", "pipe", "pipe"], windowsHide: true },
  );
  const pending = new Map();
  let nextId = 0;
  let buffer = "";
  child.stdout.on("data", (chunk) => {
    buffer += chunk.toString("utf8");
    let newline = buffer.indexOf("\n");
    while (newline >= 0) {
      const line = buffer.slice(0, newline).trim();
      buffer = buffer.slice(newline + 1);
      newline = buffer.indexOf("\n");
      if (!line) continue;
      let message;
      try {
        message = JSON.parse(line);
      } catch (error) {
        process.stderr.write(`host sent a non-JSON line: ${line}\n`);
        continue;
      }
      const waiter = pending.get(message.id);
      if (!waiter) continue;
      pending.delete(message.id);
      clearTimeout(waiter.timer);
      if (message.ok) waiter.resolve(message.result ?? {});
      else waiter.reject(new Error(message.error || "The Paint.NET UI host failed."));
    }
  });
  child.stderr.on("data", (chunk) => process.stderr.write(chunk));
  child.on("exit", (code) => {
    const error = new Error(`The Paint.NET UI host exited (${code}).`);
    for (const waiter of pending.values()) {
      clearTimeout(waiter.timer);
      waiter.reject(error);
    }
    pending.clear();
  });
  return {
    call(command, timeoutMs = 15000) {
      const id = ++nextId;
      return new Promise((resolve, reject) => {
        const timer = setTimeout(() => {
          pending.delete(id);
          reject(new Error(`The Paint.NET UI host timed out during ${command.cmd}.`));
        }, timeoutMs);
        pending.set(id, { resolve, reject, timer });
        child.stdin.write(JSON.stringify({ id, ...command }) + "\n");
      });
    },
  };
}

