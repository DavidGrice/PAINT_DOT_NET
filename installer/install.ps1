# Installs the Paint.NET MCP plugin and publishes the MCP server.
# Does not start Paint.NET. Does not write under Program Files unless -System is passed.
param(
    [switch]$System,
    [switch]$Register
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$pluginProject = Join-Path $root "src\PaintDotNet.Mcp.Plugin\PaintDotNet.Mcp.Plugin.csproj"
$serverProject = Join-Path $root "src\PaintDotNet.Mcp.Server\PaintDotNet.Mcp.Server.csproj"

Write-Host "Building the plugin and the MCP server..."
dotnet build $pluginProject -c Release --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$serverDir = Join-Path $env:LOCALAPPDATA "PaintDotNetMcp\server"
dotnet publish $serverProject -c Release -o $serverDir --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$pluginOut = Join-Path $root "src\PaintDotNet.Mcp.Plugin\bin\Release\net9.0-windows"
if ($System) {
    $paintDir = $env:PAINTDOTNET_DIR
    if ([string]::IsNullOrWhiteSpace($paintDir)) { $paintDir = "C:\Program Files\paint.net" }
    $dest = Join-Path $paintDir "Effects\PaintDotNet.Mcp"
} else {
    $docs = [Environment]::GetFolderPath("MyDocuments")
    $dest = Join-Path $docs "paint.net App Files\Effects\PaintDotNet.Mcp"
}

New-Item -ItemType Directory -Force -Path $dest | Out-Null
Get-ChildItem $pluginOut -File | Where-Object {
    $_.Extension -in ".dll", ".json", ".pdb" -and $_.Name -notmatch "^PaintDotNet\.(?!Mcp)"
} | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $dest $_.Name) -Force
    Write-Host "Copied $($_.Name)"
}

$serverDll = Join-Path $serverDir "PaintDotNet.Mcp.Server.dll"
Write-Host ""
Write-Host "Plugin folder: $dest"
Write-Host "Server: $serverDll"
Write-Host "Restart Paint.NET so it scans the plugin."
Write-Host ""
Write-Host "Grok config:"
Write-Host "[mcp_servers.paintdotnet]"
Write-Host 'command = "dotnet"'
Write-Host "args = [""exec"", '$serverDll']"
Write-Host "startup_timeout_sec = 60"
Write-Host "enabled = true"

if ($Register) {
    $config = Join-Path $env:USERPROFILE ".grok\config.toml"
    $existing = ""
    if (Test-Path $config) { $existing = Get-Content $config -Raw }
    if ($existing -match "(?m)^\[mcp_servers\.paintdotnet\]") {
        Write-Host "config.toml already has [mcp_servers.paintdotnet]. Left it unchanged."
    } else {
        $block = @"

[mcp_servers.paintdotnet]
command = "dotnet"
args = ["exec", '$serverDll']
startup_timeout_sec = 60
enabled = true
"@
        Add-Content -Path $config -Value $block -Encoding utf8
        Write-Host "Added [mcp_servers.paintdotnet] to $config"
    }
}
