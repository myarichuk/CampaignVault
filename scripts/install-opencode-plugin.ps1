<#
.SYNOPSIS
    Installs the campaign-vault opencode plugin into a target folder.

.DESCRIPTION
    Builds the plugin and copies it to <target>/.opencode/plugin/campaign-vault.js.
    Optionally creates/updates opencode.json with the plugin registration.

.PARAMETER TargetDir
    Target campaign directory (must exist).

.EXAMPLE
    .\scripts\install-opencode-plugin.ps1 ~/my-campaign
#>
param(
    [Parameter(Position = 0, Mandatory = $true)]
    [string]$TargetDir
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$PluginSrcDir = Join-Path $RepoRoot "opencode-plugin"

if (-not (Test-Path $PluginSrcDir)) {
    throw "Plugin source not found at $PluginSrcDir"
}

if (-not (Test-Path $TargetDir)) {
    throw "Target directory does not exist: $TargetDir"
}

Write-Host "Building plugin..."
Push-Location $PluginSrcDir
if (-not (Test-Path (Join-Path $PluginSrcDir "node_modules"))) {
    Write-Host "  installing dependencies..."
    npm install
}
Write-Host "  building..."
npm run build
Pop-Location

$pluginDestDir = Join-Path $TargetDir ".opencode\plugin"
New-Item -ItemType Directory -Force -Path $pluginDestDir | Out-Null
Copy-Item (Join-Path $PluginSrcDir "dist\index.js") (Join-Path $pluginDestDir "campaign-vault.js") -Force
Write-Host "Plugin installed to $pluginDestDir\campaign-vault.js"

$opencodeJsonPath = Join-Path $TargetDir "opencode.json"
if (-not (Test-Path $opencodeJsonPath)) {
    $json = @{
        mcp = @{
            "campaign-vault" = @{
                type = "remote"
                url = "http://localhost:5275"
                enabled = $true
            }
        }
        plugin = @("file://.opencode/plugin/campaign-vault.js")
    } | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText($opencodeJsonPath, $json, [System.Text.UTF8Encoding]::new($false))
    Write-Host "Created $opencodeJsonPath with MCP server + plugin registration"
} else {
    Write-Host "opencode.json already exists at $opencodeJsonPath"
    Write-Host "Ensure these are in your opencode.json:"
    Write-Host '  "mcp": { "campaign-vault": { "type": "remote", "url": "http://localhost:5275", "enabled": true } }'
    Write-Host '  "plugin": ["file://.opencode/plugin/campaign-vault.js"]'
}

Write-Host "Done. Plugin installed to $TargetDir\.opencode\plugin\campaign-vault.js"