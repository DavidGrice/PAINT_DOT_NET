using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace PaintDotNet.Mcp.Protocol;

public static class SessionFiles
{
    public static string RootDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProtocolConstants.AppFolderName);

    public static string SessionDirectory => Path.Combine(RootDirectory, "sessions");

    public static string LogDirectory => Path.Combine(RootDirectory, "logs");

    public static string ImageDirectory => Path.Combine(RootDirectory, "images");

    /// <summary>Documents\paint.net App Files\Effects\PaintDotNet.Mcp, the per-user plugin folder.</summary>
    public static string UserPluginDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "paint.net App Files",
            "Effects",
            "PaintDotNet.Mcp");

    public static string PathFor(int pid) => Path.Combine(SessionDirectory, $"{pid}.json");

    public static void Write(DiscoveryRecord record)
    {
        Directory.CreateDirectory(SessionDirectory);
        SweepDead();
        var path = PathFor(record.Pid);
        var json = JsonSerializer.Serialize(record, ProtocolJson.Options);
        File.WriteAllText(path, json);
        RestrictToCurrentUser(path);
    }

    private static void RestrictToCurrentUser(string path)
    {
        var user = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("The current process has no Windows user, so the discovery file cannot be locked down.");
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    public static void Delete(int pid)
    {
        var path = PathFor(pid);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public static IReadOnlyList<DiscoveryRecord> ReadAlive()
    {
        SweepDead();
        if (!Directory.Exists(SessionDirectory))
        {
            return [];
        }

        var list = new List<DiscoveryRecord>();
        foreach (var file in Directory.EnumerateFiles(SessionDirectory, "*.json"))
        {
            try
            {
                var record = JsonSerializer.Deserialize<DiscoveryRecord>(File.ReadAllText(file), ProtocolJson.Options);
                if (record is not null && IsPaintDotNetProcess(record.Pid))
                {
                    list.Add(record);
                }
            }
            catch (JsonException)
            {
                // Leave a corrupt file for the next sweep to remove once its pid is gone.
            }
        }

        return list;
    }

    public static void SweepDead()
    {
        if (!Directory.Exists(SessionDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(SessionDirectory, "*.json"))
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out int pid) || !IsPaintDotNetProcess(pid))
            {
                TryDelete(file);
            }
        }
    }

    public static bool IsPaintDotNetProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited
                && process.ProcessName.Contains("paintdotnet", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
