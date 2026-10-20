namespace PaintDotNet.Mcp.Protocol;

public static class ProtocolConstants
{
    public const int Version = 1;
    public const int MaxMessageBytes = 1024 * 1024;
    public const string AppFolderName = "PaintDotNetMcp";
    public const string PluginVersion = "0.1.0";
    public const int DefaultMaxPixels = 16_000_000;
    public const int HardMaxPixels = 80_000_000;
}
