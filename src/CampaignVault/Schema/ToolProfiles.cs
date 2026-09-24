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
            if (names.Contains(tool.ProtocolTool.Name))
            {
                filtered.Add(tool);
            }
        }

        return filtered;
    }
}
