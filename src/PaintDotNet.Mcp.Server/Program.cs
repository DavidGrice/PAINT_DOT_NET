using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PaintDotNet.Mcp.Server;
using PaintDotNet.Mcp.UiDriver;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<IPaintDriver, FlaUiPaintDriver>();
builder.Services.AddSingleton<PaintSession>();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<PaintTools>();

var app = builder.Build();
app.Services.GetRequiredService<ILoggerFactory>()
    .CreateLogger("PaintDotNet.Mcp")
    .LogInformation("Paint.NET MCP server is starting.");
await app.RunAsync();
