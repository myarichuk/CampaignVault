<#
.SYNOPSIS
  Sets up an opencode environment for a Campaign Vault campaign.

.DESCRIPTION
  - Extracts the fenced system-prompt block from recommended-system-prompt.md,
    fills in campaign-specific placeholders, and writes it to <target>/AGENTS.md
  - Copies the dnd-* skills into <target>/.opencode/skills/<name>/SKILL.md
  - Registers (or prints) the Campaign Vault MCP server entry for opencode.json

.PARAMETER TargetDir
  Campaign directory to set up (created if missing).

.PARAMETER Slug
  Campaign slug (campaignName).

.PARAMETER Ruleset
  Dnd5e or Pf2e (default Dnd5e).

.PARAMETER Roster
  PC roster string, e.g. "chars/abdel - Abdel, chars/nia - Nia".

.PARAMETER McpPort
  Port the Campaign Vault MCP server listens on (default 5275).

.PARAMETER Force
  Overwrite an existing AGENTS.md without backing it up.
#>
param(
    [Parameter(Position = 0)]
    [string]$TargetDir,
    [string]$Slug,
    [string]$Ruleset,
    [string]$Roster,
    [string]$McpPort = "5275",
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$PromptSrc = Join-Path $RepoRoot "recommended-system-prompt.opencode.md"
if (-not (Test-Path $PromptSrc)) {
    Write-Warning "recommended-system-prompt.opencode.md not found - falling back to the generic recommended-system-prompt.md (it won't mention the campaign-vault plugin's mechanical enforcement)."
    $PromptSrc = Join-Path $RepoRoot "recommended-system-prompt.md"
}
$SkillsSrc = Join-Path $RepoRoot "claude_skills"
$PluginSrcDir = Join-Path $RepoRoot "opencode-plugin"

if (-not $TargetDir) {
    $TargetDir = Read-Host "Target campaign directory (will be created if missing)"
}
if (-not $TargetDir) { throw "A target directory is required." }
New-Item -ItemType Directory -Force -Path $TargetDir | Out-Null
$TargetDir = (Resolve-Path $TargetDir).Path

if (-not $Slug) {
    $Slug = Read-Host "Campaign slug (campaignName, e.g. my-campaign)"
}
if (-not $Ruleset) {
    $Ruleset = Read-Host "Ruleset [Dnd5e|Pf2e] (default Dnd5e)"
    if (-not $Ruleset) { $Ruleset = "Dnd5e" }
}
if (-not $Roster) {
    $Roster = Read-Host "PC roster, e.g. 'chars/abdel - Abdel, chars/nia - Nia'"
}
if (-not $Slug) { throw "A campaign slug is required." }
if (-not $Roster) { throw "A PC roster is required." }

if (-not (Test-Path $PromptSrc)) {
    throw "Cannot find $PromptSrc - is this script still inside the CampaignVault repo?"
}

Write-Host "== Writing AGENTS.md =="

$lines = Get-Content -Path $PromptSrc -Encoding UTF8
$inFence = $false
$fenceLines = New-Object System.Collections.Generic.List[string]
foreach ($line in $lines) {
    if ($line -eq '```text') { $inFence = $true; continue }
    if ($inFence -and $line -eq '```') { $inFence = $false; continue }
    if ($inFence) { $fenceLines.Add($line) }
}

$campaignLine = "**CAMPAIGN:** campaignName=`"$Slug`" — always use this exact value on every campaign-scoped call, never ask the player or re-derive it. PC roster: $Roster — use these ids as characterId on their checks/actions. Ruleset: $Ruleset."

for ($i = 0; $i -lt $fenceLines.Count; $i++) {
    if ($fenceLines[$i] -match '^\*\*CAMPAIGN:\*\* campaignName=') {
        $fenceLines[$i] = $campaignLine
    }
}

$agentsPath = Join-Path $TargetDir "AGENTS.md"
if ((Test-Path $agentsPath) -and -not $Force) {
    $backup = "$agentsPath.bak.$(Get-Date -Format yyyyMMddHHmmss)"
    Copy-Item $agentsPath $backup
    Write-Host "  existing AGENTS.md backed up to $(Split-Path -Leaf $backup)"
}

$content = [string]::Join("`n", $fenceLines)
# Force UTF-8 without BOM; Set-Content on Windows PowerShell 5.1 defaults to UTF-16LE
# which corrupts the em dashes in the CAMPAIGN line.
[System.IO.File]::WriteAllText($agentsPath, $content, [System.Text.UTF8Encoding]::new($false))
Write-Host "  wrote $agentsPath"

Write-Host "== Copying skills =="
$skillsDest = Join-Path $TargetDir ".opencode\skills"
New-Item -ItemType Directory -Force -Path $skillsDest | Out-Null
Get-ChildItem -Path $SkillsSrc -Directory -Filter "dnd-*" | ForEach-Object {
    $dest = Join-Path $skillsDest $_.Name
    if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
    Copy-Item -Recurse $_.FullName $dest
    Write-Host "  $($_.Name)"
}
Write-Host "  (note: opencode also reads .claude/skills/ natively - this copy is convenience, not required)"

Write-Host "== Building and installing the campaign-vault plugin =="
$pluginDestRel = ".opencode/plugin/campaign-vault.js"
if (Test-Path $PluginSrcDir) {
    if (-not (Test-Path (Join-Path $PluginSrcDir "node_modules"))) {
        Write-Host "  installing plugin dependencies (npm install)..."
        Push-Location $PluginSrcDir
        npm install
        Pop-Location
    }
    Write-Host "  building plugin (npm run build)..."
    Push-Location $PluginSrcDir
    npm run build
    Pop-Location
    $pluginDest = Join-Path $TargetDir ($pluginDestRel -replace '/', [System.IO.Path]::DirectorySeparatorChar)
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $pluginDest) | Out-Null
    Copy-Item (Join-Path $PluginSrcDir "dist\index.js") $pluginDest -Force
    Write-Host "  copied dist/index.js -> $pluginDest"
} else {
    Write-Warning "  $PluginSrcDir not found - skipping plugin build/install. opencode.json will not reference a plugin."
    $pluginDestRel = ""
}

Write-Host "== MCP server + plugin registration =="
$opencodeJsonPath = Join-Path $TargetDir "opencode.json"
$mcpSnippet = @"
  "mcp": {
    "campaign-vault": {
      "type": "remote",
      "url": "http://localhost:$McpPort",
      "enabled": true
    }
  }
"@
$pluginSnippet = ""
if ($pluginDestRel) {
    $pluginSnippet = "  `"plugin`": [`"file://$pluginDestRel`"]"
}

if (Test-Path $opencodeJsonPath) {
    Write-Host "  opencode.json already exists at $opencodeJsonPath - not overwriting."
    Write-Host "  Add/merge these blocks manually (adjust if you already have `"mcp`"/`"plugin`" keys):"
    Write-Host $mcpSnippet
    if ($pluginSnippet) { Write-Host $pluginSnippet }
} else {
    if ($pluginSnippet) {
        $full = "{`n$mcpSnippet,`n$pluginSnippet`n}`n"
    } else {
        $full = "{`n$mcpSnippet`n}`n"
    }
    [System.IO.File]::WriteAllText($opencodeJsonPath, $full, [System.Text.UTF8Encoding]::new($false))
    Write-Host "  wrote $opencodeJsonPath"
}
Write-Host "  NOTE: this assumes the Campaign Vault MCP server (this repo, MCP_PORT=$McpPort) is already running via 'dotnet run' - start it separately."

Write-Host ""
Write-Host "Done. Campaign '$Slug' wired up in $TargetDir."
