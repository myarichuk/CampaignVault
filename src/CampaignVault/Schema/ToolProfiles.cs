using System.Runtime.CompilerServices;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CampaignVault.Schema;

/// <summary>Which tool subset an MCP route serves.</summary>
public enum ToolProfile
{
    All,
    Play,
    Build
}

/// <summary>
/// Tool definitions ride along on every model call, so a connector that only plays (or only builds)
/// shouldn't pay for the other half. "/" keeps every tool; "/play" and "/build" serve a subset, and a
/// call to a tool outside the subset fails as unknown.
/// </summary>
internal static class ToolProfiles
{
    public static readonly IReadOnlySet<string> PlayTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "take_turn", "get_entity", "search_world", "recall_history", "combat", "advance_world",
        "start_session", "end_session", "lookup",
        // Mid-play seeding: a name that isn't in the world yet has to be world_built before take_turn uses it.
        "world_build",
    };

    public static readonly IReadOnlySet<string> BuildTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "create_campaign", "list_campaigns", "get_config",
        "start_campaign_onboarding", "submit_onboarding_answer", "finalize_campaign_onboarding",
        "world_build", "get_entity", "search_world", "lookup",
    };

    /// <summary>Every route MapMcp serves: "/" (all tools), "/play", "/build".</summary>
    public static readonly string[] Routes = ["/", "/play", "/build"];

    public static bool IsMcpRoute(string? path)
    {
        if (path is null)
        {
            return false;
        }

        var trimmed = path.TrimEnd('/');
        var normalized = trimmed.Length == 0 ? "/" : trimmed;
        return Array.Exists(Routes, r => string.Equals(r, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static ToolProfile FromPath(string? path) =>
        path?.Trim('/').ToLowerInvariant() switch
        {
            "play" => ToolProfile.Play,
            "build" => ToolProfile.Build,
            _ => ToolProfile.All
        };

    public static McpServerPrimitiveCollection<McpServerTool>? Filter(
        McpServerPrimitiveCollection<McpServerTool>? tools, ToolProfile profile)
    {
        if (tools is null || profile == ToolProfile.All)
        {
            return tools;
        }

        var names = profile == ToolProfile.Play ? PlayTools : BuildTools;
        var filtered = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var tool in tools)
        {
            if (!names.Contains(tool.ProtocolTool.Name))
            {
                continue;
            }

            filtered.Add(profile == ToolProfile.Play && tool.ProtocolTool.Name == "world_build"
                ? PlayWorldBuild.GetValue(tool, inner => new SlimTool(inner, PlayWorldBuildDescription, PlayWorldBuildSchema))
                : tool);
        }

        return filtered;
    }

    /// <summary>
    /// Error text for a call to a tool this connector doesn't serve. Names the connector that has it and
    /// tells the model not to go looking: searching for tools re-sends the whole catalog.
    /// </summary>
    public static string UnknownToolMessage(string toolName, IReadOnlyCollection<string> available)
    {
        var here = available.Contains("take_turn") && !available.Contains("create_campaign") ? "/play"
            : available.Contains("create_campaign") && !available.Contains("take_turn") ? "/build"
            : "this connector";
        var elsewhere = PlayTools.Contains(toolName) && here != "/play" ? "; it is on /play (live play)"
            : BuildTools.Contains(toolName) && here != "/build" ? "; it is on /build (campaign setup)"
            : "";
        return $"unknown tool '{toolName}' on {here}{elsewhere}. Tell the user; don't search for tools. " +
               $"This connector: {string.Join(", ", available.Order(StringComparer.Ordinal))}.";
    }

    // Mid-play seeding is a handful of kinds with a few fields each. The full per-kind $defs stay on "/" and
    // "/build"; /play gets this stub, and the dnd-world-building skill or lookup kind=help carries the rest.
    // The call itself goes to the same tool, so nothing the stub leaves out is rejected.
    private const string PlayWorldBuildDescription =
        "Seed entities mid-play, before naming them in narration (a new NPC, shop, room, item). One atomic batch, " +
        "max 100. Items carry holderId (equipment is an items[] entry, not a character field); combat-capable NPCs " +
        "need systemStats; locations take parentLocationId and exits. Examples: lookup kind=help topic=world-building.";

    private static readonly JsonElement PlayWorldBuildSchema = JsonDocument.Parse("""
        {"type":"object","properties":{
          "batch":{"type":"object","description":"Arrays by kind: locations, characters, items, factions, quests, plotThreads, creatures, lore, rumors, worldEvents. Each entry needs id and name (quests/lore: title; rumors: subject)."},
          "campaignName":{"type":"string","description":"Campaign slug."}},
         "required":["batch","campaignName"]}
        """).RootElement.Clone();

    private static readonly ConditionalWeakTable<McpServerTool, McpServerTool> PlayWorldBuild = new();

    /// <summary>A tool served under a different description/schema on one connector; calls go to the original.</summary>
    private sealed class SlimTool(McpServerTool inner, string description, JsonElement inputSchema) : DelegatingMcpServerTool(inner)
    {
        private readonly Tool _protocolTool = new()
        {
            Name = inner.ProtocolTool.Name,
            Title = inner.ProtocolTool.Title,
            Description = description,
            InputSchema = inputSchema,
            OutputSchema = inner.ProtocolTool.OutputSchema,
            Annotations = inner.ProtocolTool.Annotations,
        };

        public override Tool ProtocolTool => _protocolTool;
    }
}
