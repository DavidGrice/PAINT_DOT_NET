using System.Diagnostics;
using PaintDotNet.Mcp.Protocol;
using PaintDotNet.Mcp.Raster;
using RasterSurface = PaintDotNet.Mcp.Raster.Surface;

namespace PaintDotNet.Mcp.Plugin;

public static class BridgeRuntime
{
    private static readonly object Gate = new();
    private static BridgeEndpoint? endpoint;
    private static string? seenIdentity;
    private static long generation = 1;
    private static readonly List<PixelSlot> slots = [];

    public static BridgeMailbox Mailbox { get; } = new();

    public static void EnsureStarted()
    {
        lock (Gate)
        {
            if (endpoint is not null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(SessionFiles.LogDirectory);
                int pid = Environment.ProcessId;
                string pipe = $"pdn-mcp-{pid}";
                endpoint = new BridgeEndpoint(Mailbox, ProductVersion);
                endpoint.Start(pipe);
                SessionFiles.Write(new DiscoveryRecord(
                    pid,
                    pipe,
                    ProtocolConstants.Version,
                    ProductVersion(),
                    ProtocolConstants.PluginVersion,
                    ["MCP Inspect", "MCP Apply"]));
                AppDomain.CurrentDomain.ProcessExit += (_, _) => Shutdown();
                AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                    Log("unhandled " + args.ExceptionObject);
                Log("Listening on " + pipe);
            }
            catch (Exception ex)
            {
                Log(ex.ToString());
            }
        }
    }

    public static long CurrentGeneration
    {
        get
        {
            lock (Gate)
            {
                return generation;
            }
        }
    }

    public static long Observe(string identity)
    {
        lock (Gate)
        {
            if (!string.Equals(seenIdentity, identity, StringComparison.Ordinal))
            {
                seenIdentity = identity;
                generation++;
            }

            return generation;
        }
    }

    public static long Commit(string identity)
    {
        lock (Gate)
        {
            seenIdentity = identity;
            return ++generation;
        }
    }

    public static void Hold(PixelSlot slot)
    {
        lock (Gate)
        {
            slots.Add(slot);
        }
    }

    public static void ReleaseSlots()
    {
        List<PixelSlot> previous;
        lock (Gate)
        {
            previous = slots.ToList();
            slots.Clear();
        }

        foreach (var slot in previous)
        {
            slot.Dispose();
        }
    }

    public static RasterSurface? OpenSlot(string slot)
    {
        try
        {
            using var map = PixelSlot.Open(PixelSlot.MapName(Environment.ProcessId, slot));
            return new RasterSurface(map.Width, map.Height, map.ReadPixels());
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(SessionFiles.LogDirectory);
            var path = Path.Combine(SessionFiles.LogDirectory, $"plugin-{Environment.ProcessId}.log");
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    private static void Shutdown()
    {
        try
        {
            Log("process-exit");
            SessionFiles.Delete(Environment.ProcessId);
            endpoint?.Dispose();
        }
        catch (Exception ex)
        {
            Log(ex.ToString());
        }
    }

    private static string ProductVersion()
    {
        var path = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(path))
        {
            return "unknown";
        }

        return FileVersionInfo.GetVersionInfo(path).ProductVersion ?? "unknown";
    }
}
