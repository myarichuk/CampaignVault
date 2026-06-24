namespace CampaignVault.Tools;

/// <summary>
/// Canonical list of supported <c>commit</c> $type discriminators — single source for tool descriptions and get_help.
/// </summary>
internal static class CommitTypesReference
{
    internal const string SupportedTypesList =
        "hp, item, item_update, status, statusremove, event, rumor, relationship, engagement_relation (legacy alias: spatial_relation), spatial_position, need, attribute, mood, activity, ruleset_action, level_up, location_create, location_update, character_create, character_update, system_stats, knowledge_update, schedule_change, item_create, travel, rest, scene_interrupt_check, faction_create, faction_reputation, faction_state, quest_create, quest_progress";

    internal const string SupportedTypesBullet =
        $"Supported `$type`s: {SupportedTypesList}.";
}