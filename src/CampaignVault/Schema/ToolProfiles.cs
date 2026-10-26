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
    /// <summary>
    /// Tools the game client calls itself and the model never needs (the character builder: the model only drafts
    /// builder JSON). Kept out of every tools/list so they cost the model nothing; still callable on "/" and "/build".
    /// </summary>
    public static readonly IReadOnlySet<string> ClientOnlyTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "character_builder", "character_level_up",
    };

    /// <summary>The tools a tools/list shows: everything served except <see cref="ClientOnlyTools"/>.</summary>
    public static List<Tool> Listed(IEnumerable<Tool> tools) =>
        [.. tools.Where(t => !ClientOnlyTools.Contains(t.Name))];

    public static readonly IReadOnlySet<string> PlayTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "take_turn", "get_entity", "search_world", "recall_history", "combat", "advance_world",
        "start_session", "end_session", "lookup",
        // No world_build here: mid-play seeding is a take_turn change (character_create, item_create,
        // location_update with a new id, rumor_create), so a table can switch off the /build connector
        // once the campaign is seeded and lose nothing.
    };

    public static readonly IReadOnlySet<string> BuildTools = new HashSet<string>(
        [
            "create_campaign", "list_campaigns", "delete_campaign", "get_config",
            "start_campaign_onboarding", "submit_onboarding_answer", "finalize_campaign_onboarding",
            "world_build", "get_entity", "search_world", "lookup",
            .. ClientOnlyTools,
        ],
        StringComparer.Ordinal);

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

            filtered.Add(tool);
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
}
